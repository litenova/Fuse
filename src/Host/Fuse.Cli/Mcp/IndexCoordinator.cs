using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fuse.Cli;
using Fuse.Collection.FileSystem;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Serializes index writes per workspace root (one in-process writer queue) and arbitrates cross-process
///     contention with a named mutex (R14). Warm foreground reads use <see cref="IWorkspaceIndexStore.OpenForReadAsync" />
///     without acquiring the writer lock; write initialization, indexing, and chunked upgrades run under the lock.
/// </summary>
public sealed class IndexCoordinator
{
    private static readonly ConcurrentDictionary<string, RootGate> Gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shared coordinator used by MCP tools and CLI commands in this process.</summary>
    public static IndexCoordinator Default { get; } = new();

    /// <summary>
    ///     Incremented when this process acquires the cross-process writer lock (R19 single-writer audit tests).
    /// </summary>
    internal static int ProcessWriteLockAcquireCount { get; private set; }

    /// <summary>
    ///     The busy timeout for a read tool's store access and its per-read reconcile (R18/R20): short so a
    ///     contended store surfaces the <c>index_busy</c> availability header within one second rather than
    ///     hanging on the full write-path timeout. A build started by a foreground read uses the same bound.
    /// </summary>
    internal const int ReadBusyTimeoutMilliseconds = 250;

    /// <summary>
    ///     Opens the store for a read tool: warm read-only open when possible, otherwise a single write
    ///     initialization under the coordinator lock.
    /// </summary>
    /// <param name="indexer">The semantic indexer (cold start and reconcile).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="backgroundSemanticUpgradeEnabled">Whether syntax-first cold start schedules a background upgrade.</param>
    /// <param name="upgradeSupervisor">The supervisor that owns background upgrade jobs.</param>
    /// <param name="scheduleSemanticUpgrade">Schedules a background semantic upgrade for a root.</param>
    /// <param name="residentWorkspaceActive">Whether a resident workspace is the sole writer for this root.</param>
    /// <param name="cancellationToken">A token to cancel the open.</param>
    /// <returns>An opened store ready for reads, or throws <see cref="IndexBusyException" /> on cross-process contention.</returns>
    public async Task<WorkspaceIndexStore> OpenIndexedAsync(
        SemanticIndexer indexer,
        string path,
        bool backgroundSemanticUpgradeEnabled,
        SemanticUpgradeSupervisor upgradeSupervisor,
        Action<SemanticIndexer, string> scheduleSemanticUpgrade,
        Func<string, bool> residentWorkspaceActive,
        CancellationToken cancellationToken)
    {
        var root = CanonicalRoot(path);
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        var store = new WorkspaceIndexStore(databasePath, busyTimeoutMilliseconds: ReadBusyTimeoutMilliseconds);
        var readStatus = await store.OpenForReadAsync(cancellationToken);

        if (readStatus is not WorkspaceIndexReadOpenStatus.Ready)
        {
            await ExecuteWriteAsync(
                root,
                async (writeStore, ct) =>
                {
                    await writeStore.InitializeAsync(ct);
                    return 0;
                },
                cancellationToken,
                ReadBusyTimeoutMilliseconds);
            store = new WorkspaceIndexStore(databasePath, busyTimeoutMilliseconds: ReadBusyTimeoutMilliseconds);
            readStatus = await store.OpenForReadAsync(cancellationToken);
            if (readStatus is not WorkspaceIndexReadOpenStatus.Ready)
                throw new InvalidOperationException("write initialization did not produce a readable store.");
        }

        var manifest = await WorkspaceIndexManifest.ValidateAsync(root, store, cancellationToken);
        if (!manifest.Ready)
        {
            await BuildIndexAsync(
                indexer,
                root,
                backgroundSemanticUpgradeEnabled,
                scheduleSemanticUpgrade,
                cancellationToken);
        }
        else if (!residentWorkspaceActive(root))
        {
            // The per-read reconcile is a small write; give it the short read timeout so a contended store surfaces
            // index_busy quickly instead of blocking a read tool on the full write-path timeout (R18/R20).
            var freshness = await ExecuteWriteAsync(
                root,
                async (writeStore, ct) => await indexer.ReconcileDirtyFilesAsync(root, writeStore, ct),
                cancellationToken,
                ReadBusyTimeoutMilliseconds);
            if (freshness.Stamped)
            {
                FuseMetrics.RecordReconcileStamped(root);
                await BuildIndexAsync(
                    indexer,
                    root,
                    backgroundSemanticUpgradeEnabled,
                    scheduleSemanticUpgrade,
                    cancellationToken);
            }
        }

        var readStore = new WorkspaceIndexStore(databasePath, busyTimeoutMilliseconds: ReadBusyTimeoutMilliseconds);
        var finalStatus = await readStore.OpenForReadAsync(cancellationToken);
        if (finalStatus is not WorkspaceIndexReadOpenStatus.Ready)
            throw new InvalidOperationException("index open did not produce a readable store.");
        return readStore;
    }

