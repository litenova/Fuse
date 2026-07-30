using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

public sealed class WorkspaceIndexMaintenanceTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-maintenance", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await _store.UpsertFilesAsync(
        [
            new IndexedFileRecord("src/Widget.cs", "src/Widget.cs", ".cs", 64, 0, "widget-hash"),
        ], CancellationToken.None);
        await _store.UpsertChunksAsync(
        [
            new ChunkRecord(
                "chunk:Widget",
                "src/Widget.cs",
                "type",
                "Widget",
                1,
                4,
                "chunk-hash",
                16,
                8,
                Name: "Widget",
                Signature: "public sealed class Widget",
                Body: "public sealed class Widget { public string Name => \"widget\"; }"),
        ], CancellationToken.None);
    }

    [Fact]
    public async Task Completed_rebuild_compacts_full_text_and_truncates_wal()
    {
        var result = await _store.MaintainAfterIndexAsync(
            completed: true,
            replacedSearchRows: 1,
            cancellationToken: CancellationToken.None);

        Assert.True(result.WalCheckpointed);
        Assert.Equal(_store.FullTextSearchAvailable, result.FullTextOptimized);
        Assert.False(_store.RequiresFullTextOptimization);
        AssertWalIsEmpty();
    }

    [Fact]
    public async Task Large_incremental_replacement_runs_one_bounded_full_text_merge()
    {
        await _store.MaintainAfterIndexAsync(
            completed: true,
            replacedSearchRows: 0,
            cancellationToken: CancellationToken.None);

        var result = await _store.MaintainAfterIndexAsync(
            completed: true,
            replacedSearchRows: 1,
            cancellationToken: CancellationToken.None);

        Assert.Equal(_store.FullTextSearchAvailable, result.FullTextMerged);
        AssertWalIsEmpty();
    }

    [Fact]
    public async Task Cancelled_job_truncates_wal_without_clearing_pending_rebuild_maintenance()
    {
        var result = await _store.MaintainAfterIndexAsync(
            completed: false,
            replacedSearchRows: 1,
            cancellationToken: CancellationToken.None);

        Assert.True(result.WalCheckpointed);
        Assert.False(result.FullTextOptimized);
        Assert.False(result.FullTextMerged);
        Assert.True(_store.RequiresFullTextOptimization);
        AssertWalIsEmpty();
    }

    private void AssertWalIsEmpty()
    {
        var walPath = _databasePath + "-wal";
        Assert.True(!File.Exists(walPath) || new FileInfo(walPath).Length == 0, "Expected the WAL to be truncated.");
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        var directory = Path.GetDirectoryName(_databasePath);
        try
        {
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
