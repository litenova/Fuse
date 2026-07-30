using Fuse.Indexing;
using Fuse.Scoping;

namespace Fuse.Retrieval;

/// <summary>
///     The retrieval engine: localizes a task to ranked candidates, and plans a context payload by expanding
///     seeds across the semantic graph and packing the result under a token budget.
/// </summary>
/// <remarks>
///     This replaces the old query-scoping pipeline. Localization is candidate generation plus normalization
///     (no graph walk, no bodies). Context planning resolves seeds to graph nodes, expands typed edges, collapses
///     nodes to files, assigns a role and render tier per file, and greedily packs must-keep files first.
/// </remarks>
public sealed class SemanticRetrievalEngine
{
    private const double ExpansionThreshold = 0.10;

    // The most candidates a non-confident (partial or best-effort insufficient) result returns. Smaller than the
    // request cap so a low-confidence answer is a short, scannable set rather than a long, noisy one; the
    // confident path returns only its leading cluster, which is typically smaller still.
    private const int PartialCandidateLimit = 10;

    private const string InsufficientAsk =
        "No candidate stands clear. Provide a symbol, route, service, request, config section, git base, or a narrower description.";

    private const string PartialAsk =
        "No candidate stands clear; this is a low-confidence best effort. Refine with a symbol, route, service, request, config section, or git base.";

    // Graph-aware discovery bounds: expand only the top seeds, one hop, and admit at most this many new neighbor
    // files, so a single weak seed cannot pull in a large subtree.
    private const int GraphSeedCount = 2;
    private const int GraphExpansionDepth = 1;
    private const int GraphMaxNeighbors = 5;

    private readonly IWorkspaceIndexStore _store;
    private readonly IChangeSource? _changeSource;
    private readonly CandidateGenerator _candidateGenerator;
    private readonly CandidateScorer _scorer;
    private readonly GraphExpansionEngine _expansion;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticRetrievalEngine" /> class.
    /// </summary>
    /// <param name="store">The index store to query.</param>
    /// <param name="changeSource">An optional change source so <c>ChangedSince</c> resolves to changed-file seeds.</param>
    public SemanticRetrievalEngine(IWorkspaceIndexStore store, IChangeSource? changeSource = null)
    {
        _store = store;
        _changeSource = changeSource;
        _candidateGenerator = CandidateGenerator.CreateDefault(store, changeSource);
        _scorer = new CandidateScorer();
        _expansion = new GraphExpansionEngine(store, new EdgeWeightProvider());
    }

    /// <summary>
    ///     Localizes a task to ranked candidate files and symbols, with no source bodies.
    /// </summary>
    /// <param name="request">The localization request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The ranked candidates and any warnings.</returns>
    public async Task<LocalizationResult> LocalizeAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        // Step 1: the low-signal classifier. A request that carries no usable scoping signal (merge/dependency/CI
        // noise, or an empty query with no structured input) names no code, so generating candidates would only
        // return junk. Refuse and route: hand back the structural map and ask for an anchor. This is the
        // insufficient state by classification, kept distinct from the score-distribution insufficiency below so
        // the low-signal detection metric (which scores this classifier) is not contaminated by weak-match cases.
        var verdict = QuerySignalClassifier.Classify(request);
        if (verdict.IsLowSignal)
        {
            var ask = verdict.Suggestion ?? InsufficientAsk;
            var navigation = await new NavigationMapBuilder(_store).BuildAsync(request, [], ask, cancellationToken);
            return new LocalizationResult(
                [], [$"Low signal: {ask}"], LowSignal: true, SuggestedInput: ask,
                State: SignalState.Insufficient, Navigation: navigation);
        }

