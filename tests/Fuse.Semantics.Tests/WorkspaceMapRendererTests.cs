using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Semantics.Tests;

// P2.4: the workspace map reads indexed symbols and routes from the store (the `fuse map` data path).
public sealed class WorkspaceMapRendererTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-semantics-tests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private WorkspaceIndexStore _store = null!;

    public WorkspaceMapRendererTests() =>
        _databasePath = Path.Combine(_root, ".fuse", "fuse.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "OrderService.cs"),
            "namespace App; public interface IOrderService { } public class OrderService : IOrderService { }");
        File.WriteAllText(Path.Combine(_root, "src", "OrdersController.cs"), """
            using Microsoft.AspNetCore.Mvc;
            [Route("api/orders")]
            public class OrdersController : ControllerBase
            {
                [HttpPost("{id}")]
                public IActionResult Create(int id) => Ok();
            }
            """);

        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);

        var indexer = CreateIndexer();
        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);
    }

    [Fact]
    public async Task RendersSymbolsAndRoutes()
    {
        var renderer = new WorkspaceMapRenderer(_store);

        var map = await renderer.RenderAsync(MapDetail.All, 200, CancellationToken.None);

        Assert.Contains("workspace map", map);
        Assert.Contains("IOrderService", map);
        Assert.Contains("OrderService", map);
        Assert.Contains("POST", map);
        Assert.Contains("/api/orders/{id}", map);
    }

    [Fact]
    public async Task SymbolsDetailOmitsRoutesSection()
    {
        var renderer = new WorkspaceMapRenderer(_store);

        var map = await renderer.RenderAsync(MapDetail.Symbols, 200, CancellationToken.None);

        Assert.Contains("symbols (", map);
        Assert.DoesNotContain("routes (", map);
    }

    [Fact]
    public async Task StoreListsPublicApiSymbolsFirst()
    {
        var symbols = await _store.ListSymbolsAsync(200, CancellationToken.None);

        Assert.NotEmpty(symbols);
        var firstPrivate = symbols.ToList().FindIndex(s => !s.IsPublicApi);
        var lastPublic = symbols.ToList().FindLastIndex(s => s.IsPublicApi);
        if (firstPrivate >= 0 && lastPublic >= 0)
            Assert.True(lastPublic < firstPrivate, "public-API symbols should sort before non-public ones");
    }

    private static SemanticIndexer CreateIndexer()
    {
        var fileSystem = new PhysicalFileSystem();
        var pipeline = new FileCollectionPipeline(
            fileSystem,
            new GitIgnoreParser(fileSystem),
            [new GitIgnoreFilter(), new ExtensionFilter(), new ExcludedDirectoryFilter(), new EmptyFileFilter(), new BinaryFileFilter(fileSystem)]);
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
