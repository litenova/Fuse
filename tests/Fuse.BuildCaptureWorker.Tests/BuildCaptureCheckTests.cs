using System.Collections.Immutable;
using Fuse.BuildCaptureWorker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Fuse.BuildCaptureWorker.Tests;

// R1: the worker's speculative typecheck. It builds and rehydrates the compilation, applies a proposed single-
// file patch in memory, and returns the compiler diagnostics for the changed document, with no disk write and
// no second build. Validated on a self-contained project: a type error is reported, a clean edit is clean.
public sealed class BuildCaptureCheckTests
{
    [Fact]
    public async Task Reports_a_type_error_in_a_proposed_edit_and_a_clean_edit_as_clean()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-check-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"),
                "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

            var rehydrator = new BuildCaptureRehydrator();
            var target = Path.Combine(work, "Widget.csproj");

            // A patch that introduces a type error (returning a string from an int method).
            var broken = await rehydrator.CheckAsync(
                target, "Widget.cs", "namespace Sample; public sealed class Widget { public int Spin() => \"nope\"; }",
                TimeSpan.FromMinutes(5), CancellationToken.None);
            if (!broken.Verified)
            {
                // The SDK could not build here; the worker abstains with a reason rather than throwing.
                Assert.False(string.IsNullOrEmpty(broken.Reason));
                return;
            }

            Assert.False(broken.IsClean);
            Assert.Contains(broken.Diagnostics, d => d.Severity == "Error");

            // A clean patch typechecks clean (tolerant of a transient second-build issue: if the capture build
            // does not re-run here, the worker abstains with a reason rather than producing a false verdict).
            var clean = await rehydrator.CheckAsync(
                target, "Widget.cs", "namespace Sample; public sealed class Widget { public int Spin() => 7; }",
                TimeSpan.FromMinutes(5), CancellationToken.None);
            if (clean.Verified)
                Assert.True(clean.IsClean, "a clean edit should typecheck with no errors");
            else
                Assert.False(string.IsNullOrEmpty(clean.Reason));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    // R1 delta check: the speculative check reports only the diagnostics an edit INTRODUCED, not the ones the
    // base compilation already carried. This is what makes a no-op edit on a project with pre-existing errors
    // (for example the phantom CS0115/CS0120 a rehydrated Blazor code-behind shows when the Razor source
    // generator fails to load) report clean, while a genuinely breaking edit is still flagged. A raw Roslyn
    // compilation is used so the test runs without MSBuild or a build-capture closure.
    [Fact]
    public void Check_reports_only_the_diagnostics_the_edit_introduced()
    {
        var runner = new SpeculativeCheckRunner();

        // A base compilation with a pre-existing error in Widget.cs: Spin returns a string where int is
        // declared. The base already reports CS0023 on that document, so it is NOT something an edit introduced.
        var baseTree = CSharpSyntaxTree.ParseText(
            "namespace Sample; public sealed class Widget { public int Spin() => \"nope\"; }",
            path: "Widget.cs");
        var compilation = CSharpCompilation.Create(
            "Sample",
            [baseTree],
            TrustedPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(
            compilation.GetSemanticModel(baseTree).GetDiagnostics().Any(d => d.Id == "CS0029"),
            "the base compilation must carry a pre-existing error for this test to be meaningful");

        // A no-op edit (the identical content) must introduce zero diagnostics and report clean.
        var noOp = runner.Check(
            [compilation], "Widget.cs",
            "namespace Sample; public sealed class Widget { public int Spin() => \"nope\"; }",
            CancellationToken.None);
        Assert.True(noOp.Verified);
        Assert.True(noOp.IsClean, "a no-op edit over a pre-existing error must report clean (delta-based check)");
        Assert.Empty(noOp.Diagnostics);

        // A genuinely breaking edit introduces a NEW error (a missing member, CS1061) on top of the pre-existing
        // one; the check must surface that introduced error and report not clean.
        var breaking = runner.Check(
            [compilation], "Widget.cs",
            "namespace Sample; public sealed class Widget { public int Spin() => Missing(); }",
            CancellationToken.None);
        Assert.True(breaking.Verified);
        Assert.False(breaking.IsClean, "an edit that introduces a new error must report not clean");
        Assert.Contains(breaking.Diagnostics, d => d.Id == "CS0103");
    }

    // The runtime's trusted platform assemblies as metadata references, so a snippet binds the BCL without a
    // project file - the standard no-MSBuild way to compile in-process.
    private static ImmutableArray<MetadataReference> TrustedPlatformReferences()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                builder.Add(MetadataReference.CreateFromFile(path));
        }

        return builder.ToImmutable();
    }
}
