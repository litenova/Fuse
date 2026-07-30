using System.Diagnostics;
using Fuse.Fusion.Scoping;
using Fuse.Fusion.PostReduction;
using Fuse.Fusion.Stages;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Emission;
using Fuse.Emission.Models;
using Fuse.Emission.Tokenization;
using Fuse.Emission.Writers;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Reduction.Models;
using Fuse.Scoping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fuse.Fusion;

/// <summary>
///     Orchestrates the full fusion pipeline: collection, optional scoping, reduction, and emission.
/// </summary>
/// <remarks>
///     This is the top-level entry point for the SDK. It validates the request, then delegates each stage to
///     <see cref="Stages.FusionCollectionStage" />, <see cref="Stages.FusionScopingStage" />,
///     <see cref="Stages.FusionReductionStage" />, and either <see cref="EmissionPipeline.EmitTableOfContentsAsync" />
///     or <see cref="PostReduction.PostReductionEnrichmentPipeline" />. See <see cref="FuseAsync" /> for the stage ordering.
/// </remarks>
public sealed class FusionOrchestrator
{
    private readonly FusionValidator _validator;
    private readonly TokenizerFactory _tokenizerFactory;
    private readonly FusionCollectionStage _collectionStage;
    private readonly FusionScopingStage _scopingStage;
    private readonly FusionReductionStage _reductionStage;
    private readonly EmissionPipeline _emissionPipeline;
    private readonly PostReductionEnrichmentPipeline _postReductionPipeline;
    private readonly IFileSystem _fileSystem;
    private readonly Func<ISourceContentProvider> _contentProviderFactory;
    private readonly CapabilityRegistry<ISymbolOutlineExtractor> _outlineExtractors;
    private readonly IWorkspaceMemoryStoreFactory _memoryStoreFactory;
    private readonly ILogger<FusionOrchestrator> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionOrchestrator" /> class.
    /// </summary>
    /// <param name="validator">Request validator.</param>
    /// <param name="tokenizerFactory">Tokenizer factory.</param>
    /// <param name="collectionStage">Collection stage.</param>
    /// <param name="scopingStage">Scoping stage.</param>
    /// <param name="reductionStage">Reduction stage.</param>
    /// <param name="emissionPipeline">Emission pipeline for table-of-contents and shared emission helpers.</param>
    /// <param name="postReductionPipeline">Post-reduction enrichment, packing, and emission pipeline.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="contentProviderFactory">Per-run content provider factory.</param>
    /// <param name="outlineExtractors">Outline extractors used when building table-of-contents symbol maps.</param>
    /// <param name="memoryStoreFactory">Host-owned repository memory cache factory.</param>
    /// <param name="logger">Optional logger.</param>
    public FusionOrchestrator(
        FusionValidator validator,
        TokenizerFactory tokenizerFactory,
        FusionCollectionStage collectionStage,
        FusionScopingStage scopingStage,
        FusionReductionStage reductionStage,
        EmissionPipeline emissionPipeline,
        PostReductionEnrichmentPipeline postReductionPipeline,
        IFileSystem fileSystem,
        Func<ISourceContentProvider> contentProviderFactory,
        CapabilityRegistry<ISymbolOutlineExtractor> outlineExtractors,
        IWorkspaceMemoryStoreFactory memoryStoreFactory,
        ILogger<FusionOrchestrator>? logger = null)
    {
        _validator = validator;
        _tokenizerFactory = tokenizerFactory;
        _collectionStage = collectionStage;
        _scopingStage = scopingStage;
        _reductionStage = reductionStage;
        _emissionPipeline = emissionPipeline;
        _postReductionPipeline = postReductionPipeline;
        _fileSystem = fileSystem;
        _contentProviderFactory = contentProviderFactory;
        _outlineExtractors = outlineExtractors;
        _memoryStoreFactory = memoryStoreFactory;
        _logger = logger ?? NullLogger<FusionOrchestrator>.Instance;
    }

