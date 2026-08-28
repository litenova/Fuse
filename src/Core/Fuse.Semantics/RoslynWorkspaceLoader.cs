using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace Fuse.Semantics;

/// <summary>
///     Loads a discovered .NET workspace through MSBuild and Roslyn, producing compilations for semantic
///     analysis. Falls back cleanly to syntax-only when MSBuild is unavailable or loading fails.
/// </summary>
/// <remarks>
///     MSBuild is registered exactly once per process through a shared gate; registration must happen before
///     any MSBuild type is used. Loading depends on a resolvable SDK at runtime, so a
///     self-contained publish without an SDK (or an unrestored project) yields
///     <see cref="RoslynWorkspaceSnapshot.SemanticLoadSucceeded" /> false with a diagnostic rather than
///     throwing; the caller then indexes at the syntax level.
/// </remarks>
public sealed class RoslynWorkspaceLoader
{
    /// <summary>
    ///     The per-project reason a project loaded with a clean compilation. The only outcome that counts as
    ///     fully loaded for the oracle tier.
    /// </summary>
    public const string CleanLoadReason = "loaded";

    /// <summary>
    ///     The per-project reason a project loaded but whose compilation carries errors. Such a project is
    ///     graph-grade (retrieval only), not oracle-grade; the compiler-backed oracle tools abstain for it.
    /// </summary>
    public const string LoadsWithErrorsReason = "loaded with compile errors (graph-grade, not oracle-grade)";

    private readonly ILogger<RoslynWorkspaceLoader>? _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RoslynWorkspaceLoader" /> class.
    /// </summary>
    /// <param name="logger">An optional logger for load diagnostics.</param>
    public RoslynWorkspaceLoader(ILogger<RoslynWorkspaceLoader>? logger = null) => _logger = logger;

    /// <summary>
    ///     Loads the workspace described by a discovery result.
    /// </summary>
    /// <param name="discovery">The discovery result identifying the solution or projects.</param>
    /// <param name="cancellationToken">A token to cancel the load.</param>
    /// <returns>
    ///     A snapshot with compilations when semantic loading succeeded, or a failed snapshot with diagnostics
    ///     for the caller to fall back to syntax-only indexing.
    /// </returns>
    public async Task<RoslynWorkspaceSnapshot> LoadAsync(
        WorkspaceDiscoveryResult discovery,
        CancellationToken cancellationToken)
    {
        if (discovery.Kind == WorkspaceKind.SyntaxOnly)
        {
            return new RoslynWorkspaceSnapshot(
                SemanticLoadSucceeded: false,
                Projects: [],
                Diagnostics: [new DiagnosticRecord(DiagnosticSeverity.Info, "syntax-only", "No solution or project found; indexing at the syntax level.")],
                ProjectReports: []);
        }

        if (!TryRegisterLocator(out var locatorDiagnostic))
            return new RoslynWorkspaceSnapshot(false, [], [locatorDiagnostic!], []);

        var diagnostics = new List<DiagnosticRecord>();
        try
        {
            return await LoadCoreAsync(discovery, diagnostics, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "MSBuild workspace load failed; falling back to syntax indexing.");
            diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error, "msbuild-load-failed",
                $"MSBuild workspace load failed: {ex.Message}. Falling back to syntax indexing."));
            return new RoslynWorkspaceSnapshot(false, [], diagnostics, []);
        }
    }

    private async Task<RoslynWorkspaceSnapshot> LoadCoreAsync(
        WorkspaceDiscoveryResult discovery,
        List<DiagnosticRecord> diagnostics,
        CancellationToken cancellationToken)
    {
        using var workspace = MSBuildWorkspace.Create();
        // MSBuild design-time build problems surface here rather than as exceptions; record them as warnings.
        _ = workspace.RegisterWorkspaceFailedHandler(args =>
            diagnostics.Add(new DiagnosticRecord(DiagnosticSeverity.Warning, "msbuild-diagnostic", args.Diagnostic.Message)));

        IReadOnlyList<Project> projects;
        if (discovery is { Kind: WorkspaceKind.Solution, SolutionPath: { } solutionPath })
        {
            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
            projects = solution.Projects.ToList();
        }
        else
        {
            var loaded = new List<Project>();
            foreach (var projectPath in discovery.ProjectPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
                loaded.Add(project);
            }

            projects = loaded;
        }

        var (loadedProjects, projectReports) = await BuildProjectReportsAsync(projects, diagnostics, cancellationToken);

        var succeeded = loadedProjects.Count > 0;
        if (!succeeded)
        {
            diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error, "no-projects-loaded",
                "No projects loaded with a compilation; falling back to syntax indexing."));
        }

        return new RoslynWorkspaceSnapshot(succeeded, loadedProjects, diagnostics, projectReports);
    }

    /// <summary>
    ///     Builds a load snapshot from an already-loaded Roslyn <see cref="Solution" /> (R42), so
    ///     <c>fuse doctor</c>'s live diagnosis can reuse a warm, daemon-held solution instead of re-opening an
    ///     <see cref="MSBuildWorkspace" />. The per-project outcome (loaded, loaded-with-errors, no-compilation)
    ///     is computed exactly as the fresh load computes it, so the reported tier and reasons are identical.
    /// </summary>
    /// <param name="solution">The already-loaded solution (from <see cref="WarmSolutionCache" />).</param>
    /// <param name="loadFailures">The genuine MSBuild load failures observed when the solution was opened.</param>
    /// <param name="cancellationToken">A token to cancel the diagnosis.</param>
    /// <returns>The snapshot with per-project reports, equivalent to a fresh load of the same solution.</returns>
    public static async Task<RoslynWorkspaceSnapshot> SnapshotFromSolutionAsync(
        Solution solution, IReadOnlyList<string> loadFailures, CancellationToken cancellationToken)
    {
        var diagnostics = new List<DiagnosticRecord>();
        foreach (var failure in loadFailures)
            diagnostics.Add(new DiagnosticRecord(DiagnosticSeverity.Warning, "msbuild-diagnostic", failure));

        var (loadedProjects, projectReports) = await BuildProjectReportsAsync(solution.Projects, diagnostics, cancellationToken);
        var succeeded = loadedProjects.Count > 0;
        if (!succeeded)
        {
            diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error, "no-projects-loaded",
                "No projects loaded with a compilation; falling back to syntax indexing."));
        }

        return new RoslynWorkspaceSnapshot(succeeded, loadedProjects, diagnostics, projectReports);
    }

    // The per-project loop shared by the fresh load (LoadCoreAsync) and the warm-solution snapshot: compile each
    // project and record its outcome (loaded, loaded-with-errors -> graph-grade, or no-compilation), so the doctor
    // tier and per-project reasons are computed one way regardless of which load produced the solution.
    private static async Task<(List<LoadedProject> Loaded, List<ProjectLoadReport> Reports)> BuildProjectReportsAsync(
        IEnumerable<Project> projects, List<DiagnosticRecord> diagnostics, CancellationToken cancellationToken)
    {
        var loadedProjects = new List<LoadedProject>();
        var projectReports = new List<ProjectLoadReport>();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            var filePath = project.FilePath ?? project.Name;
            if (compilation is null)
            {
                const string reason = "no compilation (project unrestored, SDK mismatch, or a build error)";
                diagnostics.Add(new DiagnosticRecord(
                    DiagnosticSeverity.Warning, "no-compilation",
                    $"Project produced no compilation: {project.Name}", project.FilePath));
                projectReports.Add(new ProjectLoadReport(project.Name, filePath, Loaded: false, reason));
                continue;
            }

            // Per-project degradation: a project that loaded but whose compilation is missing metadata references
            // (an approximate compilation) is graph-grade, not oracle-grade. We record it as loaded but note the
            // approximation so fuse doctor and the oracle availability contract can distinguish the tiers.
            var hasErrors = compilation.GetDiagnostics(cancellationToken)
                .Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            var report = hasErrors ? LoadsWithErrorsReason : CleanLoadReason;
            loadedProjects.Add(new LoadedProject(
                Name: project.Name,
                FilePath: filePath,
                AssemblyName: project.AssemblyName,
                Compilation: compilation));
            projectReports.Add(new ProjectLoadReport(project.Name, filePath, Loaded: true, report));
        }

        return (loadedProjects, projectReports);
    }

    // Registers MSBuild once per process. Returns false with a diagnostic when no SDK/MSBuild is
    // resolvable (for example a self-contained publish without an SDK), so the caller falls back to syntax mode.
    private bool TryRegisterLocator(out DiagnosticRecord? diagnostic)
    {
        diagnostic = null;
        try
        {
            MsBuildLocatorRegistration.EnsureRegistered();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MSBuild SDK not found; semantic loading unavailable.");
            diagnostic = new DiagnosticRecord(
                DiagnosticSeverity.Error, "msbuild-unavailable",
                $"No MSBuild/SDK found ({ex.Message}); indexing at the syntax level.");
            return false;
        }
    }
}

