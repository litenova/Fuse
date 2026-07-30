using Fuse.Indexing;
using Fuse.Scoping;

namespace Fuse.Retrieval;

/// <summary>
///     A structural prior that nudges an ambiguous candidate by how central its file is in the semantic graph,
///     so a widely-depended-on file outranks a leaf for an otherwise-tied query. The prior is a small, capped
///     multiplier on the existing score, so it tunes the ranking rather than dominating it: it cannot promote an
///     irrelevant (near-zero score) file on centrality alone.
/// </summary>
/// <remarks>
///     Centrality is normalized node degree (in plus out edges) over the semantic graph. In syntax mode the
///     graph has no edges, so the prior is empty and scoring is unchanged; the prior only moves results where a
///     real graph exists (partial or semantic mode). It reads over the language-agnostic node and edge tables.
/// </remarks>
public sealed class GraphCentralityPrior
{
    /// <summary>The maximum fractional boost a fully-central file receives (a capped, tuning-only multiplier).</summary>
    public const double CentralityWeight = GraphCentrality.RetrievalCentralityWeight;

    // Apply the prior only to the leading candidates that could enter the returned set, to bound the per-file
    // node lookups for file-only candidates.
    private const int MaxCandidatesToAdjust = 30;

    private readonly IWorkspaceIndexGraphStore _store;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GraphCentralityPrior" /> class.
    /// </summary>
    /// <param name="store">The index store whose nodes and edges define the centrality.</param>
    public GraphCentralityPrior(IWorkspaceIndexGraphStore store) => _store = store;

    /// <summary>
    ///     Applies the centrality multiplier to a ranked candidate set and re-sorts. In syntax mode (no edges)
    ///     the input is returned unchanged.
    /// </summary>
    /// <param name="ranked">The scored candidates, highest score first.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The candidates with centrality blended in, re-sorted by score then path.</returns>
    public async Task<IReadOnlyList<ScoredCandidate>> ApplyAsync(
        IReadOnlyList<ScoredCandidate> ranked, CancellationToken cancellationToken)
    {
        if (ranked.Count == 0)
            return ranked;

        var degree = await NormalizedDegreeAsync(cancellationToken);
        if (degree.Count == 0)
            return ranked;

        var adjusted = new List<ScoredCandidate>(ranked.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            var candidate = ranked[i];
            if (i >= MaxCandidatesToAdjust)
            {
                adjusted.Add(candidate);
                continue;
            }

            var centrality = await CentralityForAsync(candidate, degree, cancellationToken);
            var boosted = GraphCentrality.ApplyRetrievalPrior(candidate.Score, centrality);
            adjusted.Add(candidate with { Score = boosted });
        }

        return adjusted
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.FilePath, StringComparer.Ordinal)
            .ToList();
    }

    // Centrality of a candidate: its node's normalized degree when it is a node candidate, else the maximum
    // degree over the nodes declared in its file (a file is as central as its most-connected declaration).
    private async Task<double> CentralityForAsync(
        ScoredCandidate candidate, IReadOnlyDictionary<string, double> degree, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(candidate.NodeId) && degree.TryGetValue(candidate.NodeId, out var direct))
            return direct;

        if (string.IsNullOrEmpty(candidate.FilePath))
            return 0.0;

        var best = 0.0;
        foreach (var node in await _store.GetNodesByFileAsync(candidate.FilePath, cancellationToken))
        {
            if (degree.TryGetValue(node.NodeId, out var d) && d > best)
                best = d;
        }

        return best;
    }

    private async Task<IReadOnlyDictionary<string, double>> NormalizedDegreeAsync(CancellationToken cancellationToken)
    {
        var edges = await _store.GetAllEdgesAsync(cancellationToken);
        if (edges.Count == 0)
            return EmptyDegree;

        return GraphCentrality.NormalizedDegree(
            edges.Select(edge => (edge.FromNodeId, edge.ToNodeId)));
    }

    private static readonly IReadOnlyDictionary<string, double> EmptyDegree =
        new Dictionary<string, double>(StringComparer.Ordinal);
}
