using Fuse.Indexing;
using Fuse.Semantics.Analyzers;

namespace Fuse.Semantics;

/// <summary>
///     The top-level workspace indexer: discovers the workspace, loads it through MSBuild/Roslyn, and writes
///     project records, files (linked to projects), symbols, chunks, and routes to the index. Falls back to
///     syntax-only indexing when semantic loading is unavailable.
/// </summary>
/// <remarks>
///     Symbols come from the semantic extractor (stable assembly-qualified ids) when the workspace loads
///     semantically, and from the syntax extractor otherwise. Chunks and routes are always produced from
///     syntax so full-text search works in both modes. The resulting mode (<c>semantic</c>, <c>partial</c>,
///     or <c>syntax</c>) is stored in the index metadata and surfaced through the store state.
/// </remarks>
public sealed class SemanticIndexer
{
    private readonly DotNetWorkspaceDiscoverer _discoverer;
    private readonly RoslynWorkspaceLoader _loader;
    private readonly SemanticGraphExtractor _semanticGraph;
    private readonly LanguageSyntaxProviderRegistry _syntaxProviders;
    private readonly BuildCaptureClient _buildCaptureClient;
    private readonly IndexFinalizer _finalizer = new();
    private readonly WorkspaceInventoryPlanner _inventory;
    private readonly SyntaxIndexStage _syntaxStage;
    private readonly DirtyFileReconciler _dirtyFileReconciler;
    // R42: the host-owned warm-solution cache lets a second doctor in a session skip the full MSBuild load.
    private readonly WarmSolutionCache _warmSolutions;

    // N4/C3 tier-1 build capture is default-ON: the oracle is the product. Opt out with FUSE_BUILD_CAPTURE=0
    // (or false/no/off); any other value, or unset, enables it. It still no-ops when no worker is discoverable
    // (the tool bundles one - see BuildCaptureClient.ResolveWorkerPath) or there is no build target, so a
    // deployment without the worker degrades cleanly to the MSBuildWorkspace and syntax tiers.
    private static readonly TimeSpan BuildCaptureTimeout = TimeSpan.FromMinutes(10);

