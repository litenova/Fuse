using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// P1.2: transactional upsert/delete of files, symbols, chunks, nodes, and edges.
public sealed class WorkspaceIndexStoreUpsertTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UpsertFilesSymbolsAndChunksRecordsCounts()
    {
        await SeedOrderServiceFileAsync();

        var state = await _store.GetStateAsync(CancellationToken.None);
        Assert.Equal(1, state.FileCount);
        Assert.Equal(1, state.SymbolCount);
        Assert.Equal(WorkspaceIndexStatus.Warm, state.Status);
        Assert.Equal(1, await CountAsync("chunks"));
    }

    [Fact]
    public async Task UpsertFileIsIdempotentAndPreservesFileId()
    {
        await SeedOrderServiceFileAsync();
        var firstId = await ScalarAsync("SELECT file_id FROM files WHERE normalized_path = 'src/OrderService.cs';");

        // Re-upsert the same file with a changed hash; the file_id must not change.
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 200, 2, "hash2")],
            CancellationToken.None);
        var secondId = await ScalarAsync("SELECT file_id FROM files WHERE normalized_path = 'src/OrderService.cs';");

        Assert.Equal(firstId, secondId);
        Assert.Equal(1, await CountAsync("files"));
        Assert.Equal("hash2", await TextScalarAsync("SELECT content_hash FROM files WHERE normalized_path = 'src/OrderService.cs';"));
    }

    [Fact]
    public async Task EdgesPersistBetweenNodes()
    {
        await SeedOrderServiceFileAsync();
        await _store.UpsertNodesAsync(
            [
                new NodeRecord("type:App.IOrderService", "interface", "IOrderService", "App.IOrderService", "src/OrderService.cs"),
                new NodeRecord("type:App.OrderService", "type", "OrderService", "App.OrderService", "src/OrderService.cs"),
            ],
            CancellationToken.None);
        await _store.UpsertEdgesAsync(
            [new SemanticEdgeRecord("type:App.IOrderService", "type:App.OrderService", "di_resolves_to", 0.95, 0.95, EvidenceFilePath: "src/OrderService.cs")],
            CancellationToken.None);

        Assert.Equal(1, await CountAsync("edges"));
        Assert.Equal("di_resolves_to", await TextScalarAsync("SELECT edge_type FROM edges LIMIT 1;"));
    }

    [Fact]
    public async Task GetAllEdgesReturnsEveryStoredEdge()
    {
        await SeedOrderServiceFileAsync();
        await _store.UpsertNodesAsync(
            [
                new NodeRecord("type:App.IOrderService", "interface", "IOrderService", "App.IOrderService", "src/OrderService.cs"),
                new NodeRecord("type:App.OrderService", "type", "OrderService", "App.OrderService", "src/OrderService.cs"),
            ],
            CancellationToken.None);
        await _store.UpsertEdgesAsync(
            [
                new SemanticEdgeRecord("type:App.IOrderService", "type:App.OrderService", "di_resolves_to", 0.95, 0.95),
                new SemanticEdgeRecord("type:App.OrderService", "type:App.IOrderService", "implements", 0.9, 1.0),
            ],
            CancellationToken.None);

        var edges = await _store.GetAllEdgesAsync(CancellationToken.None);

        Assert.Equal(2, edges.Count);
        Assert.Contains(edges, e => e.EdgeType == "di_resolves_to");
        Assert.Contains(edges, e => e.EdgeType == "implements");
    }

    [Fact]
    public async Task DeleteFileDataClearsDerivedRowsButKeepsFile()
    {
        await SeedOrderServiceFileAsync();
        await _store.UpsertNodesAsync(
            [new NodeRecord("type:App.OrderService", "type", "OrderService", "App.OrderService", "src/OrderService.cs")],
            CancellationToken.None);
        await _store.UpsertEdgesAsync(
            [new SemanticEdgeRecord("type:App.OrderService", "type:App.OrderService", "references", 0.1, 0.5, EvidenceFilePath: "src/OrderService.cs")],
            CancellationToken.None);

        await _store.DeleteFileDataAsync("src/OrderService.cs", CancellationToken.None);

        Assert.Equal(1, await CountAsync("files"));
        Assert.Equal(0, await CountAsync("symbols"));
        Assert.Equal(0, await CountAsync("chunks"));
        Assert.Equal(0, await CountAsync("nodes"));
        Assert.Equal(0, await CountAsync("edges"));
    }

    [Fact]
    public async Task DeleteFileRemovesFileRecordAndFullTextRows()
    {
        await SeedOrderServiceFileAsync();

        await _store.DeleteFileAsync("src/OrderService.cs", CancellationToken.None);

        Assert.Equal(0, await CountAsync("files"));
        Assert.Equal(0, await CountAsync("symbols"));
        Assert.Equal(0, await CountAsync("chunks"));
        Assert.Empty(await _store.SearchAsync(new SearchQuery("OrderService", 10), CancellationToken.None));
    }

    [Fact]
    public async Task PruneFilesRemovesRowsOutsideCompleteInventory()
    {
        await SeedOrderServiceFileAsync();
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/Old.cs", "src/Old.cs", ".cs", 20, 2, "old")],
            CancellationToken.None);

        var removed = await _store.PruneFilesAsync(["src/OrderService.cs"], CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Equal(1, await CountAsync("files"));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM files WHERE normalized_path = 'src/Old.cs';"));
    }

    [Fact]
    public async Task ReindexChangedFileReplacesSymbols()
    {
        await SeedOrderServiceFileAsync();

        await _store.DeleteFileDataAsync("src/OrderService.cs", CancellationToken.None);
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 220, 3, "hash3")],
            CancellationToken.None);
        await _store.UpsertSymbolsAsync(
            [
                new SymbolRecord("symbol:App.OrderService", "src/OrderService.cs", "type", "OrderService", "App.OrderService", StartLine: 1, EndLine: 30),
                new SymbolRecord("symbol:App.OrderService.Create", "src/OrderService.cs", "method", "Create", "App.OrderService.Create", StartLine: 5, EndLine: 10),
            ],
            CancellationToken.None);

        Assert.Equal(2, await CountAsync("symbols"));
        Assert.Equal("hash3", await TextScalarAsync("SELECT content_hash FROM files WHERE normalized_path = 'src/OrderService.cs';"));
    }

    [Fact]
    public async Task ResetDiscardsAllDerivedRowsAndRecreatesSearchSchema()
    {
        await SeedOrderServiceFileAsync();
        await _store.SetMetaAsync("marker", "old", CancellationToken.None);

        await _store.ResetAsync(CancellationToken.None);

        var state = await _store.GetStateAsync(CancellationToken.None);
        Assert.Equal(0, state.FileCount);
        Assert.Equal(0, state.SymbolCount);
        Assert.Null(await _store.GetMetaAsync("marker", CancellationToken.None));
        Assert.Empty(await _store.SearchAsync(new SearchQuery("OrderService", 10), CancellationToken.None));
    }

    private async Task SeedOrderServiceFileAsync()
    {
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 120, 1, "hash1")],
            CancellationToken.None);
        await _store.UpsertSymbolsAsync(
            [new SymbolRecord("symbol:App.OrderService", "src/OrderService.cs", "type", "OrderService", "App.OrderService", StartLine: 1, EndLine: 20)],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [new ChunkRecord("chunk:App.OrderService", "src/OrderService.cs", "type", "App.OrderService", 1, 20, "th1", 50, 20, SymbolId: "symbol:App.OrderService", Name: "OrderService")],
            CancellationToken.None);
    }

    private async Task<long> CountAsync(string table) =>
        await ScalarAsync($"SELECT count(*) FROM {table};");

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(CancellationToken.None);
        return result is long value ? value : 0;
    }

    private async Task<string?> TextScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(CancellationToken.None);
        return result as string;
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
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
