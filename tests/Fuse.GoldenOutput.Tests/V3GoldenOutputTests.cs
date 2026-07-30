using System.Text;
using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Indexing;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Markers;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Skeleton;
using Fuse.Plugins.Languages.CSharp.Roslyn;
using Fuse.Reduction;
using Fuse.Reduction.Security;
using Fuse.Reduction.Tokenization;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.GoldenOutput.Tests;

// P10.1 / 16.3: golden output for the V3 emitter shapes (context, review, map, localize). The store is seeded
// deterministically and source files are written to disk; the manifest root is omitted so output is stable.
public sealed class V3GoldenOutputTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-v3-golden", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private WorkspaceIndexStore _store = null!;

    public V3GoldenOutputTests() => _databasePath = Path.Combine(_root, ".fuse", "fuse.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Write("src/IOrderService.cs", "namespace App;\npublic interface IOrderService\n{\n    int Create(int quantity);\n}\n");
        Write("src/OrderService.cs", "namespace App;\npublic class OrderService : IOrderService\n{\n    public int Create(int quantity)\n    {\n        return quantity;\n    }\n}\n");
        Write("src/OrdersController.cs", "namespace App;\npublic class OrdersController\n{\n    private readonly IOrderService _orders;\n    public OrdersController(IOrderService orders)\n    {\n        _orders = orders;\n    }\n}\n");

        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await SeedAsync();
    }

    [Fact]
    public async Task ContextEmitIsStable()
    {
        var plan = await Engine().PlanContextAsync(
            new ContextRequest(_root, [new ContextSeed(ContextSeedKind.Symbol, "IOrderService")]), CancellationToken.None);
        var rendered = await Renderer().RenderAsync(plan, _root, CancellationToken.None);
        var output = SemanticContextEmitter.Emit(plan, rendered, ContextOutputFormat.Xml);

        GoldenOutputAssert.AssertMatches("v3-context", output);
    }

    [Fact]
    public async Task ReviewEmitIsStable()
    {
        var engine = new SemanticRetrievalEngine(_store, new StubChangeSource(["src/OrderService.cs"]));
        var plan = await engine.ReviewAsync(new ReviewRequest(_root, "origin/main"), CancellationToken.None);
        var rendered = await Renderer().RenderAsync(plan, _root, CancellationToken.None);
        var output = SemanticContextEmitter.Emit(plan, rendered, ContextOutputFormat.Xml, changedSince: "origin/main");

        GoldenOutputAssert.AssertMatches("v3-review", output);
    }

    [Fact]
    public async Task ReviewWithClaimsEmitIsStable()
    {
        // U2: the review payload carrying a graded-claims block. The block is a deterministic rendered string the
        // tool computes (changed-file count is git-truth/verified; the API-surface delta is graph-grade). Pinning it
        // here fixes the manifest shape (claims after the api-delta, ahead of the seeds).
        var engine = new SemanticRetrievalEngine(_store, new StubChangeSource(["src/OrderService.cs"]));
        var plan = await engine.ReviewAsync(new ReviewRequest(_root, "origin/main"), CancellationToken.None);
        var rendered = await Renderer().RenderAsync(plan, _root, CancellationToken.None);
        var claims = ClaimLedger.Render(
        [
            Claim.FromCompiler("1 changed file(s) are seeded as must-keep", "git diff origin/main"),
            Claim.FromGraph("the change alters the public API surface (see the api-delta section)", "graph: public-API delta (T2)"),
        ]);
        var output = SemanticContextEmitter.Emit(
            plan, rendered, ContextOutputFormat.Xml, changedSince: "origin/main", claimsSection: claims);

        GoldenOutputAssert.AssertMatches("v3-review-claims", output);
    }

    [Fact]
    public async Task MapIsStable()
    {
        var output = await new WorkspaceMapRenderer(_store, _store).RenderAsync(MapDetail.All, 200, CancellationToken.None);

        GoldenOutputAssert.AssertMatches("v3-map", output);
    }

    [Fact]
    public async Task LocalizeIsStable()
    {
        var result = await Engine().LocalizeAsync(new LocalizationRequest(_root, Query: "order service"), CancellationToken.None);

        var builder = new StringBuilder();
        builder.AppendLine($"localize: {result.Candidates.Count} candidates");
        foreach (var candidate in result.Candidates)
            builder.AppendLine($"  {candidate.Score:F3}  {candidate.Path}");

        GoldenOutputAssert.AssertMatches("v3-localize", builder.ToString());
    }

    private SemanticRetrievalEngine Engine() => new(_store);

    private static SemanticContextRenderer Renderer()
    {
        var pipeline = new ContentReductionPipeline(
            new CapabilityRegistry<IContentReducer>([]),
            new CapabilityRegistry<ISkeletonExtractor>([new RoslynSkeletonExtractor()]),
            new CapabilityRegistry<ISemanticMarkerGenerator>([]),
            new LengthTokenCounter(),
            new DefaultSecretRedactor());
        return new SemanticContextRenderer(pipeline, new SourceContentProvider(new PhysicalFileSystem()));
    }

    private async Task SeedAsync()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/IOrderService.cs", "src/IOrderService.cs", ".cs", 60, 1, "h1"),
                new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 120, 1, "h2"),
                new IndexedFileRecord("src/OrdersController.cs", "src/OrdersController.cs", ".cs", 160, 1, "h3"),
            ],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [
                new ChunkRecord("chunk:IOrderService", "src/IOrderService.cs", "interface", "App.IOrderService", 2, 5, "t1", 20, 12, Name: "IOrderService", Body: "order service contract"),
                new ChunkRecord("chunk:OrderService", "src/OrderService.cs", "type", "App.OrderService", 2, 8, "t2", 40, 24, Name: "OrderService", Body: "order service implementation"),
                new ChunkRecord("chunk:OrdersController", "src/OrdersController.cs", "type", "App.OrdersController", 2, 8, "t3", 50, 30, Name: "OrdersController", Body: "orders controller"),
            ],
            CancellationToken.None);
        await _store.UpsertNodesAsync(
            [
                new NodeRecord("type:App.IOrderService", "interface", "IOrderService", "App.IOrderService", "src/IOrderService.cs"),
                new NodeRecord("type:App.OrderService", "class", "OrderService", "App.OrderService", "src/OrderService.cs"),
                new NodeRecord("type:App.OrdersController", "class", "OrdersController", "App.OrdersController", "src/OrdersController.cs"),
            ],
            CancellationToken.None);
        await _store.UpsertEdgesAsync(
            [
                new SemanticEdgeRecord("type:App.IOrderService", "type:App.OrderService", "di_resolves_to", 0.95, 0.95),
                new SemanticEdgeRecord("type:App.OrdersController", "type:App.IOrderService", "di_injects", 0.75, 1.0),
            ],
            CancellationToken.None);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
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

    private sealed class LengthTokenCounter : ITokenCounter
    {
        public int Count(string content) => (content.Length + 3) / 4;
    }

    private sealed class StubChangeSource(IReadOnlyList<string> changed) : IChangeSource
    {
        public Task<IReadOnlyList<string>> GetChangedFilesAsync(string rootDirectory, string since, CancellationToken cancellationToken) =>
            Task.FromResult(changed);

        public Task<IReadOnlyList<ChangedFile>> GetDiffsAsync(string rootDirectory, string since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChangedFile>>(changed.Select(p => new ChangedFile(p, 1, 0, string.Empty)).ToList());
    }
}
