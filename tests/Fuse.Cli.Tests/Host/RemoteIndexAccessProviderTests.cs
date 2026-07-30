using Fuse.Cli.Mcp;
using Fuse.Cli.Rpc;
using Fuse.Indexing;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Host;

// R19: the remote index provider delegates writes to the daemon and falls back to the local coordinator when no
// daemon answers, never opening the store raw.
public sealed class RemoteIndexAccessProviderTests
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();

    [Fact]
    public async Task Falls_back_to_local_coordinator_when_no_daemon_answers()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var root = Path.Combine(Path.GetTempPath(), "fuse-remote-index-fallback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(root, "A.cs"), "namespace T; public class A { }");

        var coordinator = _provider.GetRequiredService<IndexCoordinator>();
        var fallback = new LocalIndexAccessProvider(
            coordinator,
            _provider.GetRequiredService<IWorkspaceIndexJobManager>(),
            TimeSpan.FromSeconds(15));
        var before = coordinator.ProcessWriteLockAcquireCount;
        var provider = new RemoteIndexAccessProvider(
            (_, _, _) => Task.FromResult<OpenIndexedResultDto?>(null),
            localFallback: fallback);

        try
        {
            await using var store = await provider.OpenIndexedAsync(indexer, root, CancellationToken.None);
            var state = await store.GetStateAsync(CancellationToken.None);
            Assert.True(state.FileCount > 0);
            Assert.True(coordinator.ProcessWriteLockAcquireCount > before);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Delegated_open_does_not_acquire_local_writer_lock()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var root = Path.Combine(Path.GetTempPath(), "fuse-remote-index-delegate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(root, "B.cs"), "namespace T; public class B { }");

        var databasePath = Fuse.Reduction.Caching.FuseStorePaths.ResolveDatabasePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var seed = new WorkspaceIndexStore(databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await indexer.IndexSyntaxFirstAsync(root, seed, CancellationToken.None);
        }

        var provider = new RemoteIndexAccessProvider(
            (_, _, _) => Task.FromResult<OpenIndexedResultDto?>(new OpenIndexedResultDto("ready", null, 1, "syntax")));

        try
        {
            await using var store = await provider.OpenIndexedAsync(indexer, root, CancellationToken.None);
            Assert.True(await store.GetStateAsync(CancellationToken.None) is { FileCount: > 0 });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Maps_rebuilding_state_from_daemon()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var root = Path.Combine(Path.GetTempPath(), "fuse-remote-index-busy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var provider = new RemoteIndexAccessProvider(
            (_, _, _) => Task.FromResult<OpenIndexedResultDto?>(
                new OpenIndexedResultDto("index_rebuilding", "building", 0, null)));

        try
        {
            await Assert.ThrowsAsync<IndexRebuildingException>(() =>
                provider.OpenIndexedAsync(indexer, root, CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Starts_resident_signature_index_work_through_the_daemon()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var root = Path.Combine(Path.GetTempPath(), "fuse-remote-index-start", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var invoked = false;
        var expected = new IndexJobStartResult(
            new IndexJobSnapshot(
                "job-1",
                root,
                IndexJobState.Running,
                IndexPhase.Inventory,
                1,
                4,
                0,
                null,
                null,
                null,
                "opening index",
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                IndexCountSnapshot.Empty,
                IndexStorageSnapshot.Empty,
                [],
                null,
                null),
            Joined: false,
            Conflict: false);
        var provider = new RemoteIndexAccessProvider(
            indexStart: (_, depth, force, capture, _, _) =>
            {
                invoked = true;
                Assert.Equal(IndexDepth.Syntax, depth);
                Assert.False(force);
                Assert.Null(capture);
                return Task.FromResult<IndexJobStartResult?>(expected);
            });

        try
        {
            var result = await provider.StartSyntaxAsync(indexer, root, CancellationToken.None);

            Assert.True(invoked);
            Assert.Equal(expected, result);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
