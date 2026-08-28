using System.Diagnostics;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// The load-tier reconciliation: a project whose in-process design-time compilation carried error-severity
// diagnostics (for example a Razor/Blazor project whose source generator did not load in-process, so its
// generated partial classes are missing and the compiler reports phantom errors the real build never has) is
// verified against a scoped real dotnet build. A clean real build promotes the project to a clean load; a
// failing, erroring, or unverifiable build keeps it graph-grade. The reconciler never over-promotes.
public sealed class BuildTierReconcilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-tier-reconcile", Guid.NewGuid().ToString("N"));

    public BuildTierReconcilerTests()
    {
        Directory.CreateDirectory(_root);
        // A real (if empty) root so the build-grade mirror succeeds and the fake process runner is reached.
        File.WriteAllText(Path.Combine(_root, "placeholder.txt"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Promotes_a_project_when_the_scoped_build_reports_zero_errors()
    {
        var runner = new CannedProcessRunner("Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n", exitCode: 0, timedOut: false);
        var snapshot = SnapshotWithErrorProject("MINT.Next.Blazor");
        var reconciler = new BuildTierReconciler(runner);

        var (reports, diagnostics) = await reconciler.ReconcileAsync(_root, snapshot, CancellationToken.None);

        var report = Assert.Single(reports);
        Assert.Equal(RoslynWorkspaceLoader.CleanLoadReason, report.Reason);
        Assert.True(report.Loaded);
        // The promotion is recorded as a load diagnostic so doctor can surface it.
        Assert.Contains(diagnostics, d => d.Code == "tier-reconciled");
    }

    [Fact]
    public async Task Keeps_graph_grade_when_the_scoped_build_reports_errors()
    {
        var runner = new CannedProcessRunner(
            "C:\\repo\\MINT.Next.Blazor\\Pages\\Home.razor.cs(33,22): error CS0115: no suitable method found to override\n",
            exitCode: 1,
            timedOut: false);
        var snapshot = SnapshotWithErrorProject("MINT.Next.Blazor");
        var reconciler = new BuildTierReconciler(runner);

        var (reports, diagnostics) = await reconciler.ReconcileAsync(_root, snapshot, CancellationToken.None);

        var report = Assert.Single(reports);
        Assert.Equal(RoslynWorkspaceLoader.LoadsWithErrorsReason, report.Reason);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Keeps_graph_grade_when_the_scoped_build_times_out()
    {
        var runner = new CannedProcessRunner(string.Empty, exitCode: null, timedOut: true);
        var snapshot = SnapshotWithErrorProject("MINT.Next.Blazor");
        var reconciler = new BuildTierReconciler(runner);

        var (reports, diagnostics) = await reconciler.ReconcileAsync(_root, snapshot, CancellationToken.None);

        var report = Assert.Single(reports);
        Assert.Equal(RoslynWorkspaceLoader.LoadsWithErrorsReason, report.Reason);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Leaves_a_clean_snapshot_unchanged()
    {
        var runner = new CannedProcessRunner("should not be reached\n", exitCode: 0, timedOut: false);
        var snapshot = new RoslynWorkspaceSnapshot(
            SemanticLoadSucceeded: true,
            Projects: [],
            Diagnostics: [],
            ProjectReports: [new ProjectLoadReport("Lib", "Lib.csproj", Loaded: true, RoslynWorkspaceLoader.CleanLoadReason)]);
        var reconciler = new BuildTierReconciler(runner);

        var (reports, diagnostics) = await reconciler.ReconcileAsync(_root, snapshot, CancellationToken.None);

        var report = Assert.Single(reports);
        Assert.Equal(RoslynWorkspaceLoader.CleanLoadReason, report.Reason);
        Assert.Empty(diagnostics);
        // A clean snapshot must not trigger a scoped build at all.
        Assert.False(runner.Reached, "a clean snapshot must not run a scoped build");
    }

    [Fact]
    public async Task Promotes_only_the_projects_that_build_clean()
    {
        // The Blazor project builds clean; the Broken project has errors. The scoped build preserves the
        // project file name in its temp mirror, so the runner can answer each project independently.
        var runner = new CannedProcessRunner(startInfo =>
            startInfo.ArgumentList.Any(a => a.Contains("Broken", StringComparison.OrdinalIgnoreCase))
                ? new ProcessExecutionResult(
                    Started: true,
                    ExitCode: 1,
                    TimedOut: false,
                    StandardOutput: "C:\\repo\\Broken\\X.cs(1,1): error CS0103: the name does not exist\n",
                    StandardError: string.Empty,
                    StartError: null)
                : new ProcessExecutionResult(
                    Started: true,
                    ExitCode: 0,
                    TimedOut: false,
                    StandardOutput: "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n",
                    StandardError: string.Empty,
                    StartError: null));
        var snapshot = new RoslynWorkspaceSnapshot(
            SemanticLoadSucceeded: true,
            Projects: [],
            Diagnostics: [],
            ProjectReports:
            [
                new ProjectLoadReport("MINT.Next.Blazor", "MINT.Next.Blazor.csproj", Loaded: true, RoslynWorkspaceLoader.LoadsWithErrorsReason),
                new ProjectLoadReport("Broken", "Broken.csproj", Loaded: true, RoslynWorkspaceLoader.LoadsWithErrorsReason)
            ]);
        var reconciler = new BuildTierReconciler(runner);

        var (reports, _) = await reconciler.ReconcileAsync(_root, snapshot, CancellationToken.None);

        Assert.Equal(2, reports.Count);
        // The tier decision is per project, not whole-solution: the clean build promotes only the Blazor project.
        Assert.Equal(RoslynWorkspaceLoader.CleanLoadReason, reports[0].Reason);
        Assert.Equal(RoslynWorkspaceLoader.LoadsWithErrorsReason, reports[1].Reason);
    }

    // A snapshot with one project that loaded with compile errors (the in-process Razor downgrade case).
    private static RoslynWorkspaceSnapshot SnapshotWithErrorProject(string name) =>
        new(
            SemanticLoadSucceeded: true,
            Projects: [],
            Diagnostics: [],
            ProjectReports: [new ProjectLoadReport(name, name + ".csproj", Loaded: true, RoslynWorkspaceLoader.LoadsWithErrorsReason)]);

    // A process runner that returns canned build output instead of launching a real dotnet build, so the
    // reconciler's tier decision is exercised deterministically.
    private sealed class CannedProcessRunner : IProcessRunner
    {
        private readonly string _output = string.Empty;
        private readonly int? _exitCode;
        private readonly bool _timedOut;
        private readonly Func<ProcessStartInfo, ProcessExecutionResult>? _byStartInfo;

        public CannedProcessRunner(string output, int? exitCode, bool timedOut)
        {
            _output = output;
            _exitCode = exitCode;
            _timedOut = timedOut;
            _byStartInfo = null;
        }

        public CannedProcessRunner(Func<ProcessStartInfo, ProcessExecutionResult> byStartInfo)
            => _byStartInfo = byStartInfo;

        public bool Reached { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(
            ProcessStartInfo startInfo,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Reached = true;
            return Task.FromResult(_byStartInfo is not null
                ? _byStartInfo(startInfo)
                : new ProcessExecutionResult(
                    Started: true,
                    ExitCode: _exitCode,
                    TimedOut: _timedOut,
                    StandardOutput: _output,
                    StandardError: string.Empty,
                    StartError: null));
        }
    }
}
