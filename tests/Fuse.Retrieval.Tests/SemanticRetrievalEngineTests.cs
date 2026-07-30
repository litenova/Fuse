using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// P5.4: localization results and context-plan output over a seeded semantic index.
public sealed class SemanticRetrievalEngineTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-engine-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await SeedAsync();
    }

    [Fact]
    public async Task LocalizeReturnsRankedCandidatesWithoutBodies()
    {
        var engine = new SemanticRetrievalEngine(_store);

        var result = await engine.LocalizeAsync(new LocalizationRequest(".", Query: "OrderService"), CancellationToken.None);

        Assert.NotEmpty(result.Candidates);
        Assert.Contains(result.Candidates, c => c.Path == "src/OrderService.cs");
        Assert.All(result.Candidates, c => Assert.True(c.Score > 0));
    }

    [Fact]
    public async Task ConfidentResultReturnsTightSetWithNoNavigationMap()
    {
        var engine = new SemanticRetrievalEngine(_store);

        // An exact symbol anchor resolves to one clear winner: confident, tight set, no navigation map.
        var result = await engine.LocalizeAsync(new LocalizationRequest(".", Focus: "OrderService"), CancellationToken.None);

        Assert.Equal(SignalState.Confident, result.State);
        Assert.Contains(result.Candidates, c => c.Path == "src/OrderService.cs");
        Assert.Null(result.Navigation);
        Assert.False(result.LowSignal);
    }

    [Fact]
    public async Task NoMatchIsInsufficientAndHandsBackANavigationMap()
    {
        var engine = new SemanticRetrievalEngine(_store);

        // Nothing matches "zzznotfound": the score distribution is empty, so the state is insufficient. The R3
        // LowSignal flag stays false (the query is not a no-signal title); the engine still hands back a map.
        var result = await engine.LocalizeAsync(new LocalizationRequest(".", Query: "zzznotfound"), CancellationToken.None);

        Assert.Equal(SignalState.Insufficient, result.State);
        Assert.False(result.LowSignal);
        Assert.NotNull(result.Navigation);
        // Refuse-and-route, never just refuse: the map carries the structure Fuse did see.
        Assert.NotEmpty(result.Navigation!.CandidateAreas);
        Assert.False(string.IsNullOrWhiteSpace(result.Navigation.Ask));
    }

    [Fact]
    public async Task NoSignalTitleRefusesAndRoutesWithACandidateMap()
    {
        var engine = new SemanticRetrievalEngine(_store);

        // A merge-noise title that happens to share words with indexed files must abstain (R3) and route, not
        // return junk: insufficient state, LowSignal set, empty candidate list, but a populated navigation map.
        var result = await engine.LocalizeAsync(
            new LocalizationRequest(".", Query: "Merge pull request #42 from acme/order-service"), CancellationToken.None);

        Assert.True(result.LowSignal);
        Assert.Equal(SignalState.Insufficient, result.State);
        Assert.Empty(result.Candidates);
        Assert.False(string.IsNullOrWhiteSpace(result.SuggestedInput));
        Assert.NotNull(result.Navigation);
        Assert.NotEmpty(result.Navigation!.CandidateAreas);
    }

    [Fact]
    public async Task StrictModeRefusesAnUnanchoredQueryButAnswersAnAnchoredOne()
    {
        var engine = new SemanticRetrievalEngine(_store);

        // Unanchored (no match) under strict: refused, no candidate list, only the navigation map.
        var refused = await engine.LocalizeAsync(
            new LocalizationRequest(".", Query: "zzznotfound", Strict: true), CancellationToken.None);
        Assert.Equal(SignalState.Insufficient, refused.State);
        Assert.Empty(refused.Candidates);
        Assert.NotNull(refused.Navigation);

        // Anchored (an exact symbol) under strict: answered with the tight set.
        var answered = await engine.LocalizeAsync(
            new LocalizationRequest(".", Focus: "OrderService", Strict: true), CancellationToken.None);
        Assert.Equal(SignalState.Confident, answered.State);
        Assert.NotEmpty(answered.Candidates);
    }

    [Fact]
    public async Task DefaultModeNeverReturnsNothingForAClientThatCannotRefine()
    {
        var engine = new SemanticRetrievalEngine(_store);

        // The default (non-strict) contract always hands back something actionable: even when no candidate clears
        // the bar, the navigation map (areas, nearest symbols, an ask) is present so a one-shot client is not stranded.
        var result = await engine.LocalizeAsync(new LocalizationRequest(".", Query: "zzznotfound"), CancellationToken.None);

        Assert.NotNull(result.Navigation);
        Assert.False(string.IsNullOrWhiteSpace(result.Navigation!.Ask));
        Assert.True(result.Navigation.CandidateAreas.Count > 0 || result.Navigation.NearestSymbols.Count > 0);
    }

    [Fact]
    public async Task ContextPlanIncludesInterfaceImplementationAndConsumer()
    {
        var engine = new SemanticRetrievalEngine(_store);
        var request = new ContextRequest(".", [new ContextSeed(ContextSeedKind.Symbol, "IOrderService")]);

        var plan = await engine.PlanContextAsync(request, CancellationToken.None);

        var paths = plan.Items.Select(i => i.Path).ToHashSet();
        Assert.Contains("src/IOrderService.cs", paths);    // the seed interface
        Assert.Contains("src/OrderService.cs", paths);     // di_resolves_to implementation
        Assert.Contains("src/OrdersController.cs", paths);  // di_injects consumer (incoming)
        Assert.Contains(plan.Items, i => i.Role == "exact-seed" && i.MustKeep);
        Assert.Contains(plan.Items, i => i.Role == "di-implementation");
    }

    // Indexing is syntax-first, so the typed graph is absent until compiler analysis is requested. A named seed
    // must still plan the declaring file rather than returning an empty payload with no explanation.
    [Fact]
    public async Task NamedSeedWithoutATypedGraphPlansTheDeclaringFile()
    {
        var syntaxDatabase = Path.Combine(
            Path.GetTempPath(), "fuse-engine-tests", Guid.NewGuid().ToString("N"), "fuse.db");
        await using var syntaxStore = new WorkspaceIndexStore(syntaxDatabase);
        await syntaxStore.InitializeAsync(CancellationToken.None);
        await syntaxStore.UpsertFilesAsync(
            [new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 120, 0, "h1")],
            CancellationToken.None);
        await syntaxStore.UpsertSymbolsAsync(
            [
                new SymbolRecord(
                    SymbolId: "sym:OrderService",
                    FilePath: "src/OrderService.cs",
                    Kind: "class",
                    Name: "OrderService",
                    FullyQualifiedName: "Shop.OrderService",
                    StartLine: 1,
                    EndLine: 10,
                    IsPublicApi: true),
            ],
            CancellationToken.None);

        var plan = await new SemanticRetrievalEngine(syntaxStore).PlanContextAsync(
            new ContextRequest(".", [new ContextSeed(ContextSeedKind.Symbol, "OrderService")]),
            CancellationToken.None);

        Assert.Contains("src/OrderService.cs", plan.Items.Select(item => item.Path));

        try
        {
            Directory.Delete(Path.GetDirectoryName(syntaxDatabase)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task FileSeedExpandsFromTheFilesSymbols()
    {
        var engine = new SemanticRetrievalEngine(_store);
        // Seeding the file that declares IOrderService should expand to its implementation, like a named seed.
        var request = new ContextRequest(".", [new ContextSeed(ContextSeedKind.File, "src/IOrderService.cs")]);

        var plan = await engine.PlanContextAsync(request, CancellationToken.None);

        var paths = plan.Items.Select(i => i.Path).ToHashSet();
        Assert.Contains("src/IOrderService.cs", paths);
        Assert.Contains("src/OrderService.cs", paths);
    }

    [Fact]
    public async Task ContextPlanRespectsTokenBudget()
    {
        var engine = new SemanticRetrievalEngine(_store);
        var request = new ContextRequest(".", [new ContextSeed(ContextSeedKind.Symbol, "IOrderService")], MaxTokens: 1);

        var plan = await engine.PlanContextAsync(request, CancellationToken.None);

        // Must-keep seed is always included; optional files past the 1-token budget are dropped with a warning.
        Assert.Contains(plan.Items, i => i.MustKeep);
        Assert.Contains(plan.Warnings, w => w.Contains("budget", StringComparison.Ordinal));
    }

    private async Task SeedAsync()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/IOrderService.cs", "src/IOrderService.cs", ".cs", 10, 1, "h1"),
                new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 20, 1, "h2"),
                new IndexedFileRecord("src/OrdersController.cs", "src/OrdersController.cs", ".cs", 30, 1, "h3"),
            ],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [
                new ChunkRecord("chunk:IOrderService", "src/IOrderService.cs", "interface", "k1", 1, 5, "t1", 12, 8, Name: "IOrderService"),
                new ChunkRecord("chunk:OrderService", "src/OrderService.cs", "type", "k2", 1, 20, "t2", 60, 40, Name: "OrderService", Body: "order service"),
                new ChunkRecord("chunk:OrdersController", "src/OrdersController.cs", "type", "k3", 1, 30, "t3", 80, 50, Name: "OrdersController"),
            ],
            CancellationToken.None);
        await _store.UpsertSymbolsAsync(
            [
                new SymbolRecord("sym:IOrderService", "src/IOrderService.cs", "interface", "IOrderService", "App.IOrderService", Namespace: "App", IsPublicApi: true),
                new SymbolRecord("sym:OrderService", "src/OrderService.cs", "class", "OrderService", "App.OrderService", Namespace: "App", IsPublicApi: true),
                new SymbolRecord("sym:OrdersController", "src/OrdersController.cs", "class", "OrdersController", "App.OrdersController", Namespace: "App", IsPublicApi: true),
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
