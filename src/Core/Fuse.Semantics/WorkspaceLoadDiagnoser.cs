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

    internal WorkspaceLoadDiagnoser(
        DotNetWorkspaceDiscoverer discoverer,
        RoslynWorkspaceLoader loader,
        WarmSolutionCache warmSolutions)
    {
        _discoverer = discoverer;
        _loader = loader;
        _warmSolutions = warmSolutions;
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

        return BuildFromSnapshot(discovery, snapshot);
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
