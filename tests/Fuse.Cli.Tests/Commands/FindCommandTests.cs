using System.Runtime.CompilerServices;
using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Commands;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Xunit;

namespace Fuse.Cli.Tests.Commands;

/// <summary>
///     Verifies the CLI surface retained when the separate localize and resolve commands were removed.
/// </summary>
public sealed class FindCommandTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-find-command", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _root.AsIsolatedRepo();
        var sourceDirectory = Path.Combine(_root, "src");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "IOrderService.cs"), "public interface IOrderService { }");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "OrderService.cs"), "public sealed class OrderService : IOrderService { }");

        var databasePath = FuseStorePaths.ResolveDatabasePath(_root);
        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        await store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/IOrderService.cs", "src/IOrderService.cs", ".cs", 36, 1, "interface-hash"),
                new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 55, 1, "implementation-hash"),
            ],
            CancellationToken.None);
        await store.UpsertNodesAsync(
            [
                new NodeRecord("type:App.IOrderService", "interface", "IOrderService", "App.IOrderService", "src/IOrderService.cs", StartLine: 1),
                new NodeRecord("type:App.OrderService", "type", "OrderService", "App.OrderService", "src/OrderService.cs", StartLine: 1),
            ],
            CancellationToken.None);
        await store.UpsertEdgesAsync(
            [new SemanticEdgeRecord(
                "type:App.IOrderService",
                "type:App.OrderService",
                "di_resolves_to",
                0.95,
                0.95,
                EvidenceFilePath: "src/OrderService.cs")],
            CancellationToken.None);
    }

    [Fact]
    public async Task Service_kind_resolves_wiring_through_the_find_union()
    {
        var console = new CapturingConsoleUI();
        var command = new FindCommand(console)
        {
            Path = _root,
            Query = "IOrderService",
            Kind = "service",
        };

        await command.RunAsync(TestCliContext());

        Assert.Empty(console.Errors);
        Assert.Contains("resolve service: IOrderService", console.Results.ToString());
        Assert.Contains("OrderService", console.Results.ToString());
        Assert.Contains("di_resolves_to", console.Results.ToString());
    }

    [Fact]
    public async Task Neighbors_kind_returns_callers_and_implementers()
    {
        var console = new CapturingConsoleUI();
        var command = new FindCommand(console)
        {
            Path = _root,
            Query = "OrderService",
            Kind = "neighbors",
        };

        await command.RunAsync(TestCliContext());

        Assert.Empty(console.Errors);
        Assert.Contains("neighbors of OrderService: 1", console.Results.ToString());
        Assert.Contains("IOrderService", console.Results.ToString());
    }

    public Task DisposeAsync()
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(_root);
        new WorkspaceIndexConnectionFactory(databasePath).ClearPool();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private static CliContext TestCliContext() =>
        (CliContext)RuntimeHelpers.GetUninitializedObject(typeof(CliContext));

    private sealed class CapturingConsoleUI : IConsoleUI
    {
        public List<string> Errors { get; } = [];
        public StringBuilder Results { get; } = new();

        public void WriteError(string message) => Errors.Add(message);
        public void WriteResult(string message) => Results.AppendLine(message);
        public void WriteStep(string message)
        {
        }

        public void WriteSuccess(string message)
        {
        }
    }
}
