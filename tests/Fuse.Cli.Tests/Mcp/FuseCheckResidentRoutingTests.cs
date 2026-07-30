using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// S1 step 2: fuse_check routes through the resident workspace first. When a live resident workspace serves the
// root it answers the oracle-grade check from the held compilation (no build-capture worker, no dotnet build).
// With the default null provider the routing is a no-op and the existing worker/build-grade ladder is unchanged.
// This test wires a stub resident provider and confirms fuse_check reports the resident diagnostics at oracle
// grade.
public sealed class FuseCheckResidentRoutingTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();

    public void Dispose()
    {
        _provider.Dispose();
    }

    [Fact]
    public async Task Check_reports_resident_oracle_diagnostics_when_a_workspace_is_live()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = Path.Combine(Path.GetTempPath(), "fuse-check-resident-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.Combine(work, ".git"));
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

            var root = Path.GetFullPath(work);
            var runtime = FuseMcpRuntime.CreateIsolated(indexer, new StubCheckProvider(root, [
                new CheckDiagnostic("CS1061", "Error", "'Widget' does not contain a definition for 'Nope'", "Widget.cs", 1),
            ]));

            var output = await FuseTools.FuseCheckAsync(
                indexer, work, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => Nope; }",
                cancellationToken: CancellationToken.None,
                runtime: runtime);

            Assert.Contains("verification grade: oracle", output);
            Assert.Contains("CS1061", output);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Check_reports_resident_clean_when_the_overlay_has_no_errors()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = Path.Combine(Path.GetTempPath(), "fuse-check-resident-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.Combine(work, ".git"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"),
                "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

            var root = Path.GetFullPath(work);
            var runtime = FuseMcpRuntime.CreateIsolated(indexer, new StubCheckProvider(root, []));

            var output = await FuseTools.FuseCheckAsync(
                indexer, work, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => 7; }",
                cancellationToken: CancellationToken.None,
                runtime: runtime);

            Assert.Contains("verification grade: oracle", output);
            Assert.Contains("clean", output);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class StubCheckProvider(string root, IReadOnlyList<CheckDiagnostic> diagnostics)
        : IResidentWorkspaceProvider
    {
        public ResidentStatus? DescribeResident(string queried) =>
            string.Equals(queried, root, StringComparison.OrdinalIgnoreCase) ? new ResidentStatus(1, "test") : null;

        public IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
            string queried, string relativeFilePath, string newContent, CancellationToken cancellationToken) =>
            string.Equals(queried, root, StringComparison.OrdinalIgnoreCase) ? diagnostics : null;

        public Task<IReadOnlyList<CheckDiagnostic>?> TryCheckOverlayAsync(
            string queried, string relativeFilePath, string newContent, bool includeAnalyzers, CancellationToken cancellationToken) =>
            Task.FromResult(string.Equals(queried, root, StringComparison.OrdinalIgnoreCase) ? (IReadOnlyList<CheckDiagnostic>?)diagnostics : null);
    }
}