    /// <summary>
    ///     Executes the full fusion pipeline for the specified request and returns the fused result.
    /// </summary>
    /// <param name="request">The fusion request describing collection, scoping, reduction, and emission settings.</param>
    /// <param name="cancellationToken">Token used to cancel collection, reduction, and emission work.</param>
    /// <returns>
    ///     The awaited <see cref="FusionResult" /> describing generated output paths or in-memory content,
    ///     token totals, processed and total file counts, and any pattern summary. When no files match a
    ///     change scope, an empty result is returned rather than throwing.
    /// </returns>
    /// <exception cref="FusionValidationException">
    ///     Thrown by validation when the request is invalid, and at runtime when a focus seed matches
    ///     no collected files.
    /// </exception>
    /// <exception cref="FusionException">
    ///     Thrown when an unrecoverable runtime error occurs, such as a failure to detect git changes.
    /// </exception>
    /// <remarks>
    ///     Stages run in a fixed order:
    ///     <list type="number">
    ///         <item><description>Collection: discover and filter candidate files via the collection pipeline.</description></item>
    ///         <item><description>Optional filtering/scoping: narrow the set by focus or git changes. These two scoping modes are mutually exclusive.</description></item>
    ///         <item><description>Reduction: apply per-file content reduction, optionally cached in host memory.</description></item>
    ///         <item><description>Emission: format and write output, then append optional route maps, project graphs, redaction reports, and pattern summaries.</description></item>
    ///     </list>
    ///     The request is validated through <see cref="FusionValidator.ValidateOrThrow" /> before any stage runs.
    ///     Every run constructs its own content cache. Optional reduction and analysis entries use a shared,
    ///     repository-scoped host-memory cache, so concurrent requests do not create another SQLite database.
    /// </remarks>
    public async Task<FusionResult> FuseAsync(FusionRequest request, CancellationToken cancellationToken = default)
    {
        _validator.ValidateOrThrow(request);

        // Run-scoped collaborators: a fresh content cache (read-once per run). Holding no cross-run state is what
        // lets fusion runs execute concurrently.
        var contentProvider = _contentProviderFactory();

        var parallelism = request.Parallelism > 0 ? request.Parallelism : Environment.ProcessorCount;
        var tokenCounter = _tokenizerFactory.GetCounter(request.Emission.TokenizerModel);
        var entryFormatter = EntryFormatterFactory.Create(request.Emission.Format);

        IKeyValueStore? memoryStore = null;
        IReductionCache? reductionCache = null;
        Indexing.IAnalysisIndex? analysisIndex = null;

        if (request.UseReductionCache || request.UseAnalysisCache)
        {
            memoryStore = _memoryStoreFactory.Open(request.Collection.SourceDirectory);

            if (request.UseAnalysisCache)
                analysisIndex = new Indexing.MemoryAnalysisIndex(memoryStore);

            if (request.UseReductionCache)
            {
                reductionCache = new MemoryReductionCache(memoryStore);
                if (request.ClearReductionCache)
                    reductionCache.Clear();
            }
        }

        try
        {
            var stageTimer = Stopwatch.StartNew();
            var collectionResult = await _collectionStage.CollectAsync(
                request.Collection,
                parallelism,
                cancellationToken);
            LogStageComplete("collection", stageTimer.ElapsedMilliseconds, collectionResult.Files.Count);

            // Resolve experimental knobs once: the configured request values with environment overrides applied.
            var experimental = ExperimentalOptions.ResolveFromEnvironment(request.Experimental);

            stageTimer.Restart();
            var filterResult = await _scopingStage.FilterAsync(
                request, collectionResult.Files, parallelism, analysisIndex, contentProvider, experimental, cancellationToken);
            if (filterResult is null)
            {
                LogStageComplete("scoping", stageTimer.ElapsedMilliseconds, 0, ResolveScopingMode(request));
                return CreateEmptyChangeResult(request);
            }

            LogAnalysisIndexStats(analysisIndex);
            LogStageComplete("scoping", stageTimer.ElapsedMilliseconds, filterResult.Files.Count, ResolveScopingMode(request));

            // Build the explicit context plan: assign each selected file a role and reduction tier once,
            // instead of inferring seed versus neighbour from the provenance chain length downstream. The plan
            // drives the per-file reduction tier below. Tiered emission (focus only) reduces
            // dependency-expanded neighbours to signature skeletons so each costs fewer tokens and the
            // budget-aware packer fits more files; seeds keep the request's level. Redaction-correct because the
            // skeleton is produced inside the reduction stage, not by a post-reduction source re-read.
            var contextPlan = ContextPlanBuilder.Build(request, experimental, filterResult);
            var appliesTieredEmission = ContextPlanBuilder.AppliesTieredEmission(request, experimental, filterResult);
            var perFileLevel = appliesTieredEmission
                ? (Func<Fuse.Collection.Models.SourceFile, Fuse.Plugins.Abstractions.Options.ReductionLevel>?)(file =>
                    ContextPlanBuilder.TierFor(contextPlan, file, request.Reduction.Level))
                : null;

            // Project the shared plan to the public read-only view carried on the result, so callers can show
            // each file's role, tier, and score without re-running scoping.
            var planProjection = contextPlan.Items
                .Select(p => new PlannedFileInfo(
                    p.Path,
                    FusionPlanRole.ForEmission(p.Role),
                    RenderTierMapping.ToReductionLevel(p.Tier).ToString(),
                    p.Score))
                .ToList();

            stageTimer.Restart();
            var reducedContent = await _reductionStage.ReduceAsync(
                request,
                filterResult.Files,
                filterResult,
                contentProvider,
                parallelism,
                reductionCache,
                tokenCounter,
                perFileLevel,
                experimental,
                cancellationToken);
            LogReductionComplete(stageTimer.ElapsedMilliseconds, reducedContent.Count, reductionCache);

            // Table-of-contents mode replaces body emission with a structural map. It runs after scoping and
            // reduction so the listed files and per-file token costs match what a full fetch would cost.
            if (request.Emission.TableOfContents)
            {
                stageTimer.Restart();
                var tocResult = await _emissionPipeline.EmitTableOfContentsAsync(
                    reducedContent,
                    request.Emission,
                    request.InMemory,
                    _fileSystem,
                    async (item, ct) =>
                    {
                        var extractor = _outlineExtractors.TryResolve(item.SourceFile.Extension);
                        if (extractor is null)
                            return [];

                        var source = await contentProvider.GetContentAsync(item.SourceFile, ct);
                        return extractor.ExtractOutline(source);
                    },
                    tokenCounter,
                    cancellationToken);
                LogStageComplete("table-of-contents", stageTimer.ElapsedMilliseconds, tocResult.ProcessedFileCount);
                return WithPlan(WithReductionCacheStats(tocResult, reductionCache), planProjection);
            }

            stageTimer.Restart();
            var postReductionContext = new PostReductionContext(
                request,
                reducedContent,
                collectionResult.Files,
                filterResult.Provenance,
                filterResult.Scores,
                filterResult.SelectedMembers,
                filterResult.Preamble,
                tokenCounter,
                entryFormatter,
                experimental);
            var emissionResult = await _postReductionPipeline.ProcessAsync(
                postReductionContext,
                contentProvider,
                cancellationToken);
            LogStageComplete("post-reduction", stageTimer.ElapsedMilliseconds, emissionResult.ProcessedFileCount);

            return WithPlan(WithReductionCacheStats(emissionResult, reductionCache), planProjection);
        }
        finally
        {
            if (memoryStore is not null)
                await memoryStore.DisposeAsync();
        }
    }

