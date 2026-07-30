using System.ComponentModel;
using Fuse.Context;
using Fuse.Reduction;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The <c>fuse_context</c> MCP tool: scoped, reduced source with provenance for a set of seeds.
/// </summary>
[McpServerToolType]
internal sealed class FuseContextHandler
{
    /// <summary>Plans and emits context for the requested seeds.</summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render bodies.</param>
    /// <param name="sessionStore">The session store used to elide unchanged files.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="seeds">Symbol seeds.</param>
    /// <param name="files">File path seeds.</param>
    /// <param name="services">Service seeds to resolve and expand.</param>
    /// <param name="requests">Request or command seeds to resolve and expand.</param>
    /// <param name="configs">Config section seeds to resolve and expand.</param>
    /// <param name="routes">Route seeds.</param>
    /// <param name="depth">The graph expansion depth.</param>
    /// <param name="maxTokens">The token budget, or zero for none.</param>
    /// <param name="format">The output format: xml, markdown, or json.</param>
    /// <param name="sessionId">Session id; files already sent unchanged in the session are elided.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The emitted context payload.</returns>
    [McpServerTool(Name = "fuse_context", ReadOnly = true)]
    [Description("Plan and emit context (source bodies, mixed render tiers, manifest, provenance) for a set of seeds. Feed it the file paths from fuse_find (kind=task) or the names it resolves from wiring kinds. Pass a sessionId to elide files already sent in the session.")]
    public static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        ContextSessionStore sessionStore,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Symbol seeds.")] string[]? seeds = null,
        [Description("File path seeds (for example the paths returned by fuse_find kind=task).")] string[]? files = null,
        [Description("Service seeds to resolve and expand.")] string[]? services = null,
        [Description("Request/command seeds to resolve and expand.")] string[]? requests = null,
        [Description("Config section seeds to resolve and expand.")] string[]? configs = null,
        [Description("Route seeds, for example \"POST /api/orders/{id}\".")] string[]? routes = null,
        [Description("Graph expansion depth.")] int depth = 2,
        [Description("Token budget; must-keep seeds are always included.")] int maxTokens = 0,
        [Description("Output format: xml (default), markdown, or json.")] string format = "xml",
        [Description("Session id; files already sent unchanged in this session are elided.")] string? sessionId = null,
        CancellationToken cancellationToken = default) =>
        ContextToolOperations.ExecuteAsync(
            indexer, reductionPipeline, sessionStore, path, seeds, files, services, requests, configs, routes,
            depth, maxTokens, format, sessionId, cancellationToken);
}
