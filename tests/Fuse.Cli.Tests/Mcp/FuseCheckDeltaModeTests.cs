using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// S2: fuse_check delta mode. With a session and no content it returns the diagnostics introduced or resolved
// since the persisted session baseline, using the resident workspace's whole-state diagnostics (no build). These
// drive an in-process fuse_check with a mutable stub resident provider and a real store fixture.
public sealed class FuseCheckDeltaModeTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();

    public void Dispose()
    {
        _provider.Dispose();
    }

    [Fact]
    public async Task Delta_mode_establishes_then_reports_an_introduced_diagnostic()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = NewWorkspace();
        try
        {
            var root = Path.GetFullPath(work);
            var stub = new MutableCurrentProvider(root, []);
            var runtime = FuseMcpRuntime.CreateIsolated(indexer, stub);

            // First call: no baseline yet, so the current (empty) set is established as the baseline.
            var established = await FuseTools.FuseCheckAsync(
                indexer, work, session: "s1", cancellationToken: CancellationToken.None, runtime: runtime);
            Assert.Contains("established", established);

            // The agent edits and introduces an error; the resident whole-state now reports it.
            stub.Current = [new CheckDiagnostic("CS1061", "Error", "'Widget' does not contain a definition for 'Nope'", "Widget.cs", 1)];

            var delta = await FuseTools.FuseCheckAsync(
                indexer, work, session: "s1", cancellationToken: CancellationToken.None, runtime: runtime);
            Assert.Contains("1 introduced", delta);
            Assert.Contains("CS1061", delta);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [Fact]
    public async Task Delta_mode_reports_a_resolved_diagnostic()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = NewWorkspace();
        try
        {
            var root = Path.GetFullPath(work);
            var stub = new MutableCurrentProvider(root,
                [new CheckDiagnostic("CS0246", "Error", "type X not found", "Widget.cs", 3)]);
            var runtime = FuseMcpRuntime.CreateIsolated(indexer, stub);

            await FuseTools.FuseCheckAsync(
                indexer, work, session: "s2", cancellationToken: CancellationToken.None, runtime: runtime); // establish with the error present
            stub.Current = []; // the edit fixed it

            var delta = await FuseTools.FuseCheckAsync(
                indexer, work, session: "s2", cancellationToken: CancellationToken.None, runtime: runtime);
            Assert.Contains("1 resolved", delta);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [Fact]
    public async Task Delta_mode_mark_green_resets_the_baseline()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = NewWorkspace();
        try
        {
            var root = Path.GetFullPath(work);
            var stub = new MutableCurrentProvider(root,
                [new CheckDiagnostic("CS1061", "Error", "no member", "Widget.cs", 1)]);
            var runtime = FuseMcpRuntime.CreateIsolated(indexer, stub);

            await FuseTools.FuseCheckAsync(
                indexer, work, session: "s3", cancellationToken: CancellationToken.None, runtime: runtime); // baseline = 1 error
            var reset = await FuseTools.FuseCheckAsync(
                indexer, work, session: "s3", markGreen: true, cancellationToken: CancellationToken.None, runtime: runtime);
            Assert.Contains("marked green", reset);

            // After mark-green the current set is the new baseline, so there is no delta.
            var delta = await FuseTools.FuseCheckAsync(
                indexer, work, session: "s3", cancellationToken: CancellationToken.None, runtime: runtime);
            Assert.Contains("0 introduced, 0 resolved", delta);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [Fact]
    public async Task Delta_mode_without_a_resident_workspace_abstains()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = NewWorkspace();
        try
        {
            var output = await FuseTools.FuseCheckAsync(indexer, work, session: "s4", cancellationToken: CancellationToken.None);
            Assert.Contains("abstain", output);
            Assert.Contains("FUSE_RESIDENT", output);
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static string NewWorkspace()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-check-delta-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        // Initialize a git repo so the store resolves to {work}/.fuse rather than the shared machine-wide ~/.fuse,
        // isolating this test's index and check-session baseline from other runs.
        RunGit(work, "init");
        File.WriteAllText(Path.Combine(work, "Widget.cs"),
            "namespace Sample; public sealed class Widget { public int Spin() => 42; }");
        return work;
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Git not on PATH: the store falls back to the machine-wide location; the test still functions.
        }
    }

    private static void TryDelete(string work)
    {
        try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // A resident provider whose whole-state diagnostics can be changed between calls to simulate the agent editing.
    private sealed class MutableCurrentProvider(string root, IReadOnlyList<CheckDiagnostic> current) : IResidentWorkspaceProvider
    {
        public IReadOnlyList<CheckDiagnostic> Current { get; set; } = current;

        public ResidentStatus? DescribeResident(string queried) =>
            Matches(queried) ? new ResidentStatus(1, "test") : null;

        public IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
            string queried, string relativeFilePath, string newContent, CancellationToken cancellationToken) =>
            Matches(queried) ? [] : null;

        public IReadOnlyList<CheckDiagnostic>? TryGetCurrentDiagnostics(string queried) =>
            Matches(queried) ? Current : null;

        private bool Matches(string queried) =>
            string.Equals(Path.GetFullPath(queried), root, StringComparison.OrdinalIgnoreCase);
    }
}