    // Attaches the context-plan projection to a result, copying the other fields (FusionResult is immutable).
    // The plan is the same for every emission path of a run, so it is applied once at the return.
    private static FusionResult WithPlan(FusionResult result, IReadOnlyList<PlannedFileInfo> plan)
    {
        if (plan.Count == 0)
            return result;

        return result with { Plan = plan };
    }

    private static string ResolveScopingMode(FusionRequest request)
    {
        if (request.Focus is not null)
            return "focus";
        if (request.Changes is not null)
            return "changes";
        return "none";
    }

    private void LogStageComplete(string stage, long elapsedMs, int fileCount, string? scopingMode = null)
    {
        if (scopingMode is not null)
        {
            _logger.LogInformation(
                "Fusion stage {Stage} complete: {FileCount} files, mode {ScopingMode}, {ElapsedMs} ms",
                stage,
                fileCount,
                scopingMode,
                elapsedMs);
            return;
        }

        _logger.LogInformation(
            "Fusion stage {Stage} complete: {FileCount} files, {ElapsedMs} ms",
            stage,
            fileCount,
            elapsedMs);
    }

    private void LogAnalysisIndexStats(Indexing.IAnalysisIndex? analysisIndex)
    {
        if (analysisIndex is null)
            return;

        _logger.LogInformation(
            "Analysis index during scoping: hits {IndexHits}, misses {IndexMisses}",
            analysisIndex.Statistics.Hits,
            analysisIndex.Statistics.Misses);
    }

    private void LogReductionComplete(long elapsedMs, int fileCount, IReductionCache? cache)
    {
        if (cache is null)
        {
            LogStageComplete("reduction", elapsedMs, fileCount);
            return;
        }

        _logger.LogInformation(
            "Fusion stage {Stage} complete: {FileCount} files, cache hits {CacheHits}, misses {CacheMisses}, {ElapsedMs} ms",
            "reduction",
            fileCount,
            cache.Statistics.Hits,
            cache.Statistics.Misses,
            elapsedMs);
    }

    private static FusionResult WithReductionCacheStats(FusionResult result, IReductionCache? cache)
    {
        if (cache is null)
            return result;

        return result with
        {
            ReductionCacheHits = cache.Statistics.Hits,
            ReductionCacheMisses = cache.Statistics.Misses,
        };
    }

    private FusionResult CreateEmptyChangeResult(FusionRequest request)
    {
        var since = request.Changes?.Since ?? "ref";
        var diagnostic = $"<!-- fuse: no files changed since {since} -->";

        return new FusionResult(
            [],
            request.InMemory ? diagnostic : null,
            0,
            0,
            0,
            TimeSpan.Zero,
            []);
    }

}