    internal static bool BuildCaptureEnabled()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_BUILD_CAPTURE");
        if (value is null)
            return true;
        return !(value.Equals("0", StringComparison.Ordinal)
                 || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                 || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    // Runs the tier-1 build-capture worker for the discovered workspace. Returns the captured graph on success, or
    // null when capture is disabled, the worker is unavailable, there is no build target, or the build failed.
    private async Task<Fuse.Indexing.CaptureResult?> TryBuildCaptureAsync(
        WorkspaceDiscoveryResult discovery, string root, CancellationToken cancellationToken)
    {
        if (!BuildCaptureEnabled() || !_buildCaptureClient.IsAvailable)
            return null;
        var buildTarget = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (buildTarget is null)
            return null;
        // Pass the workspace root so the worker keys extracted symbol/node/route/DI/options file paths to it, matching
        // the root-relative files.normalized_path the store resolves foreign keys against. Without it the worker fell
        // back to each project's directory, producing project-relative paths that never resolved on a nested layout,
        // so every symbol was dropped and every node stored an unlinked file_id.
        var result = await _buildCaptureClient.CaptureAsync(buildTarget, BuildCaptureTimeout, cancellationToken, root);
        return result.Succeeded ? result : null;
    }

    /// <summary>
    ///     The index-store meta key that flags active compiler analysis after syntax data is available.
    ///     <c>"1"</c> means cross-file semantic graph extraction is running; <c>"0"</c> (or absent) means the
    ///     recorded mode is complete for the requested depth.
    /// </summary>
    public const string SemanticPendingMetaKey = "semantic_pending";

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticIndexer" /> class.
    /// </summary>
    /// <param name="discoverer">The workspace discoverer.</param>
    /// <param name="loader">The MSBuild/Roslyn workspace loader.</param>
    /// <param name="scanner">The file scanner.</param>
    /// <param name="semanticSymbols">The semantic symbol extractor.</param>
    /// <param name="syntaxSymbols">The syntax symbol and chunk extractor (used for chunks and as the fallback).</param>
    /// <param name="routeExtractor">The syntax route extractor.</param>
    /// <param name="hashService">The content hash service, used for project hashes.</param>
    /// <param name="analysisRunner">The semantic analyzer runner producing graph edges (semantic mode only).</param>
    /// <param name="warmSolutions">The host-owned cache for live MSBuild diagnostic loads.</param>
    /// <param name="buildCaptureClient">The host-owned build-capture process client.</param>
    public SemanticIndexer(
        DotNetWorkspaceDiscoverer discoverer,
        RoslynWorkspaceLoader loader,
        WorkspaceFileScanner scanner,
        SemanticSymbolExtractor semanticSymbols,
        SyntaxSymbolExtractor syntaxSymbols,
        SyntaxRouteExtractor routeExtractor,
        FileHashService hashService,
        SemanticAnalysisRunner analysisRunner,
        WarmSolutionCache? warmSolutions = null,
        BuildCaptureClient? buildCaptureClient = null)
    {
        _discoverer = discoverer;
        _loader = loader;
        _semanticGraph = new SemanticGraphExtractor(semanticSymbols, analysisRunner, hashService);
        _warmSolutions = warmSolutions ?? new WarmSolutionCache();
        _buildCaptureClient = buildCaptureClient ?? new BuildCaptureClient();
        // The syntax tier is provider-driven: C# behind the seam (unchanged behavior), plus a second-language
        // syntax spike. Built internally so the existing constructor and its callers are unaffected; a later
        // change can make the provider set injectable for an external language plugin.
        _syntaxProviders = new LanguageSyntaxProviderRegistry([new CSharpSyntaxProvider(syntaxSymbols), new PythonSyntaxProvider(), new JavaScriptSyntaxProvider()]);
        _inventory = new WorkspaceInventoryPlanner(scanner, _syntaxProviders);
        _syntaxStage = new SyntaxIndexStage(_syntaxProviders, syntaxSymbols, routeExtractor);
        _dirtyFileReconciler = new DirtyFileReconciler(_inventory, _syntaxStage, _finalizer);
    }

    /// <summary>
    ///     Indexes a workspace into the store.
    /// </summary>
    /// <param name="rootDirectory">The workspace root.</param>
    /// <param name="store">The index store to write to.</param>
    /// <param name="cancellationToken">A token to cancel the index.</param>
    /// <returns>A summary including the index mode, counts, and diagnostics.</returns>
    public async Task<SemanticIndexResult> IndexAsync(
        string rootDirectory,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        await WorkspaceIndexManifest.BeginBuildAsync(root, store, cancellationToken);
        var discovery = await _discoverer.DiscoverAsync(root, cancellationToken);
        var scan = await ScanFilesAsync(root, cancellationToken);
        var files = scan.Files;
        await store.ClearFileDataAsync(files.Select(file => file.NormalizedPath).ToArray(), cancellationToken);

        // Tier 1 (build capture, oracle-grade) when enabled and available: run the out-of-process worker, which
        // builds the repo and rehydrates the exact compilations, and write its graph bundle. Falls back to the
        // MSBuildWorkspace load (tier 2), which itself falls back to syntax (tier 3), on any capture failure.
        var capture = await TryBuildCaptureAsync(discovery, root, cancellationToken);
        SemanticIndexResult result;
        LoadDiagnosis diagnosis;
        if (capture is not null)
        {
            result = await IndexFromCaptureAsync(root, store, files, capture, cancellationToken);
            diagnosis = BuildDiagnosisFromCapture(discovery, capture);
        }
        else
        {
            var snapshot = await _loader.LoadAsync(discovery, cancellationToken);
            result = snapshot.SemanticLoadSucceeded
                ? await IndexSemanticAsync(root, store, files, snapshot, cancellationToken)
                : await _syntaxStage.IndexAllAsync(root, store, files, snapshot, cancellationToken);
            diagnosis = BuildDiagnosisFromSnapshot(discovery, snapshot);
        }

        await store.SetMetaAsync("index_mode", result.Mode, cancellationToken);
        // A full pass is the final word on the mode: clear any syntax-first pending flag a prior fast pass set.
        await store.SetMetaAsync(SemanticPendingMetaKey, "0", cancellationToken);
        // R43: stamp the per-project load diagnosis so doctor reports the tier from the warm index (no live load).
        await _finalizer.StampLoadDiagnosisAsync(store, diagnosis, cancellationToken);
        // Stamp the Fuse build that wrote this index so a later run on an incompatible upgrade rebuilds it.
        await store.SetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, FuseBuildInfo.Current, cancellationToken);
        // R22: stamp the extraction-contract version so index reuse is gated on what was extracted, not the product
        // version. Bump WorkspaceIndexSchema.ExtractionContractVersion in the same change as any extractor change.
        await store.SetMetaAsync(
            WorkspaceIndexStore.ExtractionVersionMetaKey,
            WorkspaceIndexSchema.ExtractionContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        await store.PruneFilesAsync(files.Select(file => file.NormalizedPath).ToArray(), cancellationToken);
        await store.SetMetaAsync(StaleAsOfMetaKey, "0", cancellationToken);
        await _finalizer.StampIntegrityAsync(store, store, cancellationToken); // R31: record the post-build integrity result.
        await _finalizer.StampSkippedFilesAsync(store, scan.Skipped, cancellationToken); // R35: record skipped files.
        await _finalizer.StampDetailLimitedFilesAsync(store, scan.DetailLimited, cancellationToken);

        await WorkspaceIndexManifest.CompleteAsync(root, store, files, cancellationToken);

        return result;
    }

    /// <summary>
    ///     Diagnoses the semantic load without writing the index: discovers the workspace, loads it through
    ///     MSBuild/Roslyn, and reports the achieved tier and the concrete per-project outcome. This is what
    ///     <c>fuse doctor</c> reports, so a downgrade names its reason per project (unrestored, SDK mismatch,
    ///     build error) rather than failing opaquely at the solution level.
    /// </summary>
    /// <param name="rootDirectory">The workspace root.</param>
    /// <param name="cancellationToken">A token to cancel the load.</param>
    /// <returns>The load diagnosis: the tier, per-project reports, and load diagnostics.</returns>
    public async Task<LoadDiagnosis> DiagnoseLoadAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        var discovery = await _discoverer.DiscoverAsync(root, cancellationToken);

        // R42: reuse the warm, daemon-held solution for the common single-solution repo, so a second doctor in a
        // session (or a doctor after a refactor) skips the full MSBuild load. A locator/open failure falls back to
        // the loader's graceful syntax/diagnostic handling; the multi-project and syntax-only kinds use the loader.
        RoslynWorkspaceSnapshot snapshot;
        if (discovery is { Kind: WorkspaceKind.Solution, SolutionPath: { } solutionPath })
        {
            try
            {
                var cached = await _warmSolutions.OpenAsync(solutionPath, cancellationToken);
                snapshot = await RoslynWorkspaceLoader.SnapshotFromSolutionAsync(cached.Solution, cached.LoadFailures, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                snapshot = await _loader.LoadAsync(discovery, cancellationToken);
            }
        }
        else
        {
            snapshot = await _loader.LoadAsync(discovery, cancellationToken);
        }

        return BuildDiagnosisFromSnapshot(discovery, snapshot);
    }

    // The tier from a project-report set: oracle requires every loaded project to be error-free; a project loaded
    // with compile errors is graph-grade (retrieval only), and any project that did not load at all drops the tier
    // further. Shared so the live diagnosis and the persisted-at-index-time diagnosis (R43) compute one tier.
    internal static string ComputeTier(bool semanticLoadSucceeded, int loaded, int total, bool anyErrors)
    {
        if (!semanticLoadSucceeded || loaded == 0)
            return "syntax";
        if (loaded < total || anyErrors)
            return "graph-grade (partial)";
        return "oracle-grade (all projects loaded clean)";
    }

    private static string? DescribeSelectedSolution(WorkspaceDiscoveryResult discovery) =>
        discovery.Kind == WorkspaceKind.Solution
            ? discovery.SolutionPath
            : discovery.Kind == WorkspaceKind.Projects ? $"{discovery.ProjectPaths.Count} project(s), no single solution" : null;

    // Builds the load diagnosis from an MSBuild/Roslyn load snapshot (the live doctor path and the persisted-at-
    // index-time diagnosis on the MSBuildWorkspace index path).
    internal static LoadDiagnosis BuildDiagnosisFromSnapshot(WorkspaceDiscoveryResult discovery, RoslynWorkspaceSnapshot snapshot)
    {
        var loaded = snapshot.ProjectReports.Count(p => p.Loaded);
        var total = snapshot.ProjectReports.Count;
        var anyErrors = snapshot.ProjectReports.Any(p => p.Loaded && p.Reason.Contains("error", StringComparison.OrdinalIgnoreCase));
        var tier = ComputeTier(snapshot.SemanticLoadSucceeded, loaded, total, anyErrors);
        return new LoadDiagnosis(
            tier, loaded, total, snapshot.ProjectReports, snapshot.Diagnostics, DescribeSelectedSolution(discovery), discovery.SelectionNote);
    }

    // Builds the load diagnosis from a tier-1 build capture (the default index path). Every captured project
    // produced a compilation (a project that failed to build does not rehydrate), so all are loaded; a project with
    // residual compile errors is graph-grade, matching the per-project reason strings the MSBuild loader produces.
    internal static LoadDiagnosis BuildDiagnosisFromCapture(WorkspaceDiscoveryResult discovery, Fuse.Indexing.CaptureResult capture)
    {
        var reports = capture.Projects
            .Select(p => new ProjectLoadReport(
                p.Name,
                p.FilePath,
                Loaded: true,
                p.ErrorCount > 0 ? "loaded with compile errors (graph-grade, not oracle-grade)" : "loaded"))
            .ToList();
        var anyErrors = capture.Projects.Any(p => p.ErrorCount > 0);
        var tier = ComputeTier(semanticLoadSucceeded: reports.Count > 0, loaded: reports.Count, total: reports.Count, anyErrors);
        var diagnostics = new List<DiagnosticRecord>
        {
            new(DiagnosticSeverity.Info, "build-capture", $"Tier-1 build capture: {capture.Projects.Count} project(s)."),
        };
        return new LoadDiagnosis(
            tier, reports.Count, reports.Count, reports, diagnostics, DescribeSelectedSolution(discovery), discovery.SelectionNote);
    }

    /// <summary>
    ///     Indexes the workspace at the syntax tier only, skipping the MSBuild/Roslyn load, so a first call
    ///     serves context in a few seconds instead of waiting for the full semantic load. Sets the index mode to
    ///     <c>syntax</c>. Compiler analysis starts only from an explicit semantic request.
    /// </summary>
    /// <param name="rootDirectory">The workspace root.</param>
    /// <param name="store">The index store to write to.</param>
    /// <param name="cancellationToken">A token to cancel the index.</param>
    /// <param name="progress">An optional synchronous observer for inventory and syntax-stage progress.</param>
    /// <returns>A syntax-tier index summary.</returns>
    /// <remarks>
    ///     The cold index time is dominated by the MSBuild evaluation, not the syntax extraction, so the
    ///     syntax-first pass produces a usable full-text and symbol index without loading MSBuild. Cross-file
    ///     semantic facts are added only by a requested compiler pass.
    /// </remarks>
    public async Task<SemanticIndexResult> IndexSyntaxFirstAsync(
        string rootDirectory,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken,
        IProgress<SemanticIndexProgress>? progress = null)
    {
        var root = Path.GetFullPath(rootDirectory);
        await WorkspaceIndexManifest.BeginBuildAsync(root, store, cancellationToken);
        progress?.Report(new SemanticIndexProgress(
            SemanticIndexStage.Inventory,
            0,
            null,
            "scanning repository inventory"));
        var scan = await ScanFilesAsync(root, cancellationToken);
        var files = scan.Files;
        progress?.Report(new SemanticIndexProgress(
            SemanticIndexStage.Inventory,
            files.Count,
            files.Count,
            "repository inventory complete"));
        var snapshot = new RoslynWorkspaceSnapshot(
            SemanticLoadSucceeded: false,
            Projects: [],
            Diagnostics: [new DiagnosticRecord(DiagnosticSeverity.Info, "syntax-first", "Syntax-tier index served first; run 'fuse index --semantic' to add compiler facts.")],
            ProjectReports: []);

        await store.ReplaceTfmAvailabilityAsync([], cancellationToken);
        var result = await _syntaxStage.IndexIncrementallyAsync(root, store, files, snapshot, cancellationToken, progress);
        await store.SetMetaAsync("index_mode", result.Mode, cancellationToken);
        // Syntax is now the completed default index depth. Compiler work starts only after an explicit semantic
        // request, so this store is not waiting for an automatic background upgrade.
        await store.SetMetaAsync(SemanticPendingMetaKey, "0", cancellationToken);
        // Stamp a syntax-tier diagnosis. Discovery is file based and does not load MSBuild.
        var discovery = await _discoverer.DiscoverAsync(root, cancellationToken);
        await _finalizer.StampLoadDiagnosisAsync(store, BuildDiagnosisFromSnapshot(discovery, snapshot), cancellationToken);
        // Stamp the Fuse build even on the syntax-first pass so a partial index also carries provenance.
        await store.SetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, FuseBuildInfo.Current, cancellationToken);
        // R22: stamp the extraction-contract version so index reuse is gated on what was extracted, not the product
        // version. Bump WorkspaceIndexSchema.ExtractionContractVersion in the same change as any extractor change.
        await store.SetMetaAsync(
            WorkspaceIndexStore.ExtractionVersionMetaKey,
            WorkspaceIndexSchema.ExtractionContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        await store.PruneFilesAsync(files.Select(file => file.NormalizedPath).ToArray(), cancellationToken);
        await store.SetMetaAsync(StaleAsOfMetaKey, "0", cancellationToken);
        await _finalizer.StampSkippedFilesAsync(store, scan.Skipped, cancellationToken); // R35: surface skips from the first pass.
        await _finalizer.StampDetailLimitedFilesAsync(store, scan.DetailLimited, cancellationToken);
        await WorkspaceIndexManifest.CompleteAsync(root, store, files, cancellationToken);
        return result;
    }

    /// <summary>
    ///     Maximum number of files committed in one syntax batch before yielding the SQLite writer.
    /// </summary>
    internal const int SyntaxCommitFileBatchSize = 32;

    /// <summary>
    ///     Maximum source bytes retained in one syntax batch before yielding the SQLite writer.
    /// </summary>
    internal const long SyntaxCommitSourceBatchBytes = 16L * 1024 * 1024;

    /// <summary>
    ///     Maximum number of files committed in one semantic-upgrade batch before yielding the SQLite writer.
    /// </summary>
    internal const int UpgradeCommitFileBatchSize = 32;

    /// <summary>
    ///     Upgrades a syntax-first index to the full semantic graph through the selected MSBuild workspace, then
    ///     clearing <see cref="SemanticPendingMetaKey" />. This is requested by an explicit semantic index job.
    ///     It does not start the build-capture worker: the worker is reserved for build-captured verification and
    ///     portable capture commands. Commits per project and per
    ///     <see cref="UpgradeCommitFileBatchSize" /> files so warm reads can interleave under WAL.
    /// </summary>
    /// <param name="rootDirectory">The workspace root.</param>
    /// <param name="store">The index store to write to (a fresh store handle, since the foreground store is disposed).</param>
    /// <param name="cancellationToken">A token to cancel the upgrade.</param>
    /// <param name="progress">An optional synchronous observer for compiler-stage progress.</param>
    /// <returns>The full index summary (semantic, partial, or syntax if the load could not improve on syntax).</returns>
    public async Task<SemanticIndexResult> UpgradeToSemanticAsync(
        string rootDirectory,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken,
        IProgress<SemanticIndexProgress>? progress = null)
    {
        var root = Path.GetFullPath(rootDirectory);
        var discovery = await _discoverer.DiscoverAsync(root, cancellationToken);
        var scan = await ScanFilesAsync(root, cancellationToken);
        var files = scan.Files;
        // An explicit semantic job loads the selected workspace once and then extracts projects in sequence.
        // Starting a build-capture worker here would rebuild a whole solution as a side effect of indexing, which
        // defeats syntax-first indexing and prevents project-level cancellation and progress reporting.
        var snapshot = await _loader.LoadAsync(discovery, cancellationToken);
        var result = snapshot.SemanticLoadSucceeded
            ? await IndexSemanticChunkedAsync(root, store, files, snapshot, cancellationToken, progress)
            : await _syntaxStage.IndexChunkedAsync(root, store, files, snapshot, cancellationToken, progress: null);
        var diagnosis = BuildDiagnosisFromSnapshot(discovery, snapshot);

        await store.SetMetaAsync("index_mode", result.Mode, cancellationToken);
        await store.SetMetaAsync(SemanticPendingMetaKey, "0", cancellationToken);
        // R43: stamp the per-project load diagnosis so doctor reports the tier from the warm index (no live load).
        await _finalizer.StampLoadDiagnosisAsync(store, diagnosis, cancellationToken);
        await store.SetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, FuseBuildInfo.Current, cancellationToken);
        // R22: stamp the extraction-contract version so index reuse is gated on what was extracted, not the product
        // version. Bump WorkspaceIndexSchema.ExtractionContractVersion in the same change as any extractor change.
        await store.SetMetaAsync(
            WorkspaceIndexStore.ExtractionVersionMetaKey,
            WorkspaceIndexSchema.ExtractionContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        await store.PruneFilesAsync(files.Select(file => file.NormalizedPath).ToArray(), cancellationToken);
        await store.SetMetaAsync(StaleAsOfMetaKey, "0", cancellationToken);
        await _finalizer.StampIntegrityAsync(store, store, cancellationToken); // R31: record the post-upgrade integrity result.

        await WorkspaceIndexManifest.CompleteAsync(root, store, files, cancellationToken);
        return result;
    }

    // Scans the extensions the registered language providers claim, plus the .NET config files needed for
    // discovery, so a non-C# spike language is surfaced to the indexer without hardwiring its extension.
    private async Task<FileScanResult> ScanFilesAsync(string root, CancellationToken cancellationToken)
        => await _inventory.ScanAsync(root, cancellationToken);

    /// <summary>
    ///     Checks whether the current source inventory matches the hashes persisted in an index without writing it.
    /// </summary>
    /// <param name="rootDirectory">The workspace root.</param>
    /// <param name="store">The readable index store.</param>
    /// <param name="cancellationToken">A token to cancel scanning or hash comparison.</param>
    /// <returns>True when every current indexed file and hash matches the stored inventory.</returns>
    /// <remarks>
    ///     This is the read-side half of the freshness contract. A caller that receives false must start or join
    ///     a repository job before returning indexed facts; it must not serve stale rows while a file edit waits
    ///     to be reconciled.
    /// </remarks>
    public async Task<bool> IsInventoryCurrentAsync(
        string rootDirectory,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken)
        => await _inventory.IsCurrentAsync(Path.GetFullPath(rootDirectory), store, cancellationToken);

    /// <summary>
    ///     Re-indexes a single changed file in place: clears that file's stored rows and re-extracts its
    ///     syntax-level data (symbols, chunks, full-text, routes), without rebuilding the whole index.
    /// </summary>
    /// <param name="rootDirectory">The workspace root.</param>
    /// <param name="normalizedPath">The changed file's normalized (forward-slash, repo-relative) path.</param>
    /// <param name="store">The index store to update.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of symbols re-indexed for the file (0 for a non-C# file or a deleted file).</returns>
    /// <remarks>
    ///     This updates the file's own syntax-level rows only. Cross-file semantic graph edges (DI resolution,
    ///     route handlers, MediatR and EF wiring) are computed from the whole compilation and are not
    ///     recomputed here; a full <see cref="IndexAsync" /> refreshes those. The incremental path keeps an
    ///     edit-heavy session's full-text and symbol rows current at low cost. When the file no longer exists,
    ///     its rows are cleared and nothing is re-added.
    /// </remarks>
    public async Task<int> ReindexFileAsync(
        string rootDirectory,
        string normalizedPath,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken)
        => await _dirtyFileReconciler.ReindexFileAsync(
            Path.GetFullPath(rootDirectory), normalizedPath, store, cancellationToken);

    /// <summary>The metadata key recording the dirty-file count when a freshness reconcile degraded to a stamp.</summary>
    public const string StaleAsOfMetaKey = "stale_dirty_count";

    /// <summary>
    ///     Reconciles the index against the current complete on-disk file inventory: the N6 freshness contract.
    ///     Added and edited files are re-indexed (syntax rows) and deleted files are removed, so a
    ///     read tool serves fresh data rather than an index frozen at first call. When the number of dirty files
    ///     exceeds a storm threshold the pass does not reconcile; it records a stale-as-of stamp instead, so a bulk
    ///     change degrades to an explicit "run a full index" signal rather than a reconcile storm.
    /// </summary>
    /// <param name="rootDirectory">The workspace root.</param>
    /// <param name="store">The index store to reconcile.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The freshness outcome (files checked, reconciled, remaining dirty, and whether it was stamped stale).</returns>
    /// <remarks>
    ///     Cross-file semantic edges are not recomputed here (see
    ///     <see cref="ReindexFileAsync" />); the resident-workspace path recomputes the affected neighborhood.
    /// </remarks>
    public async Task<FreshnessResult> ReconcileDirtyFilesAsync(
        string rootDirectory, IWorkspaceIndexStore store, CancellationToken cancellationToken)
        => await _dirtyFileReconciler.ReconcileAsync(
            Path.GetFullPath(rootDirectory), store, StaleAsOfMetaKey, cancellationToken);

    private async Task<SemanticIndexResult> IndexSemanticAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        // This non-capture path cannot establish target-framework membership. Clear any prior capture-derived
        // facts rather than serving availability from an unrelated build.
        await store.ReplaceTfmAvailabilityAsync([], cancellationToken);
        var projects = _semanticGraph.BuildProjectRecords(snapshot, cancellationToken);
        await store.UpsertProjectsAsync(projects, cancellationToken);

        var fileToProject = SemanticGraphExtractor.BuildFileProjectMap(root, snapshot);
        var linkedFiles = files
            .Select(f => (fileToProject.TryGetValue(f.NormalizedPath, out var projectPath)
                ? f with { ProjectPath = projectPath }
                : f) with
            { Language = _syntaxProviders.ForExtension(f.Extension)?.Language })
            .ToList();
        await store.UpsertFilesAsync(linkedFiles, cancellationToken);

        var symbols = new List<SymbolRecord>();
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            symbols.AddRange(_semanticGraph.ExtractSymbols(project, root, cancellationToken));
        }

