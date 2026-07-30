using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Semantics.Tests;

// N4 tier-1: the indexer's build-capture write path ingests the worker's graph bundle into the store. Tested
// with a synthetic capture (the worker-to-parent round-trip is covered separately) so the ingest is exercised
// deterministically without spawning a build.
public sealed class IndexFromCaptureTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-capture-ingest", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), "fuse-capture-ingest-db", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "OrderService.cs"),
            "namespace App; public interface IOrderService { } public sealed class OrderService : IOrderService { }");
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ingest_writes_the_bundle_symbols_nodes_and_edges_to_the_store()
    {
        var files = new List<IndexedFileRecord>
        {
            new("OrderService.cs", "OrderService.cs", ".cs", 120, 1, "h1", Language: "csharp"),
        };
        var capture = CaptureResult.Ok(
        [
            new CapturedProject(
                Name: "App", FilePath: "App.csproj", AssemblyName: "App", ErrorCount: 0, TypeCount: 2,
                Symbols:
                [
                    new SymbolRecord("symbol:App.OrderService", "OrderService.cs", "type", "OrderService", "App.OrderService", StartLine: 1, EndLine: 1, IsPublicApi: true),
                ],
                Nodes:
                [
                    new NodeRecord("type:App.IOrderService", "interface", "IOrderService", "App.IOrderService", "OrderService.cs"),
                    new NodeRecord("type:App.OrderService", "type", "OrderService", "App.OrderService", "OrderService.cs"),
                ],
                Edges:
                [
                    new SemanticEdgeRecord("type:App.IOrderService", "type:App.OrderService", "di_resolves_to", 0.95, 0.95, EvidenceFilePath: "OrderService.cs"),
                ],
                Routes: [], DiRegistrations: [], OptionsBindings: []),
        ]);

        var result = await CreateIndexer().IndexFromCaptureAsync(_root, _store, files, capture, CancellationToken.None);

        Assert.Equal("semantic", result.Mode);
        Assert.Equal(1, result.SymbolCount);
        var state = await _store.GetStateAsync(CancellationToken.None);
        Assert.Equal(1, state.SymbolCount);
        var edges = await _store.GetAllEdgesAsync(CancellationToken.None);
        Assert.Contains(edges, e => e.EdgeType == "di_resolves_to" && e.FromNodeId == "type:App.IOrderService");
    }

    [Fact]
    public async Task Multi_target_capture_unions_exclusive_facts_and_records_deterministic_availability()
    {
        var files = new List<IndexedFileRecord>
        {
            new("OrderService.cs", "OrderService.cs", ".cs", 120, 1, "h1", Language: "csharp"),
        };
        var capture = CaptureResult.Ok(
        [
            new CapturedProject(
                Name: "App", FilePath: "App.csproj", AssemblyName: "App", ErrorCount: 0, TypeCount: 2,
                Symbols:
                [
                    Symbol("symbol:App.Shared", "Shared"),
                    Symbol("symbol:App.ModernOnly", "ModernOnly"),
                ],
                Nodes:
                [
                    Node("type:App.Shared", "Shared"),
                    Node("type:App.ModernOnly", "ModernOnly"),
                ],
                Edges: [new SemanticEdgeRecord("type:App.Shared", "type:App.ModernOnly", "references", 1, 1, EvidenceFilePath: "OrderService.cs")],
                Routes: [new RouteRecord("route:GET:/modern", "GET", "/modern", "OrderService.cs", 1, 1, "minimal-api")],
                DiRegistrations: [new DiRegistrationRecord("di:shared", "Shared", "Singleton", "OrderService.cs", 1, 1, "generic", 1)],
                OptionsBindings: [new OptionsBindingRecord("options:shared", "SharedOptions", "OrderService.cs", 1, 1, "bind", 1)],
                TargetFramework: "net8.0"),
            new CapturedProject(
                Name: "App", FilePath: "App.csproj", AssemblyName: "App", ErrorCount: 0, TypeCount: 2,
                Symbols:
                [
                    Symbol("symbol:App.Shared", "Shared"),
                    Symbol("symbol:App.StandardOnly", "StandardOnly"),
                ],
                Nodes:
                [
                    Node("type:App.Shared", "Shared"),
                    Node("type:App.StandardOnly", "StandardOnly"),
                ],
                Edges: [new SemanticEdgeRecord("type:App.Shared", "type:App.StandardOnly", "references", 1, 1, EvidenceFilePath: "OrderService.cs")],
                Routes: [new RouteRecord("route:GET:/standard", "GET", "/standard", "OrderService.cs", 1, 1, "minimal-api")],
                DiRegistrations: [new DiRegistrationRecord("di:standard", "StandardOnly", "Singleton", "OrderService.cs", 1, 1, "generic", 1)],
                OptionsBindings: [new OptionsBindingRecord("options:standard", "StandardOptions", "OrderService.cs", 1, 1, "bind", 1)],
                TargetFramework: "netstandard2.0"),
        ]);

        var indexer = CreateIndexer();
        var first = await indexer.IndexFromCaptureAsync(_root, _store, files, capture, CancellationToken.None);
        var firstAvailability = await _store.GetTfmAvailabilityAsync(CancellationToken.None);
        var second = await indexer.IndexFromCaptureAsync(_root, _store, files, capture, CancellationToken.None);
        var secondAvailability = await _store.GetTfmAvailabilityAsync(CancellationToken.None);

        Assert.Equal(1, first.ProjectCount);
        Assert.Equal(3, first.SymbolCount);
        Assert.Equal(first.Mode, second.Mode);
        Assert.Equal(first.FileCount, second.FileCount);
        Assert.Equal(first.ProjectCount, second.ProjectCount);
        Assert.Equal(first.SymbolCount, second.SymbolCount);
        Assert.Equal(first.ChunkCount, second.ChunkCount);
        Assert.Equal(first.RouteCount, second.RouteCount);
        Assert.Equal(firstAvailability, secondAvailability);
        Assert.Equal(3, (await _store.GetStateAsync(CancellationToken.None)).SymbolCount);
        Assert.Contains(await _store.FindSymbolsByNameAsync("ModernOnly", 10, CancellationToken.None), symbol => symbol.SymbolId == "symbol:App.ModernOnly");
        Assert.Contains(await _store.FindSymbolsByNameAsync("StandardOnly", 10, CancellationToken.None), symbol => symbol.SymbolId == "symbol:App.StandardOnly");
        Assert.Equal(
            ["net8.0", "netstandard2.0"],
            firstAvailability
                .Where(item => item.EntityKind == "symbol" && item.EntityId == "symbol:App.Shared")
                .Select(item => item.TargetFramework)
                .ToList());
        Assert.Contains(firstAvailability, item => item.EntityKind == "symbol" && item.EntityId == "symbol:App.StandardOnly" && item.TargetFramework == "netstandard2.0");
        Assert.Contains(firstAvailability, item => item.EntityKind == "project" && item.EntityId == "App.csproj" && item.TargetFramework == "net8.0");
    }

    private static SymbolRecord Symbol(string id, string name) =>
        new(id, "OrderService.cs", "type", name, $"App.{name}", StartLine: 1, EndLine: 1, ProjectPath: "App.csproj");

    private static NodeRecord Node(string id, string name) =>
        new(id, "type", name, $"App.{name}", "OrderService.cs", ProjectPath: "App.csproj");

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
            SemanticAnalysisRunner.CreateDefault());
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        foreach (var dir in new[] { _root, Path.GetDirectoryName(_databasePath)! })
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
