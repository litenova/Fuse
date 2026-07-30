namespace Fuse.Retrieval;

/// <summary>
///     Supplies the traversal weight for each semantic edge type and the per-hop decay used during graph
///     expansion.
/// </summary>
/// <remarks>
///     Weights follow the retrieval design: route and MediatR handling and DI resolution are near-certain
///     (1.00 to 0.95), structural relationships are strong (0.90 to 0.75), and weak proximity signals are low
///     (project references 0.30 down to bare references 0.15). Unknown edge types fall back to a low default so a new
///     edge cannot dominate before it is tuned.
/// </remarks>
public sealed class EdgeWeightProvider
{
    /// <summary>The score multiplier applied for each additional hop from a seed.</summary>
    public double HopDecay => 0.65;

    private static readonly Dictionary<string, double> Weights = new(StringComparer.Ordinal)
    {
        ["route_handles"] = 1.00,
        ["mediatr_handles"] = 0.95,
        ["di_resolves_to"] = 0.95,
        ["implements"] = 0.90,
        ["di_depends_on_impl"] = 0.85,
        ["options_binds"] = 0.85,
        ["config_impacts"] = 0.80,
        ["inherits"] = 0.75,
        ["di_injects"] = 0.75,
        ["options_consumes"] = 0.75,
        ["sends_request"] = 0.70,
        // "tests" edges (a test type to the symbols it covers, DI-resolved) are produced by TestEdgeExtractor
        // (R5 part 2); the weight drives the test-impact traversal M1 uses to select covering tests.
        ["tests"] = 0.65,
        ["project_references"] = 0.30,
        ["path_proximity"] = 0.20,
        // "references" is produced by ReferenceEdgeAnalyzer (R5): a type-level use edge, the weakest structural
        // signal. The former "calls" weight was removed (finding 7): no analyzer emitted it, and R5 chose
        // "references" as the edge name for use edges rather than a separate call graph.
        ["references"] = 0.15,
    };

    /// <summary>
    ///     Returns the traversal weight for an edge type.
    /// </summary>
    /// <param name="edgeType">The edge type.</param>
    /// <returns>The weight in the range 0 to 1; a low default for unknown types.</returns>
    public double Weight(string edgeType) => Weights.GetValueOrDefault(edgeType, 0.10);
}