        await store.UpsertSymbolsAsync(symbols, cancellationToken);

        var (chunks, syntaxRoutes) = await _syntaxStage.ExtractChunksAndRoutesAsync(root, files, dropChunkSymbolIds: true, cancellationToken);
        await store.UpsertChunksAsync(chunks, cancellationToken);
        // Syntax routes first (covers minimal APIs), then the semantic MVC routes overwrite by route id with
        // their resolved handler symbol ids.
        await store.UpsertRoutesAsync(syntaxRoutes, cancellationToken);

        // Run the analyzers over every loaded project and store the resulting graph. Nodes are upserted before
        // edges so the edge foreign keys resolve.
        var graph = _semanticGraph.AnalyzeWorkspace(root, snapshot, cancellationToken);
        await store.UpsertNodesAsync(graph.Nodes, cancellationToken);
        await store.UpsertEdgesAsync(graph.Edges, cancellationToken);
        await store.UpsertRoutesAsync(graph.Routes, cancellationToken);
        await store.UpsertDiRegistrationsAsync(graph.DiRegistrations, cancellationToken);
        await store.UpsertOptionsBindingsAsync(graph.OptionsBindings, cancellationToken);

        // Any load diagnostic (MSBuild warning, a project without a compilation) means the semantic picture is
        // incomplete; report that honestly as partial rather than claiming a clean semantic index.
        var diagnostics = snapshot.Diagnostics.Concat(graph.Diagnostics).ToList();
        var mode = snapshot.Diagnostics.Any(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            ? "partial"
            : "semantic";

        var routeCount = syntaxRoutes.Count + graph.Routes.Count;
        return new SemanticIndexResult(mode, linkedFiles.Count, projects.Count, symbols.Count, chunks.Count, routeCount, diagnostics);
    }

