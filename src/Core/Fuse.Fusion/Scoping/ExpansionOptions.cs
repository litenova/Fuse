using Fuse.Scoping;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Controls how seed paths are expanded across the dependency graph.
/// </summary>
/// <param name="Depth">The maximum number of hops to traverse from any seed. <c>0</c> returns only the seeds.</param>
/// <param name="FollowReferences">
///     When <see langword="true" />, forward edges are followed: a file pulls in the files that define the
///     types it references (its dependencies).
/// </param>
/// <param name="FollowDependents">
///     When <see langword="true" />, reverse edges are followed: a file pulls in the files that reference the
///     types it declares (its dependents).
/// </param>
/// <param name="HopDecay">
///     The factor by which a neighbour's score decays per hop from its parent, in the range (0, 1]. Lower
///     values make distant files rank well below the seeds, so they are dropped first under a token budget.
/// </param>
/// <param name="TokenBudget">
///     An optional soft cap on the estimated tokens admitted during expansion. Seeds are always admitted;
///     once the budget is reached no further neighbours are added. <c>null</c> disables budget gating.
/// </param>
/// <param name="TokenCosts">
///     Per-path estimated token costs used with <paramref name="TokenBudget" />. Paths absent from the map
///     cost nothing. Ignored when <paramref name="TokenBudget" /> is <c>null</c>.
/// </param>
/// <param name="Centrality">
///     Optional query-independent importance prior (normalized PageRank per path, see
///     <see cref="GraphCentrality" /> in <c>Fuse.Scoping</c>). Blended additively into the rank score so that at equal relevance the
///     more depended-upon file ranks earlier. <c>null</c> disables the prior.
/// </param>
/// <param name="CentralityWeight">
///     The weight applied to <paramref name="Centrality" /> when blending it into the rank score. A weight of
///     <c>0</c> (the default) reproduces the prior ordering exactly. The blend is additive and never affects
///     the score propagated to neighbours, so it cannot compound across hops.
/// </param>
/// <param name="ProximityEdges">
///     Optional low-weight structural edges followed in addition to type-reference edges: a path mapped to the
///     paths it is structurally near (a test or implementation counterpart, a same-stem sibling). <c>null</c>
///     disables them. Ignored when <paramref name="ProximityWeight" /> is <c>0</c>.
/// </param>
/// <param name="ProximityWeight">
///     The factor applied to a proximity neighbour's propagated score, below a real reference (which uses
///     <paramref name="HopDecay" />). <c>0</c> (the default) disables proximity expansion entirely.
/// </param>
public sealed record ExpansionOptions(
    int Depth,
    bool FollowReferences = true,
    bool FollowDependents = false,
    double HopDecay = 0.5,
    int? TokenBudget = null,
    IReadOnlyDictionary<string, int>? TokenCosts = null,
    IReadOnlyDictionary<string, double>? Centrality = null,
    double CentralityWeight = 0.0,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ProximityEdges = null,
    double ProximityWeight = 0.0);
