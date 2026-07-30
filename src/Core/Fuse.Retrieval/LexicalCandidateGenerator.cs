using Fuse.Indexing;

namespace Fuse.Retrieval;

/// <summary>
///     Generates candidates from the persistent BM25F full-text index, preserving the lexical rank so a
///     better lexical match scores higher, and expanding the query with pseudo-relevance feedback (PRF).
/// </summary>
/// <remarks>
///     The store ranks chunk hits with field-weighted BM25 (path, name, symbols, signature, comments, body).
///     The earlier full-text generator discarded that rank, giving every hit a flat per-source weight, so the
///     lexical ordering was lost at merge and truncation. This generator instead carries a rank-decayed score:
///     the best in-pool file keeps its band ceiling (a name-field match outranks a body-only match), and the
///     score decays with rank down to a floor, so the lexical order survives the noisy-or merge. After the
///     first pass it runs one round of pseudo-relevance feedback: the distinctive symbol names of the top
///     files seed an expanded query that surfaces files sharing the same vocabulary, added as a weaker signal.
/// </remarks>
public sealed class LexicalCandidateGenerator : ICandidateGenerator
{
    // The retrieval pool is wider than the requested candidate count so files ranked just outside the top
    // still compete after the noisy-or merge with the other generators.
    private const int PoolFactor = 4;
    private const int MinPool = 80;

    // PRF: the top files whose names seed expansion, the maximum expansion terms, the score discount for a
    // file found only through the expanded query (a weaker, vocabulary-level signal), and a hard cap on how
    // many expansion-only files are added so the broadened query lifts recall without flooding precision.
    private const int PrfTopFiles = 5;
    private const int PrfMaxTerms = 6;
    private const double PrfCeilingFactor = 0.6;
    private const int PrfMaxNewFiles = 8;

    // The weakest in-pool hit keeps this fraction of its band ceiling, so rank decay never zeroes a real match.
    private const double RankFloor = 0.35;

    private readonly IWorkspaceIndexQueryStore _store;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LexicalCandidateGenerator" /> class.
    /// </summary>
    /// <param name="store">The index store to query.</param>
    public LexicalCandidateGenerator(IWorkspaceIndexQueryStore store) => _store = store;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return [];

        var poolSize = Math.Max(request.MaxCandidates * PoolFactor, MinPool);
        var hits = await _store.SearchAsync(new SearchQuery(request.Query, poolSize), cancellationToken);
        if (hits.Count == 0)
            return [];

        var queryTokens = Tokenize(request.Query);
        var candidates = new List<CandidateNode>(AggregateAndScore(hits, queryTokens, isPrf: false));

        // Pseudo-relevance feedback: expand the query with distinctive symbol names from the top files, then
        // add files the expanded query surfaces that the first pass did not.
        var expansion = BuildExpansionTerms(hits, queryTokens);
        if (expansion.Count > 0)
        {
            var expandedQuery = request.Query + " " + string.Join(' ', expansion);
            var prfHits = await _store.SearchAsync(new SearchQuery(expandedQuery, poolSize), cancellationToken);
            var seen = candidates.Select(c => c.FilePath).ToHashSet(StringComparer.Ordinal);
            var added = 0;
            // AggregateAndScore returns files in rank order, so taking the first new ones keeps the strongest
            // expansion matches and drops the long, low-signal tail that would hurt precision.
            foreach (var candidate in AggregateAndScore(prfHits, Tokenize(expandedQuery), isPrf: true))
            {
                if (added >= PrfMaxNewFiles)
                    break;
                if (seen.Add(candidate.FilePath))
                {
                    candidates.Add(candidate);
                    added++;
                }
            }
        }

        return candidates;
    }

    // Collapses chunk hits to one candidate per file. The first time a file appears in the BM25-ordered hit
    // list is its rank; the score decays from the band ceiling at rank 0 to RankFloor of it at the last rank.
    private static List<CandidateNode> AggregateAndScore(
        IReadOnlyList<SearchHit> hits, IReadOnlyList<string> queryTokens, bool isPrf)
    {
        var order = new List<string>();
        var nameMatch = new Dictionary<string, bool>(StringComparer.Ordinal);
        var label = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            if (!nameMatch.ContainsKey(hit.FilePath))
            {
                order.Add(hit.FilePath);
                nameMatch[hit.FilePath] = false;
                label[hit.FilePath] = hit.Name ?? hit.Kind;
            }

            if (hit.Name is not null && MatchesAnyToken(queryTokens, hit.Name))
                nameMatch[hit.FilePath] = true;
        }

        var candidates = new List<CandidateNode>(order.Count);
        var denominator = Math.Max(1, order.Count - 1);
        for (var rank = 0; rank < order.Count; rank++)
        {
            var path = order[rank];
            var isNameMatch = nameMatch[path];
            var source = isNameMatch ? CandidateSource.FtsSymbol : CandidateSource.FtsBody;
            var ceiling = CandidateSourceWeights.Weight(source) * (isPrf ? PrfCeilingFactor : 1.0);
            var decay = 1.0 - (double)rank / denominator * (1.0 - RankFloor);
            var reason = isPrf
                ? $"lexical (PRF) match: {label[path]} (rank {rank + 1})"
                : $"lexical match: {label[path]} (rank {rank + 1})";

            candidates.Add(new CandidateNode(
                NodeId: string.Empty,
                FilePath: path,
                Kind: isNameMatch ? "symbol" : "file",
                BaseScore: ceiling * decay,
                Source: source,
                Reasons: [reason],
                TokenEstimate: 0));
        }

        return candidates;
    }

    // Collects the distinctive symbol names of the top files as expansion terms: the names that are not already
    // query tokens, ordered by how often they appear among the top hits, capped at PrfMaxTerms.
    private static IReadOnlyList<string> BuildExpansionTerms(IReadOnlyList<SearchHit> hits, IReadOnlyList<string> queryTokens)
    {
        var topFiles = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits)
        {
            if (topFiles.Count >= PrfTopFiles && !topFiles.Contains(hit.FilePath))
                continue;
            topFiles.Add(hit.FilePath);
            if (hit.Name is null)
                continue;

            foreach (var token in Tokenize(hit.Name))
            {
                if (token.Length < 3 || queryTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                    continue;
                counts[token] = counts.GetValueOrDefault(token) + 1;
            }
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(PrfMaxTerms)
            .Select(kv => kv.Key)
            .ToList();
    }

    private static bool MatchesAnyToken(IReadOnlyList<string> queryTokens, string name) =>
        queryTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

    // Splits on non-alphanumeric boundaries and on camelCase humps, so "OrderService" yields order and service.
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char previous = '\0';
        foreach (var ch in text)
        {
            var isBoundary = !char.IsLetterOrDigit(ch);
            var isHump = char.IsUpper(ch) && char.IsLower(previous);
            if (isHump && current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }

            if (isBoundary)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }

            previous = ch;
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }
}