    // R14: explicit semantic indexing commits per project and per file batch so WAL readers are not blocked by one long write.
    private async Task<SemanticIndexResult> IndexSemanticChunkedAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken,
        IProgress<SemanticIndexProgress>? progress)
    {
        // This non-capture path cannot establish target-framework membership. Clear any prior capture-derived
        // facts rather than serving availability from an unrelated build.
        await store.ReplaceTfmAvailabilityAsync([], cancellationToken);
        var projects = _semanticGraph.BuildProjectRecords(snapshot, cancellationToken);
        await store.UpsertProjectsAsync(projects, cancellationToken);

        var fileToProject = SemanticGraphExtractor.BuildFileProjectMap(root, snapshot);
        var linkedFiles = files
            .Select(f => (fileToProject.TryGetValue(f.NormalizedPath, out var projectPath)
                ? f with { ProjectPath = projectPath }
                : f) with
            { Language = _syntaxProviders.ForExtension(f.Extension)?.Language })
            .ToList();

        for (var i = 0; i < linkedFiles.Count; i += UpgradeCommitFileBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = linkedFiles.Skip(i).Take(UpgradeCommitFileBatchSize).ToList();
            await store.UpsertFilesAsync(batch, cancellationToken);
        }

        var symbols = new List<SymbolRecord>();
        progress?.Report(new SemanticIndexProgress(
            SemanticIndexStage.SemanticExtraction,
            0,
            snapshot.Projects.Count,
            "extracting compiler symbols"));
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectSymbols = _semanticGraph.ExtractSymbols(project, root, cancellationToken).ToList();
            symbols.AddRange(projectSymbols);
            await store.UpsertSymbolsAsync(projectSymbols, cancellationToken);
        }

        var (chunks, syntaxRoutes) = await _syntaxStage.ExtractChunksAndRoutesChunkedAsync(
            store, root, files, dropChunkSymbolIds: true, cancellationToken);
        await store.UpsertRoutesAsync(syntaxRoutes, cancellationToken);

        var edges = new List<SemanticEdgeRecord>();
        var semanticRoutes = new List<RouteRecord>();
        var registrations = new List<DiRegistrationRecord>();
        var bindings = new List<OptionsBindingRecord>();
        var graphDiagnostics = new List<DiagnosticRecord>();

        var completedProjects = 0;
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var graph = _semanticGraph.AnalyzeProject(project, root, cancellationToken);
            edges.AddRange(graph.Edges);
            semanticRoutes.AddRange(graph.Routes);
            registrations.AddRange(graph.DiRegistrations);
            bindings.AddRange(graph.OptionsBindings);
            graphDiagnostics.AddRange(graph.Diagnostics);

            await store.UpsertNodesAsync(graph.Nodes, cancellationToken);
            await store.UpsertEdgesAsync(graph.Edges, cancellationToken);
            await store.UpsertRoutesAsync(graph.Routes, cancellationToken);
            await store.UpsertDiRegistrationsAsync(graph.DiRegistrations, cancellationToken);
            await store.UpsertOptionsBindingsAsync(graph.OptionsBindings, cancellationToken);
            completedProjects++;
            progress?.Report(new SemanticIndexProgress(
                SemanticIndexStage.SemanticExtraction,
                completedProjects,
                snapshot.Projects.Count,
                project.Name));
        }

        progress?.Report(new SemanticIndexProgress(
            SemanticIndexStage.SemanticPersistence,
            1,
            1,
            "semantic facts persisted"));

        var diagnostics = snapshot.Diagnostics.Concat(graphDiagnostics).ToList();
        var mode = snapshot.Diagnostics.Any(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            ? "partial"
            : "semantic";
        var routeCount = syntaxRoutes.Count + semanticRoutes.Count;
        return new SemanticIndexResult(mode, linkedFiles.Count, projects.Count, symbols.Count, chunks.Count, routeCount, diagnostics);
    }

    // Tier 1 write path: the graph came from the out-of-process build-capture worker (exact compilations), so
    // the symbols, nodes, edges, routes, and DI/options are taken from its bundle. Chunks and syntax routes are
    // produced here from the parent's own syntax pass, exactly as the semantic path does.
    /// <summary>
    ///     Indexes a workspace from a portable capture bundle's extracted graph, without building (C2). The bundle
    ///     carries the graph a tier-1 build already produced elsewhere (its CI), so this scans the local source for
    ///     files and chunks and writes the bundle's symbols, nodes, edges, routes, and DI/options to the store -
    ///     the oracle-grade graph on a machine that cannot restore or build. Stamps the index mode and Fuse version
    ///     exactly as <see cref="IndexAsync" /> does.
    /// </summary>
    /// <param name="rootDirectory">The workspace root (its source is present locally; only restore/build is not).</param>
    /// <param name="store">The index store to write to.</param>
    /// <param name="capture">The extracted graph read from the bundle.</param>
    /// <param name="cancellationToken">A token to cancel the index.</param>
    /// <param name="captureBundleDir">
    ///     The absolute path to the capture bundle directory, when present. Stamped into the index metadata
    ///     (<see cref="WorkspaceIndexStore.CaptureComplogPathMetaKey" />) so <c>fuse_check</c> can answer
    ///     oracle-grade from the bundle's compiler log(s) without building - the single <c>capture.complog</c> of a
    ///     direct bundle or the per-project logs of a merged (G4) bundle, resolved by the consumer. Null when no
    ///     compiler log is available.
    /// </param>
    /// <returns>The index summary (semantic when every captured project was clean, else partial).</returns>
    public async Task<SemanticIndexResult> IndexFromCaptureGraphAsync(
        string rootDirectory,
        IWorkspaceIndexStore store,
        Fuse.Indexing.CaptureResult capture,
        CancellationToken cancellationToken,
        string? captureBundleDir = null)
    {
        var root = Path.GetFullPath(rootDirectory);
        await WorkspaceIndexManifest.BeginBuildAsync(root, store, cancellationToken);
        var scan = await ScanFilesAsync(root, cancellationToken);
        var files = scan.Files;
        await store.ClearFileDataAsync(files.Select(file => file.NormalizedPath).ToArray(), cancellationToken);
        var result = await IndexFromCaptureAsync(root, store, files, capture, cancellationToken);
        await store.SetMetaAsync("index_mode", result.Mode, cancellationToken);
        await store.SetMetaAsync(SemanticPendingMetaKey, "0", cancellationToken);
        await store.SetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, FuseBuildInfo.Current, cancellationToken);
        // R22: stamp the extraction-contract version so index reuse is gated on what was extracted, not the product
        // version. Bump WorkspaceIndexSchema.ExtractionContractVersion in the same change as any extractor change.
        await store.SetMetaAsync(
            WorkspaceIndexStore.ExtractionVersionMetaKey,
            WorkspaceIndexSchema.ExtractionContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        if (!string.IsNullOrEmpty(captureBundleDir) && Directory.Exists(captureBundleDir))
            await store.SetMetaAsync(WorkspaceIndexStore.CaptureComplogPathMetaKey, Path.GetFullPath(captureBundleDir), cancellationToken);
        await store.PruneFilesAsync(files.Select(file => file.NormalizedPath).ToArray(), cancellationToken);
        await store.SetMetaAsync(StaleAsOfMetaKey, "0", cancellationToken);
        await WorkspaceIndexManifest.CompleteAsync(root, store, files, cancellationToken);
        return result;
    }

    internal async Task<SemanticIndexResult> IndexFromCaptureAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        Fuse.Indexing.CaptureResult capture,
        CancellationToken cancellationToken,
        bool replaceTfmAvailability = true)
    {
        // R60: a build captures one compiler invocation per target framework. Select a deterministic primary
        // representation for each project, then union every stable declaration and graph fact from every target
        // rather than allowing the last compiler invocation to overwrite a non-primary-only fact in SQLite.
        var union = CanonicalTfmUnion.Create(capture);
        var projects = _semanticGraph.BuildCaptureProjectRecords(union.Projects, cancellationToken);
        await store.UpsertProjectsAsync(projects, cancellationToken);

        var fileToProject = union.Symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol.ProjectPath))
            .GroupBy(symbol => symbol.FilePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ProjectPath!, StringComparer.Ordinal);
        var linkedFiles = files
            .Select(f =>
                (fileToProject.TryGetValue(f.NormalizedPath, out var projectPath)
                    ? f with { ProjectPath = projectPath }
                    : f) with
                { Language = _syntaxProviders.ForExtension(f.Extension)?.Language })
            .ToList();
        await store.UpsertFilesAsync(linkedFiles, cancellationToken);

        var symbols = union.Symbols;
        await store.UpsertSymbolsAsync(symbols, cancellationToken);

        var (chunks, syntaxRoutes) = await _syntaxStage.ExtractChunksAndRoutesAsync(root, files, dropChunkSymbolIds: true, cancellationToken);
        await store.UpsertChunksAsync(chunks, cancellationToken);
        await store.UpsertRoutesAsync(syntaxRoutes, cancellationToken);

        // Nodes before edges so the edge foreign keys resolve, then the semantic routes/DI/options from the bundle.
        var nodes = union.Nodes;
        var edges = union.Edges;
        var semanticRoutes = union.Routes;
        var registrations = union.Registrations;
        var bindings = union.Bindings;
        await store.UpsertNodesAsync(nodes, cancellationToken);
        await store.UpsertEdgesAsync(edges, cancellationToken);
        await store.UpsertRoutesAsync(semanticRoutes, cancellationToken);
        await store.UpsertDiRegistrationsAsync(registrations, cancellationToken);
        await store.UpsertOptionsBindingsAsync(bindings, cancellationToken);
        // Only a complete build capture owns this projection. A partial resident refresh must preserve the
        // availability facts supplied by the complete capture for the rest of the workspace.
        if (replaceTfmAvailability)
            await store.ReplaceTfmAvailabilityAsync(union.Availability, cancellationToken);
        await store.SetMetaAsync("multi_tfm_union_loss", "0", cancellationToken);
        await store.SetMetaAsync("multi_tfm_capture_invocations", union.CapturedProjectCount.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        await store.SetMetaAsync("multi_tfm_unique_projects", union.Projects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        await store.SetMetaAsync("multi_tfm_raw_entities", union.RawEntityCount.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        await store.SetMetaAsync("multi_tfm_canonical_entities", union.CanonicalEntityCount.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);

        // Build capture shares the real build's inputs, so a project with residual compile errors is graph-grade
        // (partial); a clean capture across every project is the oracle-grade semantic tier.
        var mode = capture.Projects.Any(p => p.ErrorCount > 0) ? "partial" : "semantic";
        var diagnostics = new List<DiagnosticRecord>
        {
            new(DiagnosticSeverity.Info, "build-capture",
                $"Tier-1 canonical TFM union: {union.CapturedProjectCount} compiler invocation(s), " +
                $"{union.Projects.Count} project(s), {symbols.Count} symbols, {edges.Count} edges."),
        };
        var routeCount = syntaxRoutes.Count + semanticRoutes.Count;
        return new SemanticIndexResult(mode, linkedFiles.Count, union.Projects.Count, symbols.Count, chunks.Count, routeCount, diagnostics);
    }

    /// <summary>
    ///     Projects live (resident) compilations into the store (S1 step 4): for each compilation, extracts its
    ///     symbols and wiring graph in-process (the same extraction the build-capture worker runs) and upserts them
    ///     through <see cref="IndexFromCaptureAsync" />, so a cross-file relationship an edit introduced (for
    ///     example a new DI registration) becomes queryable without a full re-index.
    /// </summary>
    /// <remarks>
    ///     The store upserts are INSERT OR REPLACE by content-stable id, so re-projecting unchanged content is
    ///     idempotent and an added or changed entity updates in place; this covers the add and change case.
    ///     Entities REMOVED by an edit leave stale rows until the changed files' rows are cleared first, which is a
    ///     follow-up. The caller supplies the projects' on-disk files (chunks are read from disk, so an edit must
    ///     already be written). This is a store-write: per the single-writer invariant only one process (the serve
    ///     watcher) may call it for a given root.
    /// </remarks>
    /// <param name="root">The workspace root.</param>
    /// <param name="store">The index store to project into.</param>
    /// <param name="compilations">Each project's file path paired with its live compilation.</param>
    /// <param name="files">The on-disk file records for those projects (for the file rows and chunk extraction).</param>
    /// <param name="cancellationToken">A token to cancel the projection.</param>
    /// <returns>The index result (mode and counts) for the projected set.</returns>
    public async Task<SemanticIndexResult> ProjectFromCompilationsAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<(string ProjectFilePath, Microsoft.CodeAnalysis.Compilation Compilation)> compilations,
        IReadOnlyList<IndexedFileRecord> files,
        CancellationToken cancellationToken)
    {
        // Clear each projected file's existing semantic rows first, so an entity an edit REMOVED does not linger
        // as a stale row; the upsert below then reinserts the current set. Clear-then-reproject is an idempotent
        // replace (the file row itself is kept, so symbols keep their foreign key). This covers add, change, and
        // removal within the projected files.
        foreach (var file in files)
            await store.DeleteFileDataAsync(file.NormalizedPath, cancellationToken);

        var captured = new List<CapturedProject>(compilations.Count);
        foreach (var (projectFilePath, compilation) in compilations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = new LoadedProject(
                Name: Path.GetFileNameWithoutExtension(projectFilePath),
                FilePath: projectFilePath,
                AssemblyName: compilation.AssemblyName,
                Compilation: compilation);
            // Paths are made relative to the workspace root (not the project directory) so symbol and node
            // rows match the root-relative files.normalized_path the store links foreign keys against. Passing
            // the project directory here produced project-relative paths that never resolved, so every symbol
            // was dropped (null file_id) and every node stored an unlinked file_id. Matches the root passed by
            // IndexSemanticChunkedAsync.
            var symbols = _semanticGraph.ExtractSymbols(loaded, root, cancellationToken);
            var graph = _semanticGraph.AnalyzeProject(loaded, root, cancellationToken);
            var errorCount = compilation.GetDiagnostics(cancellationToken)
                .Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            captured.Add(new CapturedProject(
                Name: loaded.Name,
                FilePath: loaded.FilePath,
                AssemblyName: compilation.AssemblyName,
                ErrorCount: errorCount,
                TypeCount: 0,
                SymbolCount: symbols.Count,
                NodeCount: graph.Nodes.Count,
                EdgeCount: graph.Edges.Count,
                Symbols: symbols,
                Nodes: graph.Nodes,
                Edges: graph.Edges,
                Routes: graph.Routes,
                DiRegistrations: graph.DiRegistrations,
                OptionsBindings: graph.OptionsBindings));
        }

        return await IndexFromCaptureAsync(
            root,
            store,
            files,
            CaptureResult.Ok(captured),
            cancellationToken,
            replaceTfmAvailability: false);
    }

}

