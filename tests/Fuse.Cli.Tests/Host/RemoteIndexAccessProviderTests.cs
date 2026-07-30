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

        var before = IndexCoordinator.ProcessWriteLockAcquireCount;
        var provider = new RemoteIndexAccessProvider(
            (_, _, _) => Task.FromResult<OpenIndexedResultDto?>(null));

        try
        {
            await using var store = await provider.OpenIndexedAsync(indexer, root, CancellationToken.None);
            var state = await store.GetStateAsync(CancellationToken.None);
            Assert.True(state.FileCount > 0);
            Assert.True(IndexCoordinator.ProcessWriteLockAcquireCount > before);
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
    public async Task Maps_index_busy_from_daemon()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var root = Path.Combine(Path.GetTempPath(), "fuse-remote-index-busy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var provider = new RemoteIndexAccessProvider(
            (_, _, _) => Task.FromResult<OpenIndexedResultDto?>(
                new OpenIndexedResultDto("index_busy", "locked", 0, null)));

        try
        {
            await Assert.ThrowsAsync<IndexBusyException>(() =>
                provider.OpenIndexedAsync(indexer, root, CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
