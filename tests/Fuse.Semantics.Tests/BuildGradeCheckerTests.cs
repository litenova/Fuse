using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// T0 (Decision D11): the build-grade rung of the verification-grade ladder. When no oracle-grade substrate is
// available, fuse_check verifies a proposed edit by running the real dotnet build toolchain, scoped to the owning
// project, and parsing its diagnostics into the same CheckResult shape as a speculative check. These are the three
// classification tests the T0 gate names; they run the real SDK against a synthesized self-contained project (no
// package references, so no restore network access is needed) and never touch the working tree (the checker
// mirrors the project into a temp directory and builds there).
public sealed class BuildGradeCheckerTests
{
    private const string ProjectFile = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private static async Task<string> CreateProjectAsync()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-build-grade-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), ProjectFile);
        await File.WriteAllTextAsync(
            Path.Combine(work, "Widget.cs"),
            "namespace Sample; public sealed class Widget { public int Spin() => 42; }");
        return work;
    }

    private static void Cleanup(string work)
    {
        try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Known_good_edit_yields_build_grade_green()
    {
        var work = await CreateProjectAsync();
        try
        {
            var projects = new[] { Path.Combine(work, "Widget.csproj") };
            var result = await new BuildGradeChecker().CheckAsync(
                work, projects, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => 7; }",
                CancellationToken.None);

            Assert.Equal("build", result.Grade);
            Assert.True(result.Verified, $"expected a verified build-grade result, got: {result.Reason}");
            Assert.True(result.IsClean, "a well-formed edit should build clean at build-grade");
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public async Task Known_bad_edit_yields_build_grade_red_with_a_parsed_cs_id()
    {
        var work = await CreateProjectAsync();
        try
        {
            var projects = new[] { Path.Combine(work, "Widget.csproj") };
            // An undefined identifier in the method body: the compiler reports a CS diagnostic located in this file.
            var result = await new BuildGradeChecker().CheckAsync(
                work, projects, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => Missing; }",
                CancellationToken.None);

            Assert.Equal("build", result.Grade);
            Assert.True(result.Verified, $"expected a verified build-grade result, got: {result.Reason}");
            Assert.False(result.IsClean, "a broken edit should not build clean");
            var error = Assert.Single(result.Diagnostics, d => d.Severity == "Error");
            Assert.StartsWith("CS", error.Id);
            Assert.Equal("Widget.cs", error.FilePath);
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public async Task Build_that_exceeds_the_timeout_abstains_with_a_reason()
    {
        var work = await CreateProjectAsync();
        try
        {
            var projects = new[] { Path.Combine(work, "Widget.csproj") };
            // A 1 ms budget cannot complete a real build, so the checker must classify it as an abstention rather
            // than guess a verdict; a build-grade verify never blocks forever.
            var result = await new BuildGradeChecker(TimeSpan.FromMilliseconds(1)).CheckAsync(
                work, projects, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => 1; }",
                CancellationToken.None);

            Assert.Equal("abstain", result.Grade);
            Assert.False(result.Verified);
            Assert.NotNull(result.Reason);
            Assert.Contains("exceeded", result.Reason);
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public async Task Unattributable_file_abstains()
    {
        var work = await CreateProjectAsync();
        try
        {
            var projects = new[] { Path.Combine(work, "Widget.csproj") };
            // A file that lives under no discovered project cannot be built-verified; the checker abstains with a
            // named reason rather than silently building the wrong project.
            var result = await new BuildGradeChecker().CheckAsync(
                Path.GetTempPath(), projects, "totally-elsewhere/Loose.cs",
                "namespace X; public class Loose { }",
                CancellationToken.None);

            Assert.Equal("abstain", result.Grade);
            Assert.False(result.Verified);
            Assert.Contains("not under any discovered project", result.Reason);
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public async Task Linked_source_is_verified_against_its_explicit_owner()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-build-grade-linked", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "src", "App"));
        Directory.CreateDirectory(Path.Combine(work, "Shared"));
        try
        {
            var project = Path.Combine(work, "src", "App", "App.csproj");
            await File.WriteAllTextAsync(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="../../Shared/Shared.cs" Link="Shared.cs" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(work, "Shared", "Shared.cs"),
                "namespace Shared; public sealed class SharedType { public int Spin() => 42; }");
            var ownership = new ProjectOwnership(
                work,
                "Shared/Shared.cs",
                [project]);

            var result = await new BuildGradeChecker().CheckAsync(
                work,
                ownership,
                "Shared/Shared.cs",
                "namespace Shared; public sealed class SharedType { public int Spin() => Missing; }",
                CancellationToken.None);

            Assert.Equal("build", result.Grade);
            Assert.True(result.Verified, result.Reason);
            Assert.False(result.IsClean);
            var error = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == "Error");
            Assert.StartsWith("CS", error.Id);
            Assert.Equal("Shared/Shared.cs", error.FilePath);
        }
        finally
        {
            Cleanup(work);
        }
    }
}