/// <summary>
///     A summary of a semantic indexing pass.
/// </summary>
/// <param name="Mode">The index mode: <c>semantic</c>, <c>partial</c>, or <c>syntax</c>.</param>
/// <param name="FileCount">The number of files indexed.</param>
/// <param name="ProjectCount">The number of projects indexed.</param>
/// <param name="SymbolCount">The number of symbols indexed.</param>
/// <param name="ChunkCount">The number of chunks indexed.</param>
/// <param name="RouteCount">The number of routes indexed.</param>
/// <param name="Diagnostics">Diagnostics gathered during loading and indexing.</param>
public sealed record SemanticIndexResult(
    string Mode,
    int FileCount,
    int ProjectCount,
    int SymbolCount,
    int ChunkCount,
    int RouteCount,
    IReadOnlyList<DiagnosticRecord> Diagnostics);

/// <summary>
///     The outcome of a freshness reconcile pass (the N6 contract): how many known files were checked against
///     their on-disk content, how many were reconciled, how many remain dirty, and whether the pass degraded to a
///     stale-as-of stamp instead of reconciling (a bulk change above the storm threshold).
/// </summary>
/// <param name="Checked">The number of known files whose on-disk hash was compared.</param>
/// <param name="Reconciled">The number of dirty files re-indexed (or removed) by this pass.</param>
/// <param name="DirtyRemaining">The number of dirty files left unreconciled (nonzero only when stamped).</param>
/// <param name="Stamped">Whether the result is stamped stale-as-of rather than reconciled (storm protection).</param>
public sealed record FreshnessResult(int Checked, int Reconciled, int DirtyRemaining, bool Stamped)
{
    /// <summary>Whether the index is fresh after this pass (nothing dirty remains).</summary>
    public bool IsFresh => DirtyRemaining == 0;
}

