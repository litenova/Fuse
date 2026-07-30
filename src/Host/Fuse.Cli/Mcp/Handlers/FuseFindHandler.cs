using System.ComponentModel;
using Fuse.Retrieval;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The <c>fuse_find</c> MCP tool: the find union over the index, wiring graph, and ranked task localization.
/// </summary>
[McpServerToolType]
internal sealed class FuseFindHandler
{
    /// <summary>Finds what a task needs by kind.</summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="changeSource">The git change source for task localization and review-aware lookup.</param>
    /// <param name="query">The name, path fragment, text, wiring identifier, or task to find.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="kind">The kind to restrict the search to.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The matches for the requested kind.</returns>
    [McpServerTool(Name = "fuse_find", ReadOnly = true)]
    [Description("The find union: locate what a task needs by kind. Exact lookup - kind=symbol (by name), path (by fragment), text (full-text), or all. Wiring - kind=service, request, route, or config resolves the query to its implementation/handler/action/options. kind=signatures returns the query symbol's exact signature. kind=neighbors returns the query symbol's callers and implementers. kind=task ranks candidate files for the query with the graded refuse-and-route contract. Use instead of broad grep when the name, wiring, or task is known.")]
    public static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        [Description("The name, path fragment, text, wiring identifier, or task to find.")] string query,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The kind: symbol, path, text, all (exact); service, request, route, config (wiring); signatures; neighbors; task.")] string kind = "all",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        FindToolOperations.ExecuteAsync(indexer, changeSource, query, path, kind, cancellationToken, runtime);
}
