using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Reduction.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fuse.Fusion.Stages;

/// <summary>
///     The scoping stage of the fusion pipeline: narrows the collected file set by focus or git changes.
/// </summary>
/// <remarks>
///     Open-ended task localization runs in <c>Fuse.Retrieval</c>; this stage applies only focus and change
///     scoping over the dependency graph.
/// </remarks>
public sealed class FusionScopingStage
{
    // The factor applied to a structural proximity neighbour's score, so it enters below a real type reference.
    private const double ProximityExpansionWeight = 0.5;

    private readonly DependencyGraphBuilder _dependencyGraphBuilder;
    private readonly FocusSeedResolver _focusSeedResolver;
    private readonly IChangeDetector _changeDetector;
    private readonly CapabilityRegistry<IDependencyExtractor> _dependencyExtractors;
    private readonly CapabilityRegistry<ITypeNameLocator> _typeNameLocators;
    private readonly CapabilityRegistry<ISymbolSliceExtractor> _sliceExtractors;
    private readonly ILogger<FusionScopingStage> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionScopingStage" /> class.
    /// </summary>
    public FusionScopingStage(
        DependencyGraphBuilder dependencyGraphBuilder,
        FocusSeedResolver focusSeedResolver,
        IChangeDetector changeDetector,
        CapabilityRegistry<IDependencyExtractor> dependencyExtractors,
        CapabilityRegistry<ITypeNameLocator> typeNameLocators,
        CapabilityRegistry<ISymbolSliceExtractor> sliceExtractors,
        ILogger<FusionScopingStage>? logger = null)
    {
        _dependencyGraphBuilder = dependencyGraphBuilder;
        _focusSeedResolver = focusSeedResolver;
        _changeDetector = changeDetector;
        _dependencyExtractors = dependencyExtractors;
        _typeNameLocators = typeNameLocators;
        _sliceExtractors = sliceExtractors;
        _logger = logger ?? NullLogger<FusionScopingStage>.Instance;
    }

    /// <summary>
    ///     Applies the scoping mode configured on <paramref name="request" /> to the collected files.
    /// </summary>
    /// <param name="request">The fusion request whose focus or change options drive scoping.</param>
    /// <param name="files">The collected candidate files.</param>
    /// <param name="parallelism">Maximum degree of parallelism for graph construction.</param>
    /// <param name="index">Optional host-memory analysis index for dependency extraction.</param>
    /// <param name="contentProvider">Run-scoped content provider for seed resolution and graph building.</param>
    /// <param name="experimental">Resolved experimental options for the run.</param>
    /// <param name="cancellationToken">Token used to cancel scoping work.</param>
    /// <returns>
    ///     The filtered file set, or <see langword="null" /> when change scoping matches no files.
    /// </returns>
    public async Task<FilteredFileSet?> FilterAsync(
        FusionRequest request,
        IReadOnlyList<SourceFile> files,
        int parallelism,
        Indexing.IAnalysisIndex? index,
        ISourceContentProvider contentProvider,
        ExperimentalOptions experimental,
        CancellationToken cancellationToken = default)
    {
        if (request.Focus is not null)
            return await FilterByFocusAsync(request, files, parallelism, index, contentProvider, experimental, cancellationToken);

        if (request.Changes is not null)
            return await FilterByChangesAsync(request, files, parallelism, index, contentProvider, cancellationToken);

        return new FilteredFileSet(files, null, null);
    }

    private async Task<FilteredFileSet> FilterByFocusAsync(
        FusionRequest request,
        IReadOnlyList<SourceFile> files,
        int parallelism,
        Indexing.IAnalysisIndex? index,
        ISourceContentProvider contentProvider,
        ExperimentalOptions experimental,
        CancellationToken cancellationToken)
    {
        var graph = await BuildGraphAsync(files, parallelism, index, contentProvider, cancellationToken);

        var seed = request.Focus!.Seed;
        var seedPaths = await _focusSeedResolver.ResolveSeedPathsAsync(
            seed, files, contentProvider, cancellationToken);

        string? sliceMember = null;
        if (seedPaths.Count == 0 && _sliceExtractors.TryResolve(".cs") is not null)
        {
            var dot = seed.LastIndexOf('.');
            if (dot > 0 && dot < seed.Length - 1)
            {
                var typeSeed = seed[..dot];
                var member = seed[(dot + 1)..];
                seedPaths = await _focusSeedResolver.ResolveSeedPathsAsync(
                    typeSeed, files, contentProvider, cancellationToken);
                if (seedPaths.Count > 0)
                    sliceMember = member;
            }
        }

        if (seedPaths.Count == 0)
        {
            throw new FusionValidationException(
                $"Focus seed '{request.Focus.Seed}' matched no collected files.");
        }

        var sliceRequest = sliceMember is null
            ? null
            : new SymbolSliceRequest(seedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase), sliceMember);

        var seedScores = seedPaths.ToDictionary(p => p, _ => 1.0, StringComparer.OrdinalIgnoreCase);
        var (focusProximityEdges, focusProximityWeight) =
            ResolveProximity(experimental, files, request.Collection.SourceDirectory);
        var options = new ExpansionOptions(
            request.Focus.Depth,
            FollowReferences: true,
            FollowDependents: true,
            Centrality: DependencyGraphCentrality.Compute(graph),
            CentralityWeight: experimental.CentralityWeight,
            ProximityEdges: focusProximityEdges,
            ProximityWeight: focusProximityWeight);

