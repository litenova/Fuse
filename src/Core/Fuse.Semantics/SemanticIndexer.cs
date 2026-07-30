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
    private readonly WorkspaceFileScanner _scanner;
    private readonly SemanticSymbolExtractor _semanticSymbols;
    private readonly SyntaxSymbolExtractor _syntaxSymbols;
    private readonly SyntaxRouteExtractor _routeExtractor;
    private readonly FileHashService _hashService;
    private readonly SemanticAnalysisRunner _analysisRunner;
    private readonly LanguageSyntaxProviderRegistry _syntaxProviders;
    private readonly GitCoChangeCollector _coChangeCollector = new();
    private readonly BuildCaptureClient _buildCaptureClient = new();
    // R42: the warm-solution cache doctor's live diagnosis reuses, so a second doctor in a session skips the full
    // MSBuild load. Shared with the refactorers, so a warm solution loaded by either serves the other.
    private readonly WarmSolutionCache _warmSolutions = WarmSolutionCache.Shared;

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

    // Non-source file extensions the scanner still needs for project discovery and config indexing, beyond the
    // source extensions the language providers claim.
    private static readonly string[] ConfigExtensions = [".csproj", ".props", ".targets", ".json"];

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
    public SemanticIndexer(
        DotNetWorkspaceDiscoverer discoverer,
        RoslynWorkspaceLoader loader,
        WorkspaceFileScanner scanner,
        SemanticSymbolExtractor semanticSymbols,
        SyntaxSymbolExtractor syntaxSymbols,
        SyntaxRouteExtractor routeExtractor,
        FileHashService hashService,
        SemanticAnalysisRunner analysisRunner)
    {
        _discoverer = discoverer;
        _loader = loader;
        _scanner = scanner;
        _semanticSymbols = semanticSymbols;
        _syntaxSymbols = syntaxSymbols;
        _routeExtractor = routeExtractor;
        _hashService = hashService;
        _analysisRunner = analysisRunner;
        // The syntax tier is provider-driven: C# behind the seam (unchanged behavior), plus a second-language
        // syntax spike. Built internally so the existing constructor and its callers are unaffected; a later
        // change can make the provider set injectable for an external language plugin.
        _syntaxProviders = new LanguageSyntaxProviderRegistry([new CSharpSyntaxProvider(syntaxSymbols), new PythonSyntaxProvider(), new JavaScriptSyntaxProvider()]);
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
                : await IndexSyntaxAsync(root, store, files, snapshot, cancellationToken);
            diagnosis = BuildDiagnosisFromSnapshot(discovery, snapshot);
        }

        await store.SetMetaAsync("index_mode", result.Mode, cancellationToken);
        // A full pass is the final word on the mode: clear any syntax-first pending flag a prior fast pass set.
        await store.SetMetaAsync(SemanticPendingMetaKey, "0", cancellationToken);
        // R43: stamp the per-project load diagnosis so doctor reports the tier from the warm index (no live load).
        await StampLoadDiagnosisAsync(store, diagnosis, cancellationToken);
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
        await StampIntegrityAsync(store, cancellationToken); // R31: record the post-build integrity result.
        await StampSkippedFilesAsync(store, scan.Skipped, cancellationToken); // R35: record skipped files.

        // Mine git co-change couplings so the open-ended scorer can recover sibling files of a multi-file change.
        // Best-effort and bounded (a commit cap, wide commits skipped); a non-repository or a git failure is a
        // no-op, so it never breaks indexing. Only the full pass mines; the syntax-first fast path skips it.
        // R41: only mine co-change when it is enabled (FUSE_COCHANGE). The prior is default-off (D6), so the
        // git log walk - a large share of the index hot path - is wasted work on the default index path.
        if (GitCoChangeCollector.IsCollectionEnabled())
        {
            try
            {
                await _coChangeCollector.CollectAndStoreAsync(root, store, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Co-change is an optional prior; a mining or write failure must not fail the index.
            }
        }

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

    // R43: stamp the load diagnosis into index_meta so doctor can report the tier and per-project reasons from the
    // warm index without a live MSBuild load. Best-effort: a serialization or write hiccup must not fail the index.
    private static async Task StampLoadDiagnosisAsync(
        IWorkspaceIndexStore store, LoadDiagnosis diagnosis, CancellationToken cancellationToken)
    {
        try
        {
            var persisted = new PersistedLoadDiagnosis(
                diagnosis.Tier,
                diagnosis.ProjectsLoaded,
                diagnosis.ProjectsTotal,
                diagnosis.Projects.Select(p => new PersistedProjectReport(p.Name, p.FilePath, p.Loaded, p.Reason)).ToList(),
                diagnosis.SelectedSolution,
                diagnosis.SelectionNote);
            var json = System.Text.Json.JsonSerializer.Serialize(persisted, PersistedLoadDiagnosisJsonContext.Default.PersistedLoadDiagnosis);
            await store.SetMetaAsync(WorkspaceIndexStore.LoadDiagnosisMetaKey, json, cancellationToken);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or System.Text.Json.JsonException)
        {
        }
    }

    // R31: record the post-build integrity result in index_meta, so the recorded health is auditable alongside
    // the live check the read paths run. Best-effort: a read/write hiccup must not fail the index pass.
    private static async Task StampIntegrityAsync(IWorkspaceIndexStore store, CancellationToken cancellationToken)
    {
        try
        {
            var state = await store.GetStateAsync(cancellationToken);
            await store.SetMetaAsync(WorkspaceIndexStore.IndexIntegrityMetaKey, IndexIntegrity.Check(state).Summary(), cancellationToken);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
        }
    }

    /// <summary>
    ///     Indexes the workspace at the syntax tier only, skipping the MSBuild/Roslyn load, so a first call
    ///     serves context in a few seconds instead of waiting for the full semantic load. Sets the index mode to
    ///     <c>syntax</c>. Compiler analysis starts only from an explicit semantic request.
    /// </summary>
    /// <param name="rootDirectory">The workspace root.</param>
    /// <param name="store">The index store to write to.</param>
    /// <param name="cancellationToken">A token to cancel the index.</param>
    /// <returns>A syntax-tier index summary.</returns>
    /// <remarks>
    ///     The cold index time is dominated by the MSBuild evaluation, not the syntax extraction, so the
    ///     syntax-first pass produces a usable full-text and symbol index without loading MSBuild. Cross-file
    ///     semantic facts are added only by a requested compiler pass.
    /// </remarks>
    public async Task<SemanticIndexResult> IndexSyntaxFirstAsync(
        string rootDirectory,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        await WorkspaceIndexManifest.BeginBuildAsync(root, store, cancellationToken);
        var scan = await ScanFilesAsync(root, cancellationToken);
        var files = scan.Files;
        await store.ClearFileDataAsync(files.Select(file => file.NormalizedPath).ToArray(), cancellationToken);
        var snapshot = new RoslynWorkspaceSnapshot(
            SemanticLoadSucceeded: false,
            Projects: [],
            Diagnostics: [new DiagnosticRecord(DiagnosticSeverity.Info, "syntax-first", "Syntax-tier index served first; the semantic graph upgrades in the background.")],
            ProjectReports: []);

        var result = await IndexSyntaxAsync(root, store, files, snapshot, cancellationToken);
        await store.SetMetaAsync("index_mode", result.Mode, cancellationToken);
        // Syntax is now the completed default index depth. Compiler work starts only after an explicit semantic
        // request, so this store is not waiting for an automatic background upgrade.
        await store.SetMetaAsync(SemanticPendingMetaKey, "0", cancellationToken);
        // Stamp a syntax-tier diagnosis. Discovery is file based and does not load MSBuild.
        var discovery = await _discoverer.DiscoverAsync(root, cancellationToken);
        await StampLoadDiagnosisAsync(store, BuildDiagnosisFromSnapshot(discovery, snapshot), cancellationToken);
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
        await StampSkippedFilesAsync(store, scan.Skipped, cancellationToken); // R35: surface skips from the first pass.
        await WorkspaceIndexManifest.CompleteAsync(root, store, files, cancellationToken);
        return result;
    }

    /// <summary>
    ///     Maximum number of files committed in one upgrade batch before yielding the SQLite writer (R14).
    /// </summary>
    internal const int UpgradeCommitFileBatchSize = 50;

    /// <summary>
    ///     Upgrades a syntax-first index to the full semantic graph by running the complete indexing pass, then
    ///     clearing <see cref="SemanticPendingMetaKey" />. This is requested by an explicit semantic index job.
    ///     Commits per project and per
    ///     <see cref="UpgradeCommitFileBatchSize" /> files so warm reads can interleave under WAL.
    /// </summary>
    /// <param name="rootDirectory">The workspace root.</param>
    /// <param name="store">The index store to write to (a fresh store handle, since the foreground store is disposed).</param>
    /// <param name="cancellationToken">A token to cancel the upgrade.</param>
    /// <returns>The full index summary (semantic, partial, or syntax if the load could not improve on syntax).</returns>
    public async Task<SemanticIndexResult> UpgradeToSemanticAsync(
        string rootDirectory,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        var discovery = await _discoverer.DiscoverAsync(root, cancellationToken);
        var scan = await ScanFilesAsync(root, cancellationToken);
        var files = scan.Files;
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
                ? await IndexSemanticChunkedAsync(root, store, files, snapshot, cancellationToken)
                : await IndexSyntaxChunkedAsync(root, store, files, snapshot, cancellationToken);
            diagnosis = BuildDiagnosisFromSnapshot(discovery, snapshot);
        }

        await store.SetMetaAsync("index_mode", result.Mode, cancellationToken);
        await store.SetMetaAsync(SemanticPendingMetaKey, "0", cancellationToken);
        // R43: stamp the per-project load diagnosis so doctor reports the tier from the warm index (no live load).
        await StampLoadDiagnosisAsync(store, diagnosis, cancellationToken);
        await store.SetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, FuseBuildInfo.Current, cancellationToken);
        // R22: stamp the extraction-contract version so index reuse is gated on what was extracted, not the product
        // version. Bump WorkspaceIndexSchema.ExtractionContractVersion in the same change as any extractor change.
        await store.SetMetaAsync(
            WorkspaceIndexStore.ExtractionVersionMetaKey,
            WorkspaceIndexSchema.ExtractionContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        await store.PruneFilesAsync(files.Select(file => file.NormalizedPath).ToArray(), cancellationToken);
        await store.SetMetaAsync(StaleAsOfMetaKey, "0", cancellationToken);
        await StampIntegrityAsync(store, cancellationToken); // R31: record the post-upgrade integrity result.

        // R41: only mine co-change when enabled (FUSE_COCHANGE); the default-off prior (D6) makes it wasted work.
        if (GitCoChangeCollector.IsCollectionEnabled())
        {
            try
            {
                await _coChangeCollector.CollectAndStoreAsync(root, store, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        await WorkspaceIndexManifest.CompleteAsync(root, store, files, cancellationToken);

        return result;
    }

    // Scans the extensions the registered language providers claim, plus the .NET config files needed for
    // discovery, so a non-C# spike language is surfaced to the indexer without hardwiring its extension.
    private async Task<FileScanResult> ScanFilesAsync(string root, CancellationToken cancellationToken)
    {
        var scanExtensions = _syntaxProviders.Extensions.Concat(ConfigExtensions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return await _scanner.ScanWithSkipsAsync(new FileScanRequest(root, scanExtensions), cancellationToken);
    }

    // R35: record the files skipped during a scan (too large, unreadable) into index_meta so doctor can surface
    // them; a bounded summary keeps the meta value small on a repo with many skips.
    private static async Task StampSkippedFilesAsync(
        IWorkspaceIndexStore store, IReadOnlyList<SkippedFile> skipped, CancellationToken cancellationToken)
    {
        const int MaxListed = 20;
        var summary = skipped.Count == 0
            ? "0"
            : $"{skipped.Count}: " + string.Join("; ", skipped.Take(MaxListed).Select(s => $"{s.Path} ({s.Reason})"))
              + (skipped.Count > MaxListed ? $"; and {skipped.Count - MaxListed} more" : string.Empty);
        await store.SetMetaAsync(WorkspaceIndexStore.SkippedFilesMetaKey, summary, cancellationToken);
    }

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
    {
        var root = Path.GetFullPath(rootDirectory);
        var absolute = Path.Combine(root, normalizedPath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(absolute))
        {
            await store.DeleteFileAsync(normalizedPath, cancellationToken);
            return 0;
        }

        await store.DeleteFileDataAsync(normalizedPath, cancellationToken);

        var info = new FileInfo(absolute);
        var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken);
        var content = System.Text.Encoding.UTF8.GetString(bytes);
        var hash = _hashService.ComputeHash(bytes);
        var provider = _syntaxProviders.ForExtension(info.Extension);
        await store.UpsertFilesAsync(
            [new IndexedFileRecord(normalizedPath, normalizedPath, info.Extension, info.Length, info.LastWriteTimeUtc.Ticks, hash, Language: provider?.Language)],
            cancellationToken);

        if (provider is null)
            return 0;

        var extracted = provider.Extract(normalizedPath, content);
        await store.UpsertSymbolsAsync(extracted.Symbols, cancellationToken);
        await store.UpsertChunksAsync(extracted.Chunks, cancellationToken);
        if (string.Equals(info.Extension, ".cs", StringComparison.OrdinalIgnoreCase))
            await store.UpsertRoutesAsync(_routeExtractor.Extract(normalizedPath, content), cancellationToken);
        return extracted.Symbols.Count;
    }

    /// <summary>The metadata key recording the dirty-file count when a freshness reconcile degraded to a stamp.</summary>
    public const string StaleAsOfMetaKey = "stale_dirty_count";

    // Storm protection: above this many dirty files (a bulk change such as a branch switch or a large pull), skip
    // the per-file reconcile that would otherwise thrash the index one file at a time and instead stamp the result
    // stale, so a caller re-runs a full index rather than paying a reconcile storm on every read.
    private const int MaxReconcileFiles = 300;

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
    {
        var root = Path.GetFullPath(rootDirectory);
        var stored = await store.GetAllFileHashesAsync(cancellationToken);
        var scan = await ScanFilesAsync(root, cancellationToken);
        var current = scan.Files.ToDictionary(file => file.NormalizedPath, file => file.ContentHash, StringComparer.Ordinal);

        var changed = new List<string>();
        foreach (var (normalizedPath, currentHash) in current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stored.TryGetValue(normalizedPath, out var storedHash)
                || !string.Equals(currentHash, storedHash, StringComparison.Ordinal))
                changed.Add(normalizedPath);
        }

        // A stored-only path is either deleted or no longer part of the scannable inventory. Delete its row even
        // when the file still exists under an excluded directory such as bin/ or obj/. ReindexFileAsync cannot
        // make that distinction because its single-file contract only checks physical existence.
        var removedOrExcluded = stored.Keys.Where(path => !current.ContainsKey(path)).ToArray();
        var dirtyCount = changed.Count + removedOrExcluded.Length;

        if (dirtyCount == 0)
        {
            await store.SetMetaAsync(StaleAsOfMetaKey, "0", cancellationToken);
            await StampSkippedFilesAsync(store, scan.Skipped, cancellationToken);
            await WorkspaceIndexManifest.CompleteAsync(root, store, scan.Files, cancellationToken);
            return new FreshnessResult(current.Count, 0, 0, Stamped: false);
        }

        if (dirtyCount > MaxReconcileFiles)
        {
            // Storm: do not reconcile one-by-one. Stamp the count so the availability header (and fuse doctor)
            // reports the answer as stale-as-of and the caller runs a full index.
            await store.SetMetaAsync(StaleAsOfMetaKey, dirtyCount.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            return new FreshnessResult(current.Count, 0, dirtyCount, Stamped: true);
        }

        var reconciled = 0;
        foreach (var path in changed)
        {
            await ReindexFileAsync(root, path, store, cancellationToken);
            reconciled++;
        }

        foreach (var path in removedOrExcluded)
        {
            await store.DeleteFileAsync(path, cancellationToken);
            reconciled++;
        }

        await store.SetMetaAsync(StaleAsOfMetaKey, "0", cancellationToken);
        await StampSkippedFilesAsync(store, scan.Skipped, cancellationToken);
        await WorkspaceIndexManifest.CompleteAsync(root, store, scan.Files, cancellationToken);
        return new FreshnessResult(current.Count, reconciled, 0, Stamped: false);
    }

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
        var projects = BuildProjectRecords(snapshot, cancellationToken);
        await store.UpsertProjectsAsync(projects, cancellationToken);

        var fileToProject = BuildFileProjectMap(root, snapshot);
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
            symbols.AddRange(_semanticSymbols.Extract(project, root, cancellationToken));
        }

        await store.UpsertSymbolsAsync(symbols, cancellationToken);

        var (chunks, syntaxRoutes) = await ExtractChunksAndRoutesAsync(root, files, dropChunkSymbolIds: true, cancellationToken);
        await store.UpsertChunksAsync(chunks, cancellationToken);
        // Syntax routes first (covers minimal APIs), then the semantic MVC routes overwrite by route id with
        // their resolved handler symbol ids.
        await store.UpsertRoutesAsync(syntaxRoutes, cancellationToken);

        // Run the analyzers over every loaded project and store the resulting graph. Nodes are upserted before
        // edges so the edge foreign keys resolve.
        var graph = RunAnalyzers(root, snapshot, cancellationToken);
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

    // R14: semantic upgrade commits per project and per file batch so WAL readers are not blocked by one long write.
    private async Task<SemanticIndexResult> IndexSemanticChunkedAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        // This non-capture path cannot establish target-framework membership. Clear any prior capture-derived
        // facts rather than serving availability from an unrelated build.
        await store.ReplaceTfmAvailabilityAsync([], cancellationToken);
        var projects = BuildProjectRecords(snapshot, cancellationToken);
        await store.UpsertProjectsAsync(projects, cancellationToken);

        var fileToProject = BuildFileProjectMap(root, snapshot);
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
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectSymbols = _semanticSymbols.Extract(project, root, cancellationToken).ToList();
            symbols.AddRange(projectSymbols);
            await store.UpsertSymbolsAsync(projectSymbols, cancellationToken);
        }

        var (chunks, syntaxRoutes) = await ExtractChunksAndRoutesChunkedAsync(
            store, root, files, dropChunkSymbolIds: true, cancellationToken);
        await store.UpsertRoutesAsync(syntaxRoutes, cancellationToken);

        var edges = new List<SemanticEdgeRecord>();
        var semanticRoutes = new List<RouteRecord>();
        var registrations = new List<DiRegistrationRecord>();
        var bindings = new List<OptionsBindingRecord>();
        var graphDiagnostics = new List<DiagnosticRecord>();

        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var graph = _analysisRunner.Run(new SemanticAnalysisContext(project, root), cancellationToken);
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
        }

        var diagnostics = snapshot.Diagnostics.Concat(graphDiagnostics).ToList();
        var mode = snapshot.Diagnostics.Any(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            ? "partial"
            : "semantic";
        var routeCount = syntaxRoutes.Count + semanticRoutes.Count;
        return new SemanticIndexResult(mode, linkedFiles.Count, projects.Count, symbols.Count, chunks.Count, routeCount, diagnostics);
    }

    private async Task<SemanticIndexResult> IndexSyntaxChunkedAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        // Syntax extraction has no compiler-target view, so prior capture-derived availability would be stale.
        await store.ReplaceTfmAvailabilityAsync([], cancellationToken);
        var taggedFiles = files
            .Select(f => f with { Language = _syntaxProviders.ForExtension(f.Extension)?.Language })
            .ToList();

        for (var i = 0; i < taggedFiles.Count; i += UpgradeCommitFileBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = taggedFiles.Skip(i).Take(UpgradeCommitFileBatchSize).ToList();
            await store.UpsertFilesAsync(batch, cancellationToken);
        }

        var perFile = new (List<SymbolRecord> Symbols, List<ChunkRecord> Chunks, List<RouteRecord> Routes)?[files.Count];
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        };
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), parallelOptions, async (i, ct) =>
        {
            var file = files[i];
            var provider = _syntaxProviders.ForExtension(file.Extension);
            if (provider is null)
                return;

            var content = await File.ReadAllTextAsync(Path.Combine(root, file.Path), ct);
            var extracted = provider.Extract(file.NormalizedPath, content);
            var fileRoutes = file.Extension == ".cs"
                ? _routeExtractor.Extract(file.NormalizedPath, content).ToList()
                : [];
            perFile[i] = (extracted.Symbols.ToList(), extracted.Chunks.ToList(), fileRoutes);
        });

        var symbols = new List<SymbolRecord>();
        var chunks = new List<ChunkRecord>();
        var routes = new List<RouteRecord>();
        foreach (var entry in perFile)
        {
            if (entry is not { } e)
                continue;
            symbols.AddRange(e.Symbols);
            chunks.AddRange(e.Chunks);
            routes.AddRange(e.Routes);
        }

        await store.UpsertSymbolsAsync(symbols, cancellationToken);
        for (var i = 0; i < chunks.Count; i += UpgradeCommitFileBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = chunks.Skip(i).Take(UpgradeCommitFileBatchSize).ToList();
            if (batch.Count > 0)
                await store.UpsertChunksAsync(batch, cancellationToken);
        }

        await store.UpsertRoutesAsync(routes, cancellationToken);

        return new SemanticIndexResult("syntax", files.Count, 0, symbols.Count, chunks.Count, routes.Count, snapshot.Diagnostics);
    }

    private async Task<(List<ChunkRecord> Chunks, List<RouteRecord> Routes)> ExtractChunksAndRoutesChunkedAsync(
        IWorkspaceIndexStore store,
        string root,
        IReadOnlyList<IndexedFileRecord> files,
        bool dropChunkSymbolIds,
        CancellationToken cancellationToken)
    {
        var allChunks = new List<ChunkRecord>();
        var allRoutes = new List<RouteRecord>();
        for (var i = 0; i < files.Count; i += UpgradeCommitFileBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = files.Skip(i).Take(UpgradeCommitFileBatchSize).ToList();
            var (chunks, routes) = await ExtractChunksAndRoutesAsync(root, batch, dropChunkSymbolIds, cancellationToken);
            allChunks.AddRange(chunks);
            allRoutes.AddRange(routes);
            if (chunks.Count > 0)
                await store.UpsertChunksAsync(chunks, cancellationToken);
        }

        return (allChunks, allRoutes);
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
        var projects = BuildCaptureProjectRecords(union.Projects, cancellationToken);
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

        var (chunks, syntaxRoutes) = await ExtractChunksAndRoutesAsync(root, files, dropChunkSymbolIds: true, cancellationToken);
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
            var symbols = _semanticSymbols.Extract(loaded, root, cancellationToken);
            var graph = _analysisRunner.Run(new SemanticAnalysisContext(loaded, root), cancellationToken);
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

    private async Task<SemanticIndexResult> IndexSyntaxAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        // Syntax extraction has no compiler-target view, so prior capture-derived availability would be stale.
        await store.ReplaceTfmAvailabilityAsync([], cancellationToken);
        // Tag each file with its language from the provider that claims its extension, so retrieval can
        // filter or blend by language; a file no provider claims (a config file) stays untagged.
        var taggedFiles = files
            .Select(f => f with { Language = _syntaxProviders.ForExtension(f.Extension)?.Language })
            .ToList();
        await store.UpsertFilesAsync(taggedFiles, cancellationToken);

        // Extract per file in parallel (file read plus stateless syntax parse, the bulk of the syntax-tier cost)
        // and collect results positionally, so the flattened output is byte-identical to a sequential pass.
        var perFile = new (List<SymbolRecord> Symbols, List<ChunkRecord> Chunks, List<RouteRecord> Routes)?[files.Count];
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        };
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), parallelOptions, async (i, ct) =>
        {
            var file = files[i];
            // Select the language provider by extension; a file no provider claims (a config file) is skipped.
            var provider = _syntaxProviders.ForExtension(file.Extension);
            if (provider is null)
                return;

            var content = await File.ReadAllTextAsync(Path.Combine(root, file.Path), ct);
            var extracted = provider.Extract(file.NormalizedPath, content);
            // Route extraction is a C# web concern; a future provider would register its own route detector.
            var fileRoutes = file.Extension == ".cs"
                ? _routeExtractor.Extract(file.NormalizedPath, content).ToList()
                : [];
            perFile[i] = (extracted.Symbols.ToList(), extracted.Chunks.ToList(), fileRoutes);
        });

        var symbols = new List<SymbolRecord>();
        var (chunks, routes) = (new List<ChunkRecord>(), new List<RouteRecord>());
        foreach (var entry in perFile)
        {
            if (entry is not { } e)
                continue;
            symbols.AddRange(e.Symbols);
            chunks.AddRange(e.Chunks);
            routes.AddRange(e.Routes);
        }

        await store.UpsertSymbolsAsync(symbols, cancellationToken);
        await store.UpsertChunksAsync(chunks, cancellationToken);
        await store.UpsertRoutesAsync(routes, cancellationToken);

        return new SemanticIndexResult("syntax", files.Count, 0, symbols.Count, chunks.Count, routes.Count, snapshot.Diagnostics);
    }

    // Chunks and routes always come from syntax so full-text search works in both modes. In semantic mode the
    // chunk symbol ids are dropped: they would be the syntax fallback ids, which do not match the semantic
    // symbol table, so a dangling reference is avoided.
    private async Task<(List<ChunkRecord> Chunks, List<RouteRecord> Routes)> ExtractChunksAndRoutesAsync(
        string root,
        IReadOnlyList<IndexedFileRecord> files,
        bool dropChunkSymbolIds,
        CancellationToken cancellationToken)
    {
        var chunks = new List<ChunkRecord>();
        var routes = new List<RouteRecord>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Extension != ".cs")
                continue;

            var content = await File.ReadAllTextAsync(Path.Combine(root, file.Path), cancellationToken);
            if (string.IsNullOrEmpty(content))
                continue;

            // R47: parse each file once and share the tree between the chunk (symbol) and route extractors, instead
            // of each extractor re-parsing the content string. On the semantic path Roslyn already parsed every file
            // for the compilation; the chunk/route pass added two more parses per file, so this removes one parse per
            // file. Byte-identical output: both extractors previously parsed the same content with the same default
            // parse options, which is exactly the shared tree here. A parse failure yields no chunks/routes for the
            // file, matching the pre-R47 behavior where both extractors caught the parse error and returned empty.
            Microsoft.CodeAnalysis.SyntaxNode syntaxRoot;
            try
            {
                syntaxRoot = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(content).GetRoot();
            }
            catch
            {
                continue;
            }

            var extracted = _syntaxSymbols.Extract(file.NormalizedPath, syntaxRoot);
            foreach (var chunk in extracted.Chunks)
                chunks.Add(dropChunkSymbolIds ? chunk with { SymbolId = null } : chunk);
            routes.AddRange(_routeExtractor.Extract(file.NormalizedPath, syntaxRoot));
        }

        return (chunks, routes);
    }

    // Runs the analyzer set over every loaded project and merges the per-project graphs.
    private SemanticAnalyzerResult RunAnalyzers(string root, RoslynWorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var edges = new List<SemanticEdgeRecord>();
        var routes = new List<RouteRecord>();
        var registrations = new List<DiRegistrationRecord>();
        var bindings = new List<OptionsBindingRecord>();
        var diagnostics = new List<DiagnosticRecord>();

        // R45: run the per-project analyzer pass concurrently (each project's graph is independent - it binds its own
        // compilation with no shared mutable state - so distinct compilations parallelize rather than serialize;
        // measured ~4.6x faster on eShopOnWeb, edges identical). The per-project results are collected positionally
        // and merged in project order below, so the flattened nodes (last-writer-wins by id) and edges are
        // byte-identical to the sequential pass - only the pass runs concurrently, not the merge.
        var projects = snapshot.Projects;
        var perProject = new SemanticAnalyzerResult[projects.Count];
        Parallel.For(
            0,
            projects.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            i => perProject[i] = _analysisRunner.Run(new SemanticAnalysisContext(projects[i], root), cancellationToken));

        // Deterministic positional merge in project order (identical to the sequential accumulation order).
        foreach (var result in perProject)
        {
            foreach (var node in result.Nodes)
                nodes[node.NodeId] = node;
            edges.AddRange(result.Edges);
            routes.AddRange(result.Routes);
            registrations.AddRange(result.DiRegistrations);
            bindings.AddRange(result.OptionsBindings);
            diagnostics.AddRange(result.Diagnostics);
        }

        // R5 part 2: after the per-project analyzers merge, emit cross-project tests edges, resolving injected
        // interfaces to their registered implementations through the di_resolves_to edges just collected. Runs
        // here (not as a per-project analyzer) so it links across projects and only to nodes that already exist.
        var existingNodeIds = new HashSet<string>(nodes.Keys, StringComparer.Ordinal);
        var diResolvesTo = edges
            .Where(e => e.EdgeType == "di_resolves_to")
            .GroupBy(e => e.FromNodeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(e => e.ToNodeId).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var (testNodes, testEdges) = new Analyzers.TestEdgeExtractor()
            .Extract(snapshot.Projects, existingNodeIds, diResolvesTo, root, cancellationToken);
        foreach (var node in testNodes)
            nodes[node.NodeId] = node;
        edges.AddRange(testEdges);

        return new SemanticAnalyzerResult(nodes.Values.ToList(), edges, routes, registrations, bindings, diagnostics);
    }

    private List<ProjectRecord> BuildProjectRecords(RoslynWorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        var records = new List<ProjectRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(project.FilePath))
                continue;

            var hash = File.Exists(project.FilePath)
                ? _hashService.ComputeHash(File.ReadAllBytes(project.FilePath))
                : "0";
            records.Add(new ProjectRecord(
                Path: project.FilePath,
                Name: project.Name,
                ProjectHash: hash,
                AssemblyName: project.AssemblyName));
        }

        return records;
    }

    private List<ProjectRecord> BuildCaptureProjectRecords(
        IReadOnlyList<CapturedProject> capturedProjects,
        CancellationToken cancellationToken)
    {
        var records = new List<ProjectRecord>(capturedProjects.Count);
        foreach (var project in capturedProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = File.Exists(project.FilePath)
                ? _hashService.ComputeHash(File.ReadAllBytes(project.FilePath))
                : "0";
            records.Add(new ProjectRecord(
                Path: project.FilePath,
                Name: project.Name,
                ProjectHash: hash,
                AssemblyName: project.AssemblyName,
                TargetFramework: project.TargetFramework));
        }

        return records;
    }

    // Maps each source file (normalized relative path) to its owning project file path, so files can be linked
    // to projects. A file shared by multiple projects (multi-targeting) maps to the first project seen.
    private static Dictionary<string, string> BuildFileProjectMap(string root, RoslynWorkspaceSnapshot snapshot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in snapshot.Projects)
        {
            foreach (var tree in project.Compilation.SyntaxTrees)
            {
                if (string.IsNullOrEmpty(tree.FilePath))
                    continue;

                var normalized = Path.GetRelativePath(root, tree.FilePath).Replace(Path.DirectorySeparatorChar, '/');
                map.TryAdd(normalized, project.FilePath);
            }
        }

        return map;
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
