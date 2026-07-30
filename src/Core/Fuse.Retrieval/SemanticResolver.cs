using Fuse.Indexing;

namespace Fuse.Retrieval;

/// <summary>
///     Deterministically resolves .NET wiring from the semantic index: the implementation injected for a
///     service, the handler that processes a request, and the action that handles a route.
/// </summary>
/// <remarks>
///     Resolution is a graph lookup: find the source node by name (or by constructed id for a route), then
///     follow the typed edge (<c>di_resolves_to</c>, <c>mediatr_handles</c>, <c>route_handles</c>) to its
///     targets. No source bodies are returned; the result carries paths, signatures, and evidence.
/// </remarks>
public sealed class SemanticResolver
{
    private readonly IWorkspaceIndexGraphStore _store;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticResolver" /> class.
    /// </summary>
    /// <param name="store">The index store to query.</param>
    public SemanticResolver(IWorkspaceIndexGraphStore store) => _store = store;

    /// <summary>
    ///     Resolves a service to its registered implementation(s).
    /// </summary>
    /// <param name="service">The service type name (simple or qualified).</param>
    /// <param name="cancellationToken">A token to cancel the resolution.</param>
    /// <returns>The resolution result; matches relate via <c>di_resolves_to</c>.</returns>
    public Task<ResolveResult> ResolveServiceAsync(string service, CancellationToken cancellationToken) =>
        ResolveByNameAsync(ResolveTarget.Service, service, "di_resolves_to", cancellationToken);

    /// <summary>
    ///     Resolves a request or command to the handler(s) that process it.
    /// </summary>
    /// <param name="request">The request type name (simple or qualified).</param>
    /// <param name="cancellationToken">A token to cancel the resolution.</param>
    /// <returns>The resolution result; matches relate via <c>mediatr_handles</c>.</returns>
    public Task<ResolveResult> ResolveRequestAsync(string request, CancellationToken cancellationToken) =>
        ResolveByNameAsync(ResolveTarget.Request, request, "mediatr_handles", cancellationToken);

    /// <summary>
    ///     Resolves a route to the action method(s) that handle it.
    /// </summary>
    /// <param name="route">The route as "METHOD /pattern" (for example "POST /api/orders/{id}").</param>
    /// <param name="cancellationToken">A token to cancel the resolution.</param>
    /// <returns>The resolution result; matches relate via <c>route_handles</c>.</returns>
    public async Task<ResolveResult> ResolveRouteAsync(string route, CancellationToken cancellationToken)
    {
        var (method, pattern) = ParseRoute(route);
        var routeNodeId = $"route:{method}:{pattern}";
        var node = await _store.GetNodeAsync(routeNodeId, cancellationToken);
        if (node is null)
            return new ResolveResult(route, ResolveTarget.Route, []);

        var matches = await FollowEdgesAsync(routeNodeId, "route_handles", cancellationToken);
        return new ResolveResult(route, ResolveTarget.Route, matches);
    }

    /// <summary>
    ///     Resolves a configuration section to the options type(s) bound to it.
    /// </summary>
    /// <param name="section">The configuration section name.</param>
    /// <param name="cancellationToken">A token to cancel the resolution.</param>
    /// <returns>The resolution result; matches relate via <c>options_binds</c>.</returns>
    public async Task<ResolveResult> ResolveConfigAsync(string section, CancellationToken cancellationToken)
    {
        var configNodeId = $"config:{section.Trim()}";
        var node = await _store.GetNodeAsync(configNodeId, cancellationToken);
        if (node is null)
            return new ResolveResult(section, ResolveTarget.Config, []);

        var matches = await FollowEdgesAsync(configNodeId, "options_binds", cancellationToken);
        return new ResolveResult(section, ResolveTarget.Config, matches);
    }

    /// <summary>
    ///     Resolves a symbol name to the matching declared node(s).
    /// </summary>
    /// <param name="symbol">The symbol name (simple or qualified).</param>
    /// <param name="cancellationToken">A token to cancel the resolution.</param>
    /// <returns>The resolution result; matches are the declarations themselves.</returns>
    public async Task<ResolveResult> ResolveSymbolAsync(string symbol, CancellationToken cancellationToken)
    {
        var nodes = await _store.FindNodesByDisplayNameAsync(SimpleName(symbol), cancellationToken);
        var matches = nodes
            .Select(n => new ResolvedNode(n.NodeId, n.DisplayName, n.Kind, n.FilePath, n.StartLine, n.Signature, "declares", null))
            .ToList();
        return new ResolveResult(symbol, ResolveTarget.Symbol, matches);
    }

    private async Task<ResolveResult> ResolveByNameAsync(
        ResolveTarget target,
        string name,
        string edgeType,
        CancellationToken cancellationToken)
    {
        var simpleName = SimpleName(name);
        var sources = await _store.FindNodesByDisplayNameAsync(simpleName, cancellationToken);
        var matches = new List<ResolvedNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            foreach (var match in await FollowEdgesAsync(source.NodeId, edgeType, cancellationToken))
            {
                if (seen.Add(match.NodeId))
                    matches.Add(match);
            }
        }

        return new ResolveResult(name, target, matches);
    }

    private async Task<IReadOnlyList<ResolvedNode>> FollowEdgesAsync(string nodeId, string edgeType, CancellationToken cancellationToken)
    {
        var matches = new List<ResolvedNode>();
        foreach (var edge in await _store.GetOutgoingEdgesAsync(nodeId, cancellationToken))
        {
            if (edge.EdgeType != edgeType)
                continue;

            var target = await _store.GetNodeAsync(edge.ToNodeId, cancellationToken);
            if (target is null)
                continue;

            matches.Add(new ResolvedNode(
                NodeId: target.NodeId,
                DisplayName: target.DisplayName,
                Kind: target.Kind,
                FilePath: target.FilePath,
                StartLine: target.StartLine,
                Signature: target.Signature,
                Relation: edge.EdgeType,
                Evidence: edge.Evidence));
        }

        return matches;
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

        var method = trimmed[..space].Trim().ToUpperInvariant();
        return (method, Normalize(trimmed[(space + 1)..].Trim()));
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

/// <summary>The kind of thing being resolved.</summary>
public enum ResolveTarget
{
    /// <summary>A symbol.</summary>
    Symbol,

    /// <summary>An HTTP route.</summary>
    Route,

    /// <summary>A DI service.</summary>
    Service,

    /// <summary>A request or command.</summary>
    Request,

    /// <summary>A configuration section.</summary>
    Config,
}

/// <summary>
///     The result of a resolution: the query, what was resolved, and the matched nodes.
/// </summary>
/// <param name="Query">The original query.</param>
/// <param name="Target">What was resolved.</param>
/// <param name="Matches">The resolved nodes (empty when nothing matched).</param>
public sealed record ResolveResult(string Query, ResolveTarget Target, IReadOnlyList<ResolvedNode> Matches);

/// <summary>
///     A resolved node: where it lives and how it relates to the query.
/// </summary>
/// <param name="NodeId">The node id.</param>
/// <param name="DisplayName">The node display name.</param>
/// <param name="Kind">The node kind.</param>
/// <param name="FilePath">The declaring file's normalized path, when known.</param>
/// <param name="StartLine">The 1-based start line, when known.</param>
/// <param name="Signature">The signature, when known.</param>
/// <param name="Relation">The edge type connecting the query to this node.</param>
/// <param name="Evidence">The edge evidence, when available.</param>
public sealed record ResolvedNode(
    string NodeId,
    string DisplayName,
    string Kind,
    string? FilePath,
    int? StartLine,
    string? Signature,
    string Relation,
    string? Evidence);