/// <summary>
///     A project loaded by <see cref="RoslynWorkspaceLoader" /> together with its compilation.
/// </summary>
/// <param name="Name">The project name.</param>
/// <param name="FilePath">The project file path.</param>
/// <param name="AssemblyName">The output assembly name.</param>
/// <param name="Compilation">The Roslyn compilation, providing semantic models and symbols.</param>
public sealed record LoadedProject(
    string Name,
    string FilePath,
    string? AssemblyName,
    Compilation Compilation);

/// <summary>
///     The result of loading a workspace through MSBuild and Roslyn.
/// </summary>
/// <param name="SemanticLoadSucceeded">Whether at least one project loaded with a compilation.</param>
/// <param name="Projects">The loaded projects and their compilations; empty on failure.</param>
/// <param name="Diagnostics">Diagnostics gathered during loading.</param>
/// <param name="ProjectReports">The per-project compiler-load outcomes.</param>
public sealed record RoslynWorkspaceSnapshot(
    bool SemanticLoadSucceeded,
    IReadOnlyList<LoadedProject> Projects,
    IReadOnlyList<DiagnosticRecord> Diagnostics,
    IReadOnlyList<ProjectLoadReport> ProjectReports);

/// <summary>
///     The per-project load outcome, surfaced by <c>fuse doctor</c> so a downgrade names its concrete reason
///     (unrestored, SDK mismatch, build error) per project rather than as a single opaque solution-level failure.
/// </summary>
/// <param name="Name">The project name.</param>
/// <param name="FilePath">The project file path.</param>
/// <param name="Loaded">Whether the project produced a compilation.</param>
/// <param name="Reason">The concrete outcome: loaded, loaded-with-errors (graph-grade), or why it did not load.</param>
public sealed record ProjectLoadReport(string Name, string FilePath, bool Loaded, string Reason);