    /// <summary>
    ///     Opens the store for a read-only CLI or diagnostics path. Uses warm read open when possible.
    /// </summary>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="cancellationToken">A token to cancel the open.</param>
    /// <returns>A readable store, or throws when the database is missing or contention blocks initialization.</returns>
    public async Task<WorkspaceIndexStore> OpenForReadOnlyAsync(string root, CancellationToken cancellationToken)
    {
        var canonicalRoot = CanonicalRoot(root);
        var databasePath = FuseStorePaths.ResolveDatabasePath(canonicalRoot);
        if (!File.Exists(databasePath))
            throw new FileNotFoundException(FuseOperationalErrors.FormatIndexNotBuilt(databasePath));

        var store = new WorkspaceIndexStore(databasePath, busyTimeoutMilliseconds: ReadBusyTimeoutMilliseconds);
        var status = await store.OpenForReadAsync(cancellationToken);
        if (status is WorkspaceIndexReadOpenStatus.Ready)
            return store;

        await ExecuteWriteAsync(
            canonicalRoot,
            async (writeStore, ct) =>
            {
                await InitializeOrThrowAsync(writeStore, ct);
                return 0;
            },
            cancellationToken,
            ReadBusyTimeoutMilliseconds);

        store = new WorkspaceIndexStore(databasePath, busyTimeoutMilliseconds: ReadBusyTimeoutMilliseconds);
        status = await store.OpenForReadAsync(cancellationToken);
        if (status is not WorkspaceIndexReadOpenStatus.Ready)
            throw new InvalidOperationException("read open failed after initialization.");
        return store;
    }

    /// <summary>
    ///     Opens the store for an explicit write path (<c>fuse index</c>, capture rehydrate). The caller must run
    ///     all write work inside <paramref name="work" /> so the coordinator lock covers the full mutation.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="work">Write work against an initialized store.</param>
    /// <param name="cancellationToken">A token to cancel the work.</param>
    /// <returns>The work result.</returns>
    public Task<T> OpenForWriteAsync<T>(
        string root,
        Func<WorkspaceIndexStore, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            root,
            async (store, ct) =>
            {
                var status = await store.OpenForReadAsync(ct);
                if (status is not WorkspaceIndexReadOpenStatus.Ready)
                    await store.InitializeAsync(ct);
                return await work(store, ct);
            },
            cancellationToken);

    /// <summary>
    ///     Runs a background semantic upgrade under the coordinator write lock with chunked SQLite commits.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="cancellationToken">A token to cancel the upgrade.</param>
    /// <returns>A task that completes when the upgrade finishes.</returns>
    public Task RunBackgroundUpgradeAsync(
        SemanticIndexer indexer,
        string root,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            root,
            async (store, ct) =>
            {
                var status = await store.OpenForReadAsync(ct);
                if (status is not WorkspaceIndexReadOpenStatus.Ready)
                    await InitializeOrThrowAsync(store, ct);
                await indexer.UpgradeToSemanticAsync(root, store, ct);
                return 0;
            },
            cancellationToken);

