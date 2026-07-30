using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Opens the workspace index for the MCP tool operations and turns a not-ready index into the structured
///     availability header a read tool returns instead of blocking or failing opaquely.
/// </summary>
internal static class IndexedStoreAccess
{
    /// <summary>Resolves the host-owned runtime, creating an isolated one when a caller supplies none.</summary>
    /// <param name="runtime">The host-owned runtime, when present.</param>
    /// <param name="indexer">The semantic indexer backing an isolated runtime.</param>
    /// <returns>The runtime to use for this operation.</returns>
    internal static FuseMcpRuntime ResolveRuntime(FuseMcpRuntime? runtime, SemanticIndexer indexer) =>
        runtime ?? FuseMcpRuntime.CreateIsolated(indexer);

    /// <summary>
    ///     Opens the store, building the index on first use so read tools work without an explicit index call.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="path">The requested workspace path.</param>
    /// <param name="cancellationToken">A token to cancel the open.</param>
    /// <returns>The opened store.</returns>
    /// <exception cref="IndexBlockedReadException">The index is cold or contended and cannot answer yet.</exception>
    internal static Task<WorkspaceIndexStore> OpenIndexedAsync(
        SemanticIndexer indexer,
        string path,
        CancellationToken cancellationToken) =>
        OpenIndexedAsync(ResolveRuntime(runtime: null, indexer), indexer, path, cancellationToken);

    /// <summary>
    ///     Opens the store through the runtime's index access provider, building the index on first use. The shared
    ///     syntax job stays independent of this caller's wait; a requested semantic pass continues after syntax is
    ///     readable and does not block this open.
    /// </summary>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="path">The requested workspace path.</param>
    /// <param name="cancellationToken">A token to cancel the open.</param>
    /// <returns>The opened store.</returns>
    /// <exception cref="IndexBlockedReadException">The index is cold or contended and cannot answer yet.</exception>
    internal static async Task<WorkspaceIndexStore> OpenIndexedAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        string path,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        try
        {
            var store = await runtime.IndexAccess.OpenIndexedAsync(indexer, root, cancellationToken);
            var mode = await store.GetMetaAsync(WorkspaceIndexStore.IndexModeMetaKey, cancellationToken) ?? "unknown";
            FuseMetrics.RecordIndexMode(root, mode);
            return store;
        }
        catch (ColdStartInProgressException)
        {
            // R27: the cold syntax build did not finish within the deadline; return a bounded building_syntax
            // header as the tool body while the build continues, instead of blocking the read for the whole build.
            throw new IndexBlockedReadException(
                await IndexAvailabilityReporter.BuildingSyntaxHeaderAsync(root, runtime.IndexJobs, cancellationToken));
        }
        catch (Exception ex) when (IsContention(ex))
        {
            throw new IndexBlockedReadException(
                await IndexAvailabilityReporter.BlockedReadHeaderAsync(root, cancellationToken));
        }
    }

    /// <summary>
    ///     Runs a read MCP tool body and returns the availability header as the tool result when the index cannot
    ///     be opened yet (R20), instead of a generic <c>index_busy:</c> prefix or an unbounded hang.
    /// </summary>
    /// <param name="action">The read tool implementation.</param>
    /// <returns>The tool result or a structured availability header on blocked read.</returns>
    internal static async Task<string> ExecuteReadMcpAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (IndexBlockedReadException ex)
        {
            // R30/R37: a not-ready read abstains and defers to native search (returns the fast structured signal,
            // not a diluted result). Count the deferral so it is never silent.
            FuseMetrics.RecordDegraded(DegradedStateKind.Deferred);
            return ex.AvailabilityHeader;
        }
        catch (Exception ex)
        {
            return FuseOperationalErrors.FromException(ex);
        }
    }

    /// <summary>
    ///     Opens a warm read-only store for optional enrichment or covering selection (R18). Never triggers a
    ///     syntax-first build or a reconcile.
    /// </summary>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">A token to cancel the open.</param>
    /// <returns>The store, or null when the index is missing or contended.</returns>
    internal static async Task<WorkspaceIndexStore?> TryOpenForEnrichmentAsync(
        FuseMcpRuntime runtime,
        string root,
        CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(FuseStorePaths.ResolveDatabasePath(root))
                ? await runtime.IndexCoordinator.OpenForReadOnlyAsync(root, cancellationToken)
                : null;
        }
        catch (Exception ex) when (IsContention(ex))
        {
            return null;
        }
    }

    /// <summary>
    ///     Opens the store that persists a <c>fuse_check</c> delta-session baseline, initializing it when the
    ///     repository has no index yet.
    /// </summary>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">A token to cancel the open.</param>
    /// <returns>The store, or null when the index is contended.</returns>
    /// <remarks>
    ///     The session baseline is a small daemon-owned write, so the store is initialized when it does not exist
    ///     yet: delta mode with a resident workspace must not require a pre-built persistent index (this mirrors
    ///     the host RPC baseline path). Genuine contention still abstains.
    /// </remarks>
    internal static async Task<WorkspaceIndexStore?> TryOpenForSessionBaselineAsync(
        FuseMcpRuntime runtime,
        string root,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(FuseStorePaths.ResolveDatabasePath(root)))
            {
                await runtime.IndexCoordinator.OpenForWriteAsync(
                    root, static (_, _) => Task.FromResult(0), cancellationToken);
            }

            return await runtime.IndexCoordinator.OpenForReadOnlyAsync(root, cancellationToken);
        }
        catch (Exception ex) when (IsContention(ex))
        {
            return null;
        }
    }

    /// <summary>Whether an exception means the index is briefly contended rather than broken.</summary>
    /// <param name="exception">The exception raised while opening the index.</param>
    /// <returns>True when the caller should degrade to an availability header.</returns>
    internal static bool IsContention(Exception exception) =>
        exception is IndexBusyException
            or SqliteException { SqliteErrorCode: 5 or 6 }
            or IOException { HResult: unchecked((int)0x80070020) };
}