        // Step 2: generate, score, blend the structural priors, and grade the distribution into the contract.
        var candidates = await _candidateGenerator.GenerateAsync(request, cancellationToken);
        var scored = _scorer.Score(candidates);
        // Dependency-centrality prior: a widely-depended-on file outranks a leaf for an otherwise-tied query. A
        // small capped multiplier, empty (no-op) in syntax mode where the graph has no edges. Gated so the ranking
        // suite can score the base channels in isolation.
        if (request.EnableCentralityPrior)
            scored = await new GraphCentralityPrior(_store).ApplyAsync(scored, cancellationToken);
        var state = SignalGrader.Grade(scored);

        // Select the returned set by state. Confident returns only the leading cluster (the precision win); partial
        // returns a small flagged best-effort set; insufficient returns nothing under strict mode (a hard anchor
        // requirement) and a best-effort set under the graceful default, so a client that cannot refine still gets
        // something. The LowSignal flag stays false here: this is a graded outcome, not a no-signal title.
        IReadOnlyList<ScoredCandidate> selected = state switch
        {
            SignalState.Confident => SignalGrader.LeadingCluster(scored),
            SignalState.Partial => scored.Take(PartialCandidateLimit).ToList(),
            _ => request.Strict ? [] : scored.Take(PartialCandidateLimit).ToList(),
        };
        // Graph-aware discovery (opt-in): enrich the selected set with the typed-graph neighbors of its seeds (the
        // implementers, callers, and configuration of what was matched), at a decayed score and bounded by seed
        // count, depth, and neighbor cap. Off by default because the blast radius widens recall but pressures
        // precision; a no-op in syntax mode and when nothing was selected (strict refusal).
        if (request.ExpandGraph)
            selected = await ExpandSeedsThroughGraphAsync(selected, cancellationToken);
        // Never return two copies of the same file: collapse byte-identical candidates to the highest-scored one.
        // A safety net for any duplication that escapes index-time exclusion (worktrees, backups, vendored copies).
        selected = await DeduplicateByContentAsync(selected, cancellationToken);
        selected = selected.Take(request.MaxCandidates).ToList();

        var warnings = new List<string>();
        NavigationMap? navigationMap = null;
        if (state != SignalState.Confident)
        {
            var ask = state == SignalState.Insufficient ? InsufficientAsk : PartialAsk;
            navigationMap = await new NavigationMapBuilder(_store).BuildAsync(request, scored, ask, cancellationToken);
            warnings.Add(state switch
            {
                SignalState.Insufficient when request.Strict => $"Insufficient signal (strict): refused, no candidates returned. {ask}",
                SignalState.Insufficient when selected.Count == 0 => $"Insufficient signal: no candidate cleared the bar. {ask}",
                SignalState.Insufficient => $"Insufficient signal: returning a best-effort set, low confidence. {ask}",
                _ => $"Partial signal: returning a best-effort set, low confidence. {ask}",
            });
        }

        var localized = new List<LocalizedCandidate>(selected.Count);
        foreach (var candidate in selected)
        {
            var tokens = await _store.GetFileTokenEstimateAsync(candidate.FilePath, cancellationToken);
            localized.Add(new LocalizedCandidate(
                candidate.FilePath, candidate.NodeId, candidate.Kind, candidate.Score, tokens, candidate.Reasons));
        }

