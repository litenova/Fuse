using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Reduction;
using Fuse.Retrieval;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Implements <c>fuse_context</c>: plan and emit scoped, reduced source with provenance for a set of seeds.
/// </summary>
internal static class ContextToolOperations
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
    internal static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        ContextSessionStore sessionStore,
        string path = ".",
        string[]? seeds = null,
        string[]? files = null,
        string[]? services = null,
        string[]? requests = null,
        string[]? configs = null,
        string[]? routes = null,
        int depth = 2,
        int maxTokens = 0,
        string format = "xml",
        string? sessionId = null,
        CancellationToken cancellationToken = default) =>
        FuseOperationalErrors.ExecuteMcpAsync(() => ContextCoreAsync(
            indexer, reductionPipeline, sessionStore, path, seeds, files, services, requests, configs, routes,
            depth, maxTokens, format, sessionId, cancellationToken));

    private static async Task<string> ContextCoreAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        ContextSessionStore sessionStore,
        string path,
        string[]? seeds,
        string[]? files,
        string[]? services,
        string[]? requests,
        string[]? configs,
        string[]? routes,
        int depth,
        int maxTokens,
        string format,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var seedList = BuildSeeds(seeds, files, services, requests, configs, routes);
        if (seedList.Count == 0)
            return "Error: provide at least one seed (symbol/file/service/request/config/route).";

        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        if (files is { Length: > 0 })
        {
            var fileError = WorkspacePathResolver.ValidateWorkspacePaths(root, files, "read");
            if (fileError is not null)
                return fileError;
        }

        await using var store = await IndexedStoreAccess.OpenIndexedAsync(indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store);
        var plan = await engine.PlanContextAsync(
            new ContextRequest(root, seedList, depth, maxTokens > 0 ? maxTokens : null), cancellationToken);

        var renderer = new SemanticContextRenderer(reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, cancellationToken);
        var unchanged = string.IsNullOrWhiteSpace(sessionId) ? null : sessionStore.Reconcile(sessionId, rendered.Files);
        return SemanticContextEmitter.Emit(plan, rendered, ContextFormat.Parse(format), root, unchangedPaths: unchanged);
    }

    private static List<ContextSeed> BuildSeeds(
        string[]? seeds,
        string[]? files,
        string[]? services,
        string[]? requests,
        string[]? configs,
        string[]? routes) =>
        (seeds ?? []).Select(seed => new ContextSeed(ContextSeedKind.Symbol, seed))
            .Concat((files ?? []).Select(file => new ContextSeed(ContextSeedKind.File, file)))
            .Concat((services ?? []).Select(service => new ContextSeed(ContextSeedKind.Service, service)))
            .Concat((requests ?? []).Select(request => new ContextSeed(ContextSeedKind.Request, request)))
            .Concat((configs ?? []).Select(config => new ContextSeed(ContextSeedKind.Config, config)))
            .Concat((routes ?? []).Select(route => new ContextSeed(ContextSeedKind.Route, route)))
            .ToList();
}
