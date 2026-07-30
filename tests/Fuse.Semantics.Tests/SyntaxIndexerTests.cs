using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using System.Text;
using Xunit;

namespace Fuse.Semantics.Tests;

// P2.2: syntax-level symbol + chunk extraction into the store, and FTS over the stored chunks.
// Routed through SemanticIndexer.IndexSyntaxFirstAsync (provider-driven syntax tier).
public sealed class SyntaxIndexerTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-semantics-tests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private WorkspaceIndexStore _store = null!;

    public SyntaxIndexerTests() =>
        _databasePath = Path.Combine(_root, ".fuse", "fuse.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "OrderService.cs"), """
            namespace App.Services;

            public interface IOrderService
            {
                void Place(int id);
            }

            public class OrderService : IOrderService
            {
                private readonly int _max;

                public OrderService(int max) => _max = max;

                public void Place(int id) { }
            }
            """);

        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IndexExtractsTypeAndMemberSymbols()
    {
        var indexer = CreateIndexer();

        var result = await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);

        Assert.Equal(1, result.FileCount);
        Assert.Equal("syntax", result.Mode);
        // 2 types (IOrderService, OrderService) + members (Place x2, ctor, field, _max field).
        Assert.True(result.SymbolCount >= 5, $"expected >= 5 symbols, got {result.SymbolCount}");
        Assert.True(await ExistsAsync("SELECT 1 FROM symbols WHERE name = 'OrderService' AND kind = 'class';"));
        Assert.True(await ExistsAsync("SELECT 1 FROM symbols WHERE name = 'IOrderService' AND kind = 'interface';"));
        Assert.True(await ExistsAsync("SELECT 1 FROM symbols WHERE name = 'Place' AND kind = 'method';"));
    }

    [Fact]
    public async Task IndexedChunksAreSearchableByFts()
    {
        var indexer = CreateIndexer();
        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);

        var hits = await _store.SearchAsync(new SearchQuery("OrderService"), CancellationToken.None);

        Assert.Contains(hits, h => h.FilePath.EndsWith("src/OrderService.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReindexAfterEditReplacesSymbols()
    {
        var indexer = CreateIndexer();
        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);
        var before = await CountAsync("SELECT count(*) FROM symbols;");

        // Edit the file to remove a member, then clear its data and reindex.
        File.WriteAllText(Path.Combine(_root, "src", "OrderService.cs"),
            "namespace App.Services; public class OrderService { }");
        await _store.DeleteFileDataAsync("src/OrderService.cs", CancellationToken.None);
        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);

        var after = await CountAsync("SELECT count(*) FROM symbols;");
        Assert.True(after < before, $"expected fewer symbols after trimming the file ({after} < {before})");
        Assert.True(await ExistsAsync("SELECT 1 FROM symbols WHERE name = 'OrderService';"));
        Assert.False(await ExistsAsync("SELECT 1 FROM symbols WHERE name = 'IOrderService';"));
    }

    [Fact]
    public async Task IndexStoresRoutesFromControllers()
    {
        File.WriteAllText(Path.Combine(_root, "src", "OrdersController.cs"), """
            using Microsoft.AspNetCore.Mvc;

            [Route("api/orders")]
            public class OrdersController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() => Ok();
            }
            """);
        var indexer = CreateIndexer();

        var result = await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);

        Assert.True(result.RouteCount >= 1);
        // A verb attribute with no route argument falls back to the handler name as the path segment.
        Assert.True(await ExistsAsync("SELECT 1 FROM routes WHERE route_pattern = '/api/orders/List' AND http_method = 'GET';"));
    }

    [Fact]
    public async Task GeneratedSourcesRetainDeclarationsWithoutIndexingMethodBodies()
    {
        var generatedDirectory = Path.Combine(_root, "generated");
        Directory.CreateDirectory(generatedDirectory);
        var generatedPath = Path.Combine(generatedDirectory, "GeneratedModel.g.cs");
        var generatedSource = """
            // <auto-generated>
            namespace Generated;
            public sealed class GeneratedModel
            {
                public string Render() { var value = "GENERATED_BODY_MARKER"; return value; }
            }
            """ + "\n// filler " + new string('x', 3 * 1024 * 1024);
        await File.WriteAllTextAsync(generatedPath, generatedSource);

        var oversizedPath = Path.Combine(generatedDirectory, "Oversized.g.cs");
        await File.WriteAllTextAsync(
            oversizedPath,
            "// <auto-generated>\nnamespace Generated; public sealed class Oversized { }\n// " +
            new string('x', 5 * 1024 * 1024 + 128));

        var indexer = CreateIndexer();
        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);

        var symbols = await _store.FindSymbolsByNameAsync("GeneratedModel", 10, CancellationToken.None);
        Assert.Contains(symbols, symbol => symbol.FilePath.EndsWith("generated/GeneratedModel.g.cs", StringComparison.Ordinal));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM chunk_fts WHERE chunk_fts MATCH '\"GENERATED_BODY_MARKER\"';"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM chunks WHERE signature LIKE '%GENERATED_BODY_MARKER%';"));
        Assert.Equal("declarations", await ScalarTextAsync("SELECT index_detail FROM files WHERE normalized_path = 'generated/GeneratedModel.g.cs';"));
        Assert.Equal("inventory_only", await ScalarTextAsync("SELECT index_detail FROM files WHERE normalized_path = 'generated/Oversized.g.cs';"));
        var detailLimited = await _store.GetMetaAsync(WorkspaceIndexStore.DetailLimitedFilesMetaKey, CancellationToken.None);
        Assert.Contains("generated/Oversized.g.cs", detailLimited, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnchangedSyntaxRefreshSkipsFileWritesAndOnlyRewritesChangedRows()
    {
        var indexer = CreateIndexer();
        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);

        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);
        Assert.Equal("0", await _store.GetMetaAsync("last_index_file_upserts", CancellationToken.None));
        Assert.Equal("0", await _store.GetMetaAsync("last_index_fts_replacements", CancellationToken.None));

        await File.WriteAllTextAsync(
            Path.Combine(_root, "src", "OrderService.cs"),
            "namespace App.Services; public sealed class OrderService { public int Version => 2; }");
        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);
        Assert.Equal("1", await _store.GetMetaAsync("last_index_file_upserts", CancellationToken.None));
        Assert.NotEqual("0", await _store.GetMetaAsync("last_index_fts_replacements", CancellationToken.None));

        File.Delete(Path.Combine(_root, "src", "OrderService.cs"));
        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);
        Assert.False(await ExistsAsync("SELECT 1 FROM files WHERE normalized_path = 'src/OrderService.cs';"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM search_documents;"));
    }

    [Fact]
    public async Task InterruptedSyntaxBatchIsReplayedBeforeTheIndexBecomesReady()
    {
        var indexer = CreateIndexer();
        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);

        await _store.SetMetaAsync(
            "pending_syntax_batch",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("src/OrderService.cs")),
            CancellationToken.None);
        await _store.ClearFileDataAsync(["src/OrderService.cs"], CancellationToken.None);
        Assert.False(await ExistsAsync("SELECT 1 FROM symbols WHERE name = 'OrderService';"));

        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);

        Assert.True(await ExistsAsync("SELECT 1 FROM symbols WHERE name = 'OrderService';"));
        Assert.Equal(string.Empty, await _store.GetMetaAsync("pending_syntax_batch", CancellationToken.None));
    }

    private static SemanticIndexer CreateIndexer()
    {
        var fileSystem = new PhysicalFileSystem();
        var pipeline = new FileCollectionPipeline(
            fileSystem,
            new GitIgnoreParser(fileSystem),
            [
                new GitIgnoreFilter(),
                new ExtensionFilter(),
                new ExcludedDirectoryFilter(),
                new EmptyFileFilter(),
                new BinaryFileFilter(fileSystem),
            ]);
        return new SemanticIndexer(
            new DotNetWorkspaceDiscoverer(),
            new RoslynWorkspaceLoader(),
            new WorkspaceFileScanner(pipeline),
            new SemanticSymbolExtractor(),
            new SyntaxSymbolExtractor(),
            new SyntaxRouteExtractor(),
            new FileHashService(),
            Fuse.Semantics.Analyzers.SemanticAnalysisRunner.CreateDefault());
    }

    private async Task<bool> ExistsAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None) is not null;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None) is long value ? value : 0;
    }

    private async Task<string?> ScalarTextAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None) as string;
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
