using Fuse.Indexing;

namespace Fuse.Retrieval;

/// <summary>
///     Expands a set of seed candidates across the semantic graph, following typed edges with weighted,
///     decaying priority and pruning low-scoring branches.
/// </summary>
/// <remarks>
///     A max-priority frontier pops the highest-scoring node first, so the first time a node is reached is via
///     its best path (child scores only shrink, by edge weight and per-hop decay, so this is Dijkstra-like).
///     A child's score is <c>parentScore * edgeWeight * decay * ambiguityPenalty</c>, where the ambiguity
///     penalty (<c>1 / sqrt(fanout)</c>) down-weights hub nodes that fan out to many same-typed neighbors.
///     Both outgoing and incoming edges are traversed so, for example, an interface seed reaches its
///     implementations. Seeds are must-keep; expanded nodes below the threshold are pruned.
/// </remarks>
public sealed class GraphExpansionEngine
{
    private readonly IWorkspaceIndexGraphStore _store;
    private readonly EdgeWeightProvider _weights;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GraphExpansionEngine" /> class.
    /// </summary>
    /// <param name="store">The index store to traverse.</param>
    /// <param name="weights">The edge weight provider.</param>
    public GraphExpansionEngine(IWorkspaceIndexGraphStore store, EdgeWeightProvider weights)
    {
        _store = store;
        _weights = weights;
    }

    /// <summary>
    ///     Expands the seeds across the graph.
    /// </summary>
    /// <param name="seeds">The ranked seed candidates.</param>
    /// <param name="depth">The maximum number of hops from a seed.</param>
    /// <param name="threshold">The minimum score for an expanded (non-seed) node to be kept.</param>
    /// <param name="cancellationToken">A token to cancel the expansion.</param>
    /// <returns>The seeds and the expanded nodes, ordered by score descending.</returns>
    public async Task<IReadOnlyList<ExpandedNode>> ExpandAsync(
        IReadOnlyList<ScoredCandidate> seeds,
        int depth,
        double threshold,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ExpandedNode>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        // Min-heap keyed by negative score so the highest score pops first.
        var frontier = new PriorityQueue<FrontierEntry, double>();

        foreach (var seed in seeds)
        {
            if (string.IsNullOrEmpty(seed.NodeId))
            {
                // File-only seed: keep it directly; it cannot be expanded without a node.
                var key = "file:" + seed.FilePath;
                result[key] = new ExpandedNode(string.Empty, seed.FilePath, seed.Kind, seed.Score, 0, seed.Reasons, MustKeep: true);
                continue;
            }

            frontier.Enqueue(new FrontierEntry(seed.NodeId, seed.Score, 0, seed.Reasons, MustKeep: true), -seed.Score);
        }

        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = frontier.Dequeue();
            if (!visited.Add(entry.NodeId))
                continue;

            var node = await _store.GetNodeAsync(entry.NodeId, cancellationToken);
            result[entry.NodeId] = new ExpandedNode(
                entry.NodeId, node?.FilePath, node?.Kind ?? "node", entry.Score, entry.Hop, entry.Provenance, entry.MustKeep);

            if (entry.Hop >= depth)
                continue;

            await EnqueueNeighborsAsync(frontier, visited, entry, threshold, cancellationToken);
        }

        return result.Values.OrderByDescending(n => n.Score).ThenBy(n => n.FilePath, StringComparer.Ordinal).ToList();
    }

    private async Task EnqueueNeighborsAsync(
        PriorityQueue<FrontierEntry, double> frontier,
        HashSet<string> visited,
        FrontierEntry entry,
        double threshold,
        CancellationToken cancellationToken)
    {
        var outgoing = await _store.GetOutgoingEdgesAsync(entry.NodeId, cancellationToken);
        var incoming = await _store.GetIncomingEdgesAsync(entry.NodeId, cancellationToken);

        // Fan-out per edge type drives the ambiguity penalty: a node pointing to many same-typed neighbors
        // (a hub interface, a common base) makes each individual neighbor a weaker signal.
        var fanout = outgoing.Concat(incoming)
            .GroupBy(e => e.EdgeType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var edge in outgoing)
            Enqueue(frontier, visited, entry, edge.EdgeType, edge.ToNodeId, fanout, threshold, isOutgoing: true);
        foreach (var edge in incoming)
            Enqueue(frontier, visited, entry, edge.EdgeType, edge.FromNodeId, fanout, threshold, isOutgoing: false);
    }

    private void Enqueue(
        PriorityQueue<FrontierEntry, double> frontier,
        HashSet<string> visited,
        FrontierEntry parent,
        string edgeType,
        string neighborId,
        IReadOnlyDictionary<string, int> fanout,
        double threshold,
        bool isOutgoing)
    {
        if (visited.Contains(neighborId))
            return;

        var ambiguityPenalty = 1.0 / Math.Sqrt(fanout.GetValueOrDefault(edgeType, 1));
        var score = parent.Score * _weights.Weight(edgeType) * _weights.HopDecay * ambiguityPenalty;
        if (score < threshold)
            return;

        var direction = isOutgoing ? "->" : "<-";
        var provenance = parent.Provenance.Append($"{direction} {edgeType} (hop {parent.Hop + 1})").ToList();
        frontier.Enqueue(new FrontierEntry(neighborId, score, parent.Hop + 1, provenance, MustKeep: false), -score);
    }

    private sealed record FrontierEntry(
        string NodeId,
        double Score,
        int Hop,
        IReadOnlyList<string> Provenance,
        bool MustKeep);
}

/// <summary>
///     A node reached during graph expansion, with the score and provenance chain that brought it in.
/// </summary>
/// <param name="NodeId">The node id, or empty for a file-only seed.</param>
/// <param name="FilePath">The node's file path, when known.</param>
/// <param name="Kind">The node kind.</param>
/// <param name="Score">The expansion score (0 to 1).</param>
/// <param name="Hop">The number of hops from a seed (0 for seeds).</param>
/// <param name="Provenance">The reasons and edge chain that included the node.</param>
/// <param name="MustKeep">Whether the node is a must-keep seed.</param>
public sealed record ExpandedNode(
    string NodeId,
    string? FilePath,
    string Kind,
    double Score,
    int Hop,
    IReadOnlyList<string> Provenance,
    bool MustKeep);
