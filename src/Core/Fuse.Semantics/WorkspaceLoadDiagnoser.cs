using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     Loads a selected workspace for diagnostics and converts compiler outcomes into a stable load report.
/// </summary>
internal sealed class WorkspaceLoadDiagnoser
{
    private readonly DotNetWorkspaceDiscoverer _discoverer;
    private readonly RoslynWorkspaceLoader _loader;
    private readonly WarmSolutionCache _warmSolutions;
    private readonly BuildTierReconciler _reconciler;

    internal WorkspaceLoadDiagnoser(
        DotNetWorkspaceDiscoverer discoverer,
        RoslynWorkspaceLoader loader,
        WarmSolutionCache warmSolutions,
        IProcessRunner? processRunner = null)
    {
        _discoverer = discoverer;
        _loader = loader;
        _warmSolutions = warmSolutions;
        _reconciler = new BuildTierReconciler(processRunner ?? new OwnedProcessRunner());
    }

    internal async Task<LoadDiagnosis> DiagnoseAsync(string root, CancellationToken cancellationToken)
    {
        var discovery = await _discoverer.DiscoverAsync(root, cancellationToken);
        RoslynWorkspaceSnapshot snapshot;
        if (discovery is { Kind: WorkspaceKind.Solution, SolutionPath: { } solutionPath })
        {
            try
            {
                var cached = await _warmSolutions.OpenAsync(solutionPath, cancellationToken);
                snapshot = await RoslynWorkspaceLoader.SnapshotFromSolutionAsync(
                    cached.Solution, cached.LoadFailures, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                snapshot = await _loader.LoadAsync(discovery, cancellationToken);
            }
        }
        else
        {
            snapshot = await _loader.LoadAsync(discovery, cancellationToken);
        }

        // The in-process design-time load may be incomplete for some projects (for example a Razor/Blazor
        // project whose source generator did not load in-process), so its tier is reconciled with the real
        // toolchain before the diagnosis is reported: a project that builds clean with the SDK's correct
        // generator versions earns a clean load even when its in-process compilation carried phantom errors.
        snapshot = await ReconcileAsync(root, snapshot, cancellationToken);
        return BuildFromSnapshot(discovery, snapshot);
    }

    // Reconciles a load snapshot's per-project tier reports with the real toolchain (a scoped dotnet build of any
    // project whose in-process compilation reported errors), returning the snapshot with the promoted reports and
    // the reconciliation diagnostics merged in. A snapshot with no error-loaded projects is returned unchanged.
    internal async Task<RoslynWorkspaceSnapshot> ReconcileAsync(
        string root, RoslynWorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.ProjectReports.All(report =>
                !report.Loaded || report.Reason != RoslynWorkspaceLoader.LoadsWithErrorsReason))
        {
            return snapshot;
        }

        var (reports, reconciliationDiagnostics) =
            await _reconciler.ReconcileAsync(root, snapshot, cancellationToken);
        if (reconciliationDiagnostics.Count == 0)
        {
            return snapshot;
        }

        return snapshot with
        {
            ProjectReports = reports,
            Diagnostics = snapshot.Diagnostics.Concat(reconciliationDiagnostics).ToList()
        };
    }

    internal static string ComputeTier(bool semanticLoadSucceeded, int loaded, int total, bool anyErrors)
    {
        if (!semanticLoadSucceeded || loaded == 0)
            return "syntax";
        if (loaded < total || anyErrors)
            return "graph-grade (partial)";
        return "oracle-grade (all projects loaded clean)";
    }

    internal static LoadDiagnosis BuildFromSnapshot(
        WorkspaceDiscoveryResult discovery,
        RoslynWorkspaceSnapshot snapshot)
    {
        var loaded = snapshot.ProjectReports.Count(report => report.Loaded);
        var total = snapshot.ProjectReports.Count;
        var anyErrors = snapshot.ProjectReports.Any(report =>
            report.Loaded && report.Reason.Contains("error", StringComparison.OrdinalIgnoreCase));
        return new LoadDiagnosis(
            ComputeTier(snapshot.SemanticLoadSucceeded, loaded, total, anyErrors),
            loaded,
            total,
            snapshot.ProjectReports,
            snapshot.Diagnostics,
            DescribeSelectedSolution(discovery),
            discovery.SelectionNote);
    }

    // Syntax indexing deliberately does not select a compiler workspace. In particular, repositories with several
    // solution filters must still get a usable syntax index; ambiguity matters only when a compiler-backed request
    // explicitly asks Fuse to choose a target.
    internal static LoadDiagnosis BuildSyntaxFirst(RoslynWorkspaceSnapshot snapshot) =>
        new(
            "syntax",
            0,
            0,
            [],
            snapshot.Diagnostics,
            null,
            "compiler analysis has not been requested");

    internal static LoadDiagnosis BuildFromCapture(WorkspaceDiscoveryResult discovery, CaptureResult capture)
    {
        var reports = capture.Projects
            .Select(project => new ProjectLoadReport(
                project.Name,
                project.FilePath,
                Loaded: true,
                project.ErrorCount > 0 ? RoslynWorkspaceLoader.LoadsWithErrorsReason : RoslynWorkspaceLoader.CleanLoadReason))
            .ToList();
        var anyErrors = capture.Projects.Any(project => project.ErrorCount > 0);
        var diagnostics = new List<DiagnosticRecord>
        {
            new(DiagnosticSeverity.Info, "build-capture", $"Tier-1 build capture: {capture.Projects.Count} project(s)."),
        };
        return new LoadDiagnosis(
            ComputeTier(reports.Count > 0, reports.Count, reports.Count, anyErrors),
            reports.Count,
            reports.Count,
            reports,
            diagnostics,
            DescribeSelectedSolution(discovery),
            discovery.SelectionNote);
    }

    private static string? DescribeSelectedSolution(WorkspaceDiscoveryResult discovery) =>
        discovery.Kind == WorkspaceKind.Solution
            ? discovery.SolutionPath
            : discovery.Kind == WorkspaceKind.Projects ? $"{discovery.ProjectPaths.Count} project(s), no single solution" : null;
}
