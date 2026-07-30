using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;

namespace Fuse.Cli.Rpc;

/// <summary>
///     An <see cref="IIndexAccessProvider" /> that delegates index writes to a shared daemon over the pipe (R19),
///     so one daemon-owned <c>IndexCoordinator</c> serves every MCP client for a root. After the daemon prepares
///     the store, this process opens it read-only locally for queries. When no compatible daemon answers, it falls
///     back to <see cref="LocalIndexAccessProvider" /> (R14), never a raw store open.
/// </summary>
public sealed class RemoteIndexAccessProvider : IIndexAccessProvider
{
    private readonly Func<string, TimeSpan, CancellationToken, Task<OpenIndexedResultDto?>> _openIndexed;
    private readonly Func<string, IndexDepth, bool, string?, TimeSpan, CancellationToken, Task<IndexJobStartResult?>> _indexStart;
    private readonly Func<string, TimeSpan, CancellationToken, Task<IndexJobSnapshot?>> _indexStatus;
    private readonly TimeSpan _connectTimeout;
    private readonly IIndexAccessProvider? _localFallback;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RemoteIndexAccessProvider" /> class.
    /// </summary>
    /// <param name="openIndexed">
    ///     The open-indexed RPC call (root, timeout, token). Injected for tests; production uses
    ///     <see cref="FuseHostClient.TryOpenIndexedAsync" />.
    /// </param>
    /// <param name="indexStart">
    ///     The index-start RPC call. Injected for tests; production uses <see cref="FuseHostClient.TryIndexStartAsync" />.
    /// </param>
    /// <param name="indexStatus">The index-status RPC call used while an explicit caller waits for completion.</param>
    /// <param name="connectTimeout">How long to wait for a daemon connection.</param>
    /// <param name="localFallback">The host-owned local path to use when the daemon cannot answer.</param>
    public RemoteIndexAccessProvider(
        Func<string, TimeSpan, CancellationToken, Task<OpenIndexedResultDto?>>? openIndexed = null,
        Func<string, IndexDepth, bool, string?, TimeSpan, CancellationToken, Task<IndexJobStartResult?>>? indexStart = null,
        Func<string, TimeSpan, CancellationToken, Task<IndexJobSnapshot?>>? indexStatus = null,
        TimeSpan? connectTimeout = null,
        IIndexAccessProvider? localFallback = null)
    {
        _openIndexed = openIndexed ?? FuseHostClient.TryOpenIndexedAsync;
        _indexStart = indexStart ?? FuseHostClient.TryIndexStartAsync;
        _indexStatus = indexStatus ?? FuseHostClient.TryIndexStatusAsync;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _localFallback = localFallback;
    }

    /// <inheritdoc />
    public async Task<WorkspaceIndexStore> OpenIndexedAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        var remote = await _openIndexed(root, _connectTimeout, cancellationToken);
        if (remote is null)
            return await LocalFallback(indexer).OpenIndexedAsync(indexer, path, cancellationToken);

        switch (remote.Status)
        {
            case "ready":
                return await OpenReadableStoreAsync(root, cancellationToken);
            case "index_rebuilding":
                throw new IndexRebuildingException(remote.Detail ?? "rebuilding from source");
            case "not_indexed":
                throw new InvalidOperationException("daemon reported not_indexed after openIndexed");
            default:
                throw new InvalidOperationException($"unexpected daemon openIndexed status '{remote.Status}'.");
        }
    }

    /// <inheritdoc />
    public async Task<SemanticIndexResult> IndexAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        var started = await _indexStart(root, IndexDepth.Syntax, false, null, _connectTimeout, cancellationToken);
        if (started is null)
            return await LocalFallback(indexer).IndexAsync(indexer, path, cancellationToken);
        if (started.Conflict)
            throw new InvalidOperationException(started.Snapshot.ErrorMessage ?? "index_job_conflict");

        var snapshot = started.Snapshot;
        while (snapshot.State is IndexJobState.Queued or IndexJobState.Running or IndexJobState.Cancelling)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            snapshot = await _indexStatus(root, _connectTimeout, cancellationToken)
                ?? throw new InvalidOperationException("daemon stopped while indexing");
        }

        if (snapshot.State == IndexJobState.Cancelled)
            throw new OperationCanceledException("index job was cancelled", cancellationToken);
        if (snapshot.State == IndexJobState.Failed)
            throw new InvalidOperationException(snapshot.ErrorMessage ?? "index_failed");

        return new SemanticIndexResult(
            "syntax",
            snapshot.Counts.Files,
            snapshot.Counts.Projects,
            snapshot.Counts.Symbols,
            snapshot.Counts.Chunks,
            snapshot.Counts.Routes,
            Diagnostics: []);
    }

    private static async Task<WorkspaceIndexStore> OpenReadableStoreAsync(string root, CancellationToken cancellationToken)
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        var store = new WorkspaceIndexStore(databasePath);
        var status = await store.OpenForReadAsync(cancellationToken);
        if (status is WorkspaceIndexReadOpenStatus.Ready)
            return store;

        await store.DisposeAsync();
        throw new IndexRebuildingException("daemon reported a readable index that could not be opened locally");
    }

    private IIndexAccessProvider LocalFallback(SemanticIndexer indexer)
    {
        if (_localFallback is not null)
            return _localFallback;

        // Direct unit calls that do not construct a host get an isolated manager. Production always supplies the
        // host-owned fallback through dependency injection, so jobs remain deduplicated for the process lifetime.
        return FuseMcpRuntime.CreateIsolated(indexer).IndexAccess;
    }
}