/// <summary>
///     The outcome of diagnosing a workspace's semantic load, reported by <c>fuse doctor</c>.
/// </summary>
/// <param name="Tier">The achieved load tier (oracle-grade, graph-grade (partial), or syntax).</param>
/// <param name="ProjectsLoaded">The number of projects that produced a compilation.</param>
/// <param name="ProjectsTotal">The total number of projects the loader opened.</param>
/// <param name="Projects">The per-project load reports with their concrete reasons.</param>
/// <param name="Diagnostics">The load diagnostics (SDK, restore, MSBuild) gathered during loading.</param>
/// <param name="SelectedSolution">
///     The solution (or project-set summary) discovery selected as the semantic target, so doctor names the exact
///     workspace bound to the typed graph rather than leaving it implicit (R24).
/// </param>
/// <param name="SelectionNote">
///     A warning when the selection was ambiguous, pinned, or fell back from a fixture-directory solution (R24);
///     <see langword="null" /> for an unambiguous root-level solution.
/// </param>
public sealed record LoadDiagnosis(
    string Tier,
    int ProjectsLoaded,
    int ProjectsTotal,
    IReadOnlyList<ProjectLoadReport> Projects,
    IReadOnlyList<DiagnosticRecord> Diagnostics,
    string? SelectedSolution = null,
    string? SelectionNote = null);