    /// <summary>
    ///     Runs write work under the per-root in-process queue and cross-process writer mutex.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="work">The work to run with a store handle.</param>
    /// <param name="cancellationToken">A token to cancel the work.</param>
    /// <param name="busyTimeoutMilliseconds">The SQLite busy timeout for the store used by the write.</param>
    /// <returns>The work result.</returns>
    public async Task<T> ExecuteWriteAsync<T>(
        string root,
        Func<WorkspaceIndexStore, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken,
        int busyTimeoutMilliseconds = WorkspaceIndexConnectionFactory.DefaultBusyTimeoutMilliseconds)
    {
        var canonicalRoot = CanonicalRoot(root);
        var normalizedRoot = WorkspaceIdentityResolver.NormalizeKey(canonicalRoot);
        var gate = Gates.GetOrAdd(normalizedRoot, _ => new RootGate());
        await gate.WriteSemaphore.WaitAsync(cancellationToken);
        try
        {
            using var writerLock = IndexWriterLock.TryAcquire(normalizedRoot);
            if (!writerLock.IsOwner)
                throw new IndexBusyException();

            ProcessWriteLockAcquireCount++;
            var databasePath = FuseStorePaths.ResolveDatabasePath(canonicalRoot);
            await using var store = new WorkspaceIndexStore(databasePath, busyTimeoutMilliseconds: busyTimeoutMilliseconds);
            return await work(store, cancellationToken);
        }
        finally
        {
            gate.WriteSemaphore.Release();
        }
    }

    private static async Task InitializeOrThrowAsync(WorkspaceIndexStore store, CancellationToken cancellationToken)
    {
        var outcome = await store.InitializeAsync(cancellationToken);
        if (outcome.RebuiltEmptyStore)
            throw new IndexRebuildingException(outcome.Detail ?? "rebuilding from source");
    }

    private async Task BuildIndexAsync(
        SemanticIndexer indexer,
        string root,
        bool backgroundSemanticUpgradeEnabled,
        Action<SemanticIndexer, string> scheduleSemanticUpgrade,
        CancellationToken cancellationToken)
    {
        await ExecuteWriteAsync(
            root,
            (writeStore, ct) => indexer.IndexSyntaxFirstAsync(root, writeStore, ct),
            cancellationToken,
            ReadBusyTimeoutMilliseconds);
    }

    private static string CanonicalRoot(string root) =>
        WorkspaceIdentityResolver.TryResolveRepositoryRoot(root, out var repositoryRoot)
            ? repositoryRoot
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private sealed class RootGate
    {
        public SemaphoreSlim WriteSemaphore { get; } = new(1, 1);
    }

    /// <summary>
    ///     Cross-process single-writer lock for index mutations, keyed by workspace root (same hash scheme as
    ///     <c>fuse host</c> daemon arbitration).
    /// </summary>
    private sealed class IndexWriterLock : IDisposable
    {
        private readonly Mutex? _mutex;
        private bool _released;

        private IndexWriterLock(Mutex? mutex, bool isOwner)
        {
            _mutex = mutex;
            IsOwner = isOwner;
        }

        public bool IsOwner { get; }

        public static IndexWriterLock TryAcquire(string repositoryRoot)
        {
            var mutex = new Mutex(initiallyOwned: false, WriterMutexName(repositoryRoot));
            bool owned;
            try
            {
                owned = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            if (!owned)
            {
                mutex.Dispose();
                return new IndexWriterLock(null, isOwner: false);
            }

            return new IndexWriterLock(mutex, isOwner: true);
        }

        public void Dispose()
        {
            if (!IsOwner || _mutex is null || _released)
                return;
            _released = true;
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _mutex.Dispose();
        }

        private static string WriterMutexName(string repositoryRoot)
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot))
                .ToLowerInvariant();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return "fuse-index-writer-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
        }
    }
}
