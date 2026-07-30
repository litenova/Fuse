using System.Text;
using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     Renders a textual workspace map from the index: indexed symbols, routes, and summary counts.
/// </summary>
/// <remarks>
///     A cheap, read-only view over the persisted index, used by the <c>map</c> command. It does not
///     index; callers must index first. Richer maps (DI, project graph) are added as those analyzers land.
/// </remarks>
public sealed class WorkspaceMapRenderer
{
    private readonly IWorkspaceIndexLifecycleStore _lifecycleStore;
    private readonly IWorkspaceIndexQueryStore _queryStore;

    /// <summary>
    ///     Initializes a new instance of the <see cref="WorkspaceMapRenderer" /> class.
    /// </summary>
    /// <param name="lifecycleStore">The index store lifecycle reader.</param>
    /// <param name="queryStore">The index store query reader.</param>
    public WorkspaceMapRenderer(IWorkspaceIndexLifecycleStore lifecycleStore, IWorkspaceIndexQueryStore queryStore)
    {
        _lifecycleStore = lifecycleStore;
        _queryStore = queryStore;
    }

    /// <summary>
    ///     Renders the workspace map at the requested detail level.
    /// </summary>
    /// <param name="detail">The detail to include.</param>
    /// <param name="maxRows">The maximum rows per section.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>A plain-text map.</returns>
    public async Task<string> RenderAsync(MapDetail detail, int maxRows, CancellationToken cancellationToken)
    {
        var state = await _lifecycleStore.GetStateAsync(cancellationToken);
        var builder = new StringBuilder();
        builder.AppendLine("workspace map");
        builder.AppendLine($"status: {state.Status}  files: {state.FileCount}  symbols: {state.SymbolCount}");
        builder.AppendLine();

        if (detail is MapDetail.Symbols or MapDetail.All)
            await RenderSymbolsAsync(builder, maxRows, cancellationToken);

        if (detail is MapDetail.Routes or MapDetail.All)
            await RenderRoutesAsync(builder, maxRows, cancellationToken);

        return builder.ToString();
    }

    private async Task RenderSymbolsAsync(StringBuilder builder, int maxRows, CancellationToken cancellationToken)
    {
        var symbols = await _queryStore.ListSymbolsAsync(maxRows, cancellationToken);
        builder.AppendLine($"symbols ({symbols.Count})");
        foreach (var symbol in symbols)
        {
            var visibility = symbol.IsPublicApi ? "+" : " ";
            builder.AppendLine($"  {visibility} {symbol.Kind,-11} {symbol.FullyQualifiedName}  ({symbol.FilePath}:{symbol.StartLine})");
        }

        builder.AppendLine();
    }

    private async Task RenderRoutesAsync(StringBuilder builder, int maxRows, CancellationToken cancellationToken)
    {
        var routes = await _queryStore.ListRoutesAsync(maxRows, cancellationToken);
        builder.AppendLine($"routes ({routes.Count})");
        foreach (var route in routes)
            builder.AppendLine($"  {route.HttpMethod,-6} {route.RoutePattern}  ({route.FilePath}:{route.StartLine})");

        builder.AppendLine();
    }
}

/// <summary>
///     The detail level for a workspace map.
/// </summary>
public enum MapDetail
{
    /// <summary>Indexed symbols.</summary>
    Symbols,

    /// <summary>Indexed routes.</summary>
    Routes,

    /// <summary>All available sections.</summary>
    All,
}
