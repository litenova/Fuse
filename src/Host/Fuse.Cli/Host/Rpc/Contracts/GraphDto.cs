namespace Fuse.Cli.Rpc;

/// <summary>
///     The result of the <c>fuse/graph</c> method: the dependency graph at the requested level of detail.
/// </summary>
/// <param name="Nodes">The graph nodes.</param>
/// <param name="Edges">The graph edges.</param>
/// <param name="Detail">The level of detail the nodes are at (<c>Files</c> or <c>Directories</c>).</param>
public sealed record GraphDto(
    IReadOnlyList<GraphNodeDto> Nodes,
    IReadOnlyList<GraphEdgeDto> Edges,
    string Detail);

/// <summary>
///     One node in the dependency graph projection: a file with the data a client needs to size, color, and label
///     it without recomputing anything.
/// </summary>
/// <param name="Path">The normalized repository-relative file path; the node identity.</param>
/// <param name="DeclaredTypes">The type names the file declares, for the node label and hover.</param>
/// <param name="Centrality">The normalized PageRank centrality in <c>[0, 1]</c>, driving node size.</param>
/// <param name="TokenCost">The estimated token cost of including the file, driving node color.</param>
/// <param name="Role">The file's role when a scope is active (seed, dependency, changed), or <c>null</c>.</param>
public sealed record GraphNodeDto(
    string Path,
    IReadOnlyList<string> DeclaredTypes,
    double Centrality,
    int TokenCost,
    string? Role);

/// <summary>
///     One directed edge in the dependency graph projection: a reference from one file to another.
/// </summary>
/// <param name="From">The referencing file path.</param>
/// <param name="To">The referenced file path.</param>
/// <param name="Weight">The edge weight (reference strength), for styling.</param>
/// <param name="Kind">The reference kind (for example <c>reference</c>), for edge styling.</param>
public sealed record GraphEdgeDto(string From, string To, double Weight, string Kind);