        return new LocalizationResult(localized, warnings, LowSignal: false, SuggestedInput: null, State: state, Navigation: navigationMap);
    }

    // Collapses candidates whose files are byte-identical (same content hash) to the first, which is the
    // highest-scored one in the incoming order. Candidates with no file or no indexed hash pass through. Keeps
    // retrieval from ever surfacing N copies of one file, independent of how the duplication got into the index.
    private async Task<IReadOnlyList<ScoredCandidate>> DeduplicateByContentAsync(
        IReadOnlyList<ScoredCandidate> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count < 2)
            return candidates;

        var paths = candidates
            .Where(c => !string.IsNullOrEmpty(c.FilePath))
            .Select(c => c.FilePath)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0)
            return candidates;

        var hashes = await _store.GetContentHashesAsync(paths, cancellationToken);
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ScoredCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate.FilePath)
                && hashes.TryGetValue(candidate.FilePath, out var hash)
                && !string.IsNullOrEmpty(hash)
                && !seenHashes.Add(hash))
            {
                continue; // A byte-identical file is already represented by a higher-scored candidate.
            }

            result.Add(candidate);
        }

        return result;
    }

    // Graph-aware discovery: expand the top seeds one hop through the typed semantic graph and admit a capped
    // set of new neighbor files at their decayed expansion score, so a weak lexical hit on one file pulls in its
    // implementers, callers, and configuration. Bounded by seed count, depth, and neighbor cap. In syntax mode
    // the graph has no edges, so expansion returns only the seeds and the set is unchanged.
    private async Task<IReadOnlyList<ScoredCandidate>> ExpandSeedsThroughGraphAsync(
        IReadOnlyList<ScoredCandidate> scored, CancellationToken cancellationToken)
    {
        if (scored.Count == 0)
            return scored;

        // A node candidate seeds itself; a file-only (lexical) candidate seeds the nodes its file declares, so a
        // text hit can still traverse the graph.
        var seeds = new List<ScoredCandidate>();
        foreach (var candidate in scored.Take(GraphSeedCount))
        {
            if (!string.IsNullOrEmpty(candidate.NodeId))
            {
                seeds.Add(candidate);
                continue;
            }

            if (string.IsNullOrEmpty(candidate.FilePath))
                continue;
            foreach (var node in await _store.GetNodesByFileAsync(candidate.FilePath, cancellationToken))
                seeds.Add(candidate with { NodeId = node.NodeId });
        }

        if (seeds.Count == 0)
            return scored;

        var expanded = await _expansion.ExpandAsync(seeds, GraphExpansionDepth, ExpansionThreshold, cancellationToken);

        var present = new HashSet<string>(
            scored.Where(c => !string.IsNullOrEmpty(c.FilePath)).Select(c => c.FilePath), StringComparer.Ordinal);
        var additions = new List<ScoredCandidate>();
        foreach (var node in expanded
            .Where(n => n.Hop > 0 && !string.IsNullOrEmpty(n.FilePath) && !present.Contains(n.FilePath!))
            .OrderByDescending(n => n.Score))
        {
            if (additions.Count >= GraphMaxNeighbors)
                break;
            if (!present.Add(node.FilePath!))
                continue;
            additions.Add(new ScoredCandidate(
                node.NodeId, node.FilePath!, node.Kind, node.Score, [CandidateSource.GraphNeighbor],
                [$"graph neighbor: {string.Join(' ', node.Provenance)}".Trim()], 0));
        }

        if (additions.Count == 0)
            return scored;

        return scored.Concat(additions)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.FilePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     Plans a context payload around a set of seeds.
    /// </summary>
    /// <param name="request">The context request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The context plan.</returns>
    public async Task<ContextPlan> PlanContextAsync(ContextRequest request, CancellationToken cancellationToken)
    {
        var seedCandidates = await BuildSeedCandidatesAsync(request.Seeds, cancellationToken);
        return await BuildPlanAsync(
            "context", seedCandidates, changedPaths: null, request.Depth, request.MaxTokens,
            request.RenderMode, request.IncludeTests, request.IncludeConfig, [], cancellationToken);
    }

    /// <summary>
    ///     Builds a review plan: changed files are must-keep seeds, and the semantic blast radius (callers, DI
    ///     consumers, route handlers, request handlers, options consumers, tests) is expanded around them.
    /// </summary>
    /// <param name="request">The review request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The review context plan; changed files carry the role <c>changed</c>.</returns>
    public async Task<ContextPlan> ReviewAsync(ReviewRequest request, CancellationToken cancellationToken)
    {
        if (_changeSource is null)
            return new ContextPlan("review", [], [], 0, ["No change source available; review requires git."]);

        IReadOnlyList<string> changed;
        try
        {
            changed = await _changeSource.GetChangedFilesAsync(request.RootDirectory, request.ChangedSince, cancellationToken);
        }
        catch (ChangeSourceException ex)
        {
            return new ContextPlan("review", [], [], 0, [ex.Message]);
        }

        var changedSet = changed.Select(p => p.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var seeds = new List<CandidateNode>();
        foreach (var path in changedSet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep the file itself (handles files with no graph nodes, for example config json), and seed every
            // node declared in the changed file so the blast radius expands from each changed symbol.
            seeds.Add(new CandidateNode(string.Empty, path, "file", 1.0, CandidateSource.DiffChangedFile, ["changed file"], 0));
            foreach (var node in await _store.GetNodesByFileAsync(path, cancellationToken))
                seeds.Add(new CandidateNode(node.NodeId, node.FilePath ?? path, node.Kind, 1.0, CandidateSource.DiffChangedFile, ["changed symbol"], 0));
        }

        var warnings = new List<string>();
        if (changedSet.Count == 0)
            warnings.Add("No changed files since " + request.ChangedSince + ".");

        return await BuildPlanAsync(
            "review", seeds, changedSet, request.Depth, request.MaxTokens,
            ContextRenderMode.Mixed, request.IncludeTests, request.IncludeConfig, warnings, cancellationToken);
    }

    // Shared planner: score seeds, expand the graph, collapse to files, assign role and render tier, and pack
    // under the token budget. When changedPaths is supplied, files in that set are labeled "changed".
    private async Task<ContextPlan> BuildPlanAsync(
        string mode,
        IReadOnlyList<CandidateNode> seedCandidates,
        IReadOnlySet<string>? changedPaths,
        int depth,
        int? maxTokens,
        ContextRenderMode renderMode,
        bool includeTests,
        bool includeConfig,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var scoredSeeds = _scorer.Score(seedCandidates);
        var expanded = await _expansion.ExpandAsync(scoredSeeds, depth, ExpansionThreshold, cancellationToken);

        var byFile = expanded
            .Where(n => !string.IsNullOrEmpty(n.FilePath))
            .GroupBy(n => n.FilePath!, StringComparer.Ordinal);

        var items = new List<ContextPlanItem>();
        foreach (var group in byFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var best = group.OrderByDescending(n => n.Score).First();
            var role = changedPaths?.Contains(group.Key) == true ? "changed" : RoleFor(best);
            if (role == "test" && !includeTests)
                continue;
            if (role == "config" && !includeConfig)
                continue;

            var mustKeep = group.Any(n => n.MustKeep);
            var tokens = await _store.GetFileTokenEstimateAsync(group.Key, cancellationToken);
            items.Add(new ContextPlanItem(
                Path: group.Key,
                NodeId: string.IsNullOrEmpty(best.NodeId) ? null : best.NodeId,
                Role: role,
                Tier: TierFor(role, renderMode),
                Score: best.Score,
                EstimatedTokens: tokens,
                MustKeep: mustKeep,
                Reasons: best.Provenance,
                ProvenanceChain: best.Provenance));
        }

        var packed = ContextPlanPacker.Pack(items, maxTokens, warnings);
        var estimated = packed.Sum(i => i.EstimatedTokens);
        return new ContextPlan(mode, packed, [], estimated, warnings);
    }

    private async Task<List<CandidateNode>> BuildSeedCandidatesAsync(IReadOnlyList<ContextSeed> seeds, CancellationToken cancellationToken)
    {
        var candidates = new List<CandidateNode>();
        foreach (var seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (seed.Kind)
            {
                case ContextSeedKind.File:
                    var filePath = seed.Value.Replace('\\', '/');
                    candidates.Add(new CandidateNode(
                        string.Empty, filePath, "file", 1.0,
                        CandidateSource.DiffChangedFile, ["seed: file"], 0));
                    // Seed the file's declared nodes too, so a file seed (for example a path from localize)
                    // expands across the graph rather than being included in isolation.
                    foreach (var node in await _store.GetNodesByFileAsync(filePath, cancellationToken))
                        candidates.Add(new CandidateNode(
                            node.NodeId, node.FilePath ?? filePath, node.Kind, 1.0,
                            CandidateSource.SymbolExact, ["seed: file symbol"], 0));
                    break;
                case ContextSeedKind.Route:
                    await AddRouteSeedAsync(candidates, seed.Value, cancellationToken);
                    break;
                default:
                    await AddNamedSeedAsync(candidates, seed.Value, cancellationToken);
                    break;
            }
        }

        return candidates;
    }

    private async Task AddNamedSeedAsync(List<CandidateNode> candidates, string value, CancellationToken cancellationToken)
    {
        var nodes = await _store.FindNodesByDisplayNameAsync(SimpleName(value), cancellationToken);
        foreach (var node in nodes)
        {
            candidates.Add(new CandidateNode(
                node.NodeId, node.FilePath ?? string.Empty, node.Kind, 1.0,
                CandidateSource.SymbolExact, [$"seed: {value}"], 0));
        }
    }

    private async Task AddRouteSeedAsync(List<CandidateNode> candidates, string route, CancellationToken cancellationToken)
    {
        var (method, pattern) = ParseRoute(route);
        var routeNodeId = $"route:{method}:{pattern}";
        var node = await _store.GetNodeAsync(routeNodeId, cancellationToken);
        if (node is not null)
        {
            candidates.Add(new CandidateNode(
                node.NodeId, node.FilePath ?? string.Empty, node.Kind, 1.0,
                CandidateSource.RouteExact, [$"seed: route {route}"], 0));
        }
    }

    private static string RoleFor(ExpandedNode node)
    {
        if (node.MustKeep)
            return "exact-seed";

        var edgeType = LastEdgeType(node.Provenance);
        return edgeType switch
        {
            "route_handles" => "route-handler",
            "mediatr_handles" => "request-handler",
            "di_resolves_to" or "di_depends_on_impl" => "di-implementation",
            "di_injects" or "sends_request" => "consumer",
            "options_binds" or "options_consumes" or "config_impacts" => "config",
            "tests" => "test",
            _ => "dependency",
        };
    }

    private static RenderTier TierFor(string role, ContextRenderMode mode) => mode switch
    {
        ContextRenderMode.Source => RenderTier.FullSource,
        ContextRenderMode.Reduced => RenderTier.Reduced,
        ContextRenderMode.Skeleton => RenderTier.Skeleton,
        ContextRenderMode.PublicApi => RenderTier.PublicApi,
        _ => role switch
        {
            "changed" or "exact-seed" or "route-handler" or "request-handler" or "di-implementation" => RenderTier.Reduced,
            "config" => RenderTier.FullSource,
            "consumer" or "test" or "dependency" => RenderTier.Skeleton,
            _ => RenderTier.Skeleton,
        },
    };

    private static string? LastEdgeType(IReadOnlyList<string> provenance)
    {
        for (var i = provenance.Count - 1; i >= 0; i--)
        {
            // Provenance entries from expansion read "-> edge_type (hop n)" or "<- edge_type (hop n)".
            var parts = provenance[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && (parts[0] == "->" || parts[0] == "<-"))
                return parts[1];
        }

        return null;
    }

    private static string SimpleName(string name)
    {
        var trimmed = name.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        return lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed;
    }

    private static (string Method, string Pattern) ParseRoute(string route)
    {
        var trimmed = route.Trim();
        var space = trimmed.IndexOf(' ');
        if (space < 0)
            return ("GET", Normalize(trimmed));
        return (trimmed[..space].Trim().ToUpperInvariant(), Normalize(trimmed[(space + 1)..].Trim()));
    }

    private static string Normalize(string pattern)
    {
        if (pattern.Length == 0)
            return "/";
        if (!pattern.StartsWith('/'))
            pattern = "/" + pattern;
        return pattern.Length > 1 ? pattern.TrimEnd('/') : pattern;
    }
}