        var expansion = _focusSeedResolver.Expand(graph, seedScores, options);
        var filtered = files.Where(f => expansion.IncludedPaths.Contains(f.NormalizedRelativePath)).ToArray();
        return new FilteredFileSet(filtered, expansion.ProvenanceChains, expansion.Scores, Slice: sliceRequest);
    }

    private async Task<FilteredFileSet?> FilterByChangesAsync(
        FusionRequest request,
        IReadOnlyList<SourceFile> files,
        int parallelism,
        Indexing.IAnalysisIndex? index,
        ISourceContentProvider contentProvider,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> changedPaths;
        try
        {
            changedPaths = await _changeDetector.GetChangedRelativePathsAsync(
                request.Collection.SourceDirectory,
                request.Changes!.Since,
                cancellationToken);
        }
        catch (ChangeDetectionException ex)
        {
            throw new FusionException(ex.Message);
        }

        var filePathSet = files
            .Select(f => f.NormalizedRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matchedPaths = changedPaths
            .Where(filePathSet.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var changedOnly = matchedPaths.ToArray();

        IReadOnlyDictionary<string, IReadOnlyList<string>>? provenance = null;
        IReadOnlyDictionary<string, double>? scores = null;
        string? reviewPreamble = null;
        var review = request.Changes.Review;

        if ((request.Changes.IncludeDependents || review) && matchedPaths.Count > 0)
        {
            var graph = await BuildGraphAsync(files, parallelism, index, contentProvider, cancellationToken);

            if (review)
            {
                var diffs = await GetReviewDiffsAsync(request, filePathSet, cancellationToken);
                var callers = ChangeReviewBuilder.ComputeCallers(changedOnly, graph);
                reviewPreamble = ChangeReviewBuilder.Build(diffs, callers);
            }

            var seedScores = matchedPaths.ToDictionary(p => p, _ => 1.0, StringComparer.OrdinalIgnoreCase);
            var options = new ExpansionOptions(
                Depth: 1,
                FollowReferences: false,
                FollowDependents: true);

            var expansion = _focusSeedResolver.Expand(graph, seedScores, options);
            matchedPaths = expansion.IncludedPaths;
            provenance = expansion.ProvenanceChains;
            scores = expansion.Scores;
        }

        if (matchedPaths.Count == 0)
            return null;

        var filtered = files.Where(f => matchedPaths.Contains(f.NormalizedRelativePath)).ToArray();
        return new FilteredFileSet(filtered, provenance, scores, reviewPreamble);
    }

    private async Task<IReadOnlyList<FileDiff>> GetReviewDiffsAsync(
        FusionRequest request,
        HashSet<string> collectedPaths,
        CancellationToken cancellationToken)
    {
        try
        {
            var diffs = await _changeDetector.GetDiffsAsync(
                request.Collection.SourceDirectory,
                request.Changes!.Since,
                cancellationToken);
            return diffs.Where(d => collectedPaths.Contains(d.Path)).ToArray();
        }
        catch (ChangeDetectionException ex)
        {
            throw new FusionException(ex.Message);
        }
    }

    private Task<DependencyGraph> BuildGraphAsync(
        IReadOnlyList<SourceFile> files,
        int parallelism,
        Indexing.IAnalysisIndex? index,
        ISourceContentProvider contentProvider,
        CancellationToken cancellationToken) =>
        _dependencyGraphBuilder.BuildAsync(
            files,
            contentProvider,
            _dependencyExtractors,
            _typeNameLocators,
            parallelism,
            cancellationToken,
            index);

    private (IReadOnlyDictionary<string, IReadOnlyList<string>>? Edges, double Weight) ResolveProximity(
        ExperimentalOptions experimental,
        IReadOnlyList<SourceFile> files,
        string sourceRoot)
    {
        if (!experimental.ProximityEdges && !experimental.ProjectGraph)
            return (null, 0.0);

        var paths = files.Select(f => f.NormalizedRelativePath).ToList();
        IReadOnlyDictionary<string, IReadOnlyList<string>>? proximity =
            experimental.ProximityEdges ? ProximityEdgeBuilder.Build(paths) : null;
        IReadOnlyDictionary<string, IReadOnlyList<string>>? project =
            experimental.ProjectGraph ? ProjectGraphEdgeBuilder.Build(sourceRoot, paths, _logger) : null;

        if (project is null || project.Count == 0)
            return (proximity, ProximityExpansionWeight);
        if (proximity is null || proximity.Count == 0)
            return (project, ProximityExpansionWeight);

        var merged = new Dictionary<string, IReadOnlyList<string>>(proximity, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, projectNeighbours) in project)
        {
            if (merged.TryGetValue(path, out var existing))
            {
                var union = new SortedSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                union.UnionWith(projectNeighbours);
                merged[path] = union.ToList();
            }
            else
            {
                merged[path] = projectNeighbours;
            }
        }

        return (merged, ProximityExpansionWeight);
    }
}
