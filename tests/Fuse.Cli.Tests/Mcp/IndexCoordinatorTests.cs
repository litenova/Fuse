using System.Security.Cryptography;
using System.Text;
using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

/// <summary>
///     R14: unit tests for <see cref="IndexCoordinator" /> queue ordering and cross-process writer arbitration.
/// </summary>
public sealed class IndexCoordinatorTests
{
    [Fact]
    public async Task ExecuteWriteAsync_serializes_writes_for_same_root()
    {
        var root = CreateTempRoot();
        var coordinator = new IndexCoordinator();
        var order = new List<int>();
        var firstStarted = new TaskCompletionSource();

        var first = coordinator.ExecuteWriteAsync(root, async (_, ct) =>
        {
            order.Add(1);
            firstStarted.SetResult();
            await Task.Delay(200, ct);
            return 0;
        }, CancellationToken.None);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.ExecuteWriteAsync(root, async (_, _) =>
        {
            order.Add(2);
            return 0;
        }, CancellationToken.None);

        await Task.WhenAll(first, second);
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task ExecuteWriteAsync_serializes_repository_root_and_nested_path_as_one_identity()
    {
        var root = CreateTempRoot();
        var nested = Path.Combine(root, "tests", "bin", "Release");
        Directory.CreateDirectory(nested);
        var coordinator = new IndexCoordinator();
        var order = new List<int>();
        var firstStarted = new TaskCompletionSource();

        var first = coordinator.ExecuteWriteAsync(nested, async (store, ct) =>
        {
            await store.InitializeAsync(ct);
            order.Add(1);
            firstStarted.SetResult();
            await Task.Delay(200, ct);
            return 0;
        }, CancellationToken.None);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.ExecuteWriteAsync(root, (_, _) =>
        {
            order.Add(2);
            return Task.FromResult(0);
        }, CancellationToken.None);

        await Task.WhenAll(first, second);
        Assert.Equal([1, 2], order);
        Assert.True(File.Exists(FuseStorePaths.ResolveDatabasePath(root)));
        Assert.Equal(FuseStorePaths.ResolveDatabasePath(root), FuseStorePaths.ResolveDatabasePath(nested));
    }

    [Fact]
    public async Task ExecuteWriteAsync_returns_index_busy_when_cross_process_mutex_held()
    {
        var root = CreateTempRoot();
        using var foreignLock = AcquireWriterMutex(root);
        var coordinator = new IndexCoordinator();

        await Assert.ThrowsAsync<IndexBusyException>(() =>
            coordinator.ExecuteWriteAsync(
                root,
                async (store, ct) =>
                {
                    await store.InitializeAsync(ct);
                    return 0;
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteWriteAsync_nested_path_uses_repository_root_mutex()
    {
        var root = CreateTempRoot();
        var nested = Path.Combine(root, "src", "Nested");
        Directory.CreateDirectory(nested);
        using var foreignLock = AcquireWriterMutex(root);
        var coordinator = new IndexCoordinator();

        await Assert.ThrowsAsync<IndexBusyException>(() =>
            coordinator.ExecuteWriteAsync(nested, (_, _) => Task.FromResult(0), CancellationToken.None));
    }

    [Fact]
    public async Task OpenForReadOnlyAsync_uses_warm_read_without_initialize_meta_write()
    {
        var root = CreateTempRoot();
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var seed = new WorkspaceIndexStore(databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.UpsertFilesAsync(
                [new IndexedFileRecord("App.cs", "App.cs", ".cs", 10, DateTime.UtcNow.Ticks, "hash", Language: "csharp")],
                CancellationToken.None);
        }

        var coordinator = new IndexCoordinator();
        await using var store = await coordinator.OpenForReadOnlyAsync(root, CancellationToken.None);
        var state = await store.GetStateAsync(CancellationToken.None);
        Assert.Equal(1, state.FileCount);
    }

    [Fact]
    public async Task Concurrent_warm_read_opens_do_not_deadlock()
    {
        var root = CreateTempRoot();
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var seed = new WorkspaceIndexStore(databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.UpsertFilesAsync(
                [new IndexedFileRecord("App.cs", "App.cs", ".cs", 10, DateTime.UtcNow.Ticks, "hash", Language: "csharp")],
                CancellationToken.None);
        }

        var coordinator = new IndexCoordinator();
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => coordinator.OpenForReadOnlyAsync(root, CancellationToken.None))
            .ToArray();
        var stores = await Task.WhenAll(tasks);
        Assert.All(stores, s => Assert.NotNull(s));
        foreach (var store in stores)
            await store.DisposeAsync();
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-coordinator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root.AsIsolatedRepo();
    }

    private static IDisposable AcquireWriterMutex(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var name = "fuse-index-writer-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
        return new ForeignMutexHolder(name);
    }

    // Holds a named mutex on a dedicated thread, so the coordinator's WaitOne on any other thread sees it held.
    // A Windows named mutex is owned per thread: acquiring it on the test thread would make the coordinator's
    // reacquire reentrant (and thus succeed), which is not the cross-process contention the test simulates.
    private sealed class ForeignMutexHolder : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _acquired = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public ForeignMutexHolder(string name)
        {
            _thread = new Thread(() =>
            {
                using var mutex = new Mutex(initiallyOwned: false, name);
                if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("could not acquire the foreign writer mutex.");
                _acquired.Set();
                _release.Wait();
                mutex.ReleaseMutex();
            })
            {
                IsBackground = true,
            };
            _thread.Start();
            Assert.True(_acquired.Wait(TimeSpan.FromSeconds(5)));
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(5));
            _acquired.Dispose();
            _release.Dispose();
        }
    }
}

/// <summary>
///     R14: concurrent MCP tool calls and reads during background upgrade.
/// </summary>
[Collection("FuseToolsResidentProvider")]
public sealed class IndexConcurrencyIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-index-concurrency", Guid.NewGuid().ToString("N"));
    private readonly IndexCoordinator _coordinator = new();
    private SemanticIndexer Indexer => _provider.GetRequiredService<SemanticIndexer>();
    private IChangeSource ChangeSource => _provider.GetRequiredService<IChangeSource>();
    private FuseMcpRuntime Runtime => _provider.GetRequiredService<FuseMcpRuntime>();

    public Task InitializeAsync()
    {
        _root.AsIsolatedRepo();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Concurrent_fuse_find_calls_in_one_process_do_not_deadlock()
    {
        // The read path reconciles the warm store against disk (N6 freshness), so the seeded record must have a
        // real file behind it or reconcile removes it as deleted before the find runs.
        await File.WriteAllTextAsync(Path.Combine(_root, "Widget.cs"), "public class Widget { public void Run() { } }");
        var databasePath = FuseStorePaths.ResolveDatabasePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var seed = new WorkspaceIndexStore(databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.UpsertFilesAsync(
                [new IndexedFileRecord("Widget.cs", "Widget.cs", ".cs", 10, DateTime.UtcNow.Ticks, "hash", Language: "csharp")],
                CancellationToken.None);
            await seed.UpsertSymbolsAsync(
                [new SymbolRecord("sym1", "Widget.cs", "class", "Widget", "Widget")],
                CancellationToken.None);
        }

        var tasks = Enumerable.Range(0, 8).Select(_ => FindToolOperations.ExecuteAsync(
            Indexer,
            ChangeSource,
            "Widget",
            path: _root,
            kind: "symbol",
            runtime: Runtime)).ToArray();

        var results = await Task.WhenAll(tasks);

        // Reaching here at all is the deadlock assertion. Every result must then be either the indexed answer or
        // one of the documented deferrals: a read that races this process's own reconcile abstains with the
        // availability header rather than blocking, so requiring all eight to carry the symbol would assert
        // against the contract and fail intermittently under load.
        Assert.All(results, result => Assert.True(
            result.Contains("Widget", StringComparison.Ordinal) || IsDocumentedDeferral(result),
            $"a concurrent find returned neither the indexed answer nor a documented deferral: {result}"));

        // At least one call must return the real answer, so the test still proves concurrent reads are served.
        Assert.Contains(results, result => result.Contains("Widget", StringComparison.Ordinal));
        Assert.DoesNotContain(results, r => r.StartsWith(FuseOperationalErrors.InternalErrorPrefix));
    }

    // The deferral shapes a read may return while the index is contended or refreshing: the structured
    // availability header (R20) or an operational index-state prefix.
    private static bool IsDocumentedDeferral(string result) =>
        result.StartsWith("index_state:", StringComparison.Ordinal)
        || result.StartsWith(FuseOperationalErrors.IndexBusyPrefix, StringComparison.Ordinal)
        || result.StartsWith(FuseOperationalErrors.IndexRebuildingPrefix, StringComparison.Ordinal)
        || result.StartsWith(FuseOperationalErrors.IndexNotBuiltPrefix, StringComparison.Ordinal);

    [Fact]
    public async Task Warm_reads_complete_while_coordinator_write_lock_is_held()
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var seed = new WorkspaceIndexStore(databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.SetMetaAsync("index_mode", "syntax", CancellationToken.None);
            await seed.SetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, "1", CancellationToken.None);
            await seed.UpsertFilesAsync(
                [new IndexedFileRecord("Service.cs", "Service.cs", ".cs", 10, DateTime.UtcNow.Ticks, "hash", Language: "csharp")],
                CancellationToken.None);
            await seed.UpsertSymbolsAsync(
                [new SymbolRecord("sym1", "Service.cs", "class", "Service", "Service")],
                CancellationToken.None);
        }

        var writeReleased = new TaskCompletionSource();
        var write = _coordinator.ExecuteWriteAsync(
            _root,
            async (_, ct) =>
            {
                await writeReleased.Task.WaitAsync(ct);
                return 0;
            },
            CancellationToken.None);

        await Task.Delay(50);

        await using var store = await _coordinator.OpenForReadOnlyAsync(_root, CancellationToken.None);
        var state = await store.GetStateAsync(CancellationToken.None);
        Assert.Equal(1, state.FileCount);
        Assert.Equal("syntax", state.Mode);

        writeReleased.SetResult();
        await write.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Parallel_warm_opens_stress_test_completes()
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var seed = new WorkspaceIndexStore(databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            var files = Enumerable.Range(0, 40)
                .Select(i => new IndexedFileRecord($"F{i}.cs", $"F{i}.cs", ".cs", 10, DateTime.UtcNow.Ticks, $"h{i}", Language: "csharp"))
                .ToList();
            await seed.UpsertFilesAsync(files, CancellationToken.None);
        }

        var opens = Enumerable.Range(0, 24).Select(_ => FindToolOperations.ExecuteAsync(
            Indexer,
            ChangeSource,
            "F1",
            path: _root,
            kind: "path",
            runtime: Runtime)).ToArray();
        var results = await Task.WhenAll(opens);
        Assert.Equal(24, results.Length);
        Assert.DoesNotContain(results, r => r.StartsWith(FuseOperationalErrors.InternalErrorPrefix));
    }

    public Task DisposeAsync()
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(_root);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _provider.Dispose();
}
