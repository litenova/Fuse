using Fuse.Cli.Rpc;
using Fuse.Cli.Mcp;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Reduction;
using Fuse.Reduction.Security;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Fuse.Cli.Tests.Host;

// Served-root binding (R7): every RPC that carries a root must match the daemon's served root. These tests
// parameterize over each root-bound method so a gap on one entry point fails the suite.
public sealed class FuseHostServedRootTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();

    private FuseHostService NewService(string servedRoot)
    {
        var context = new FuseHostRequestContext(
            _provider.GetRequiredService<SemanticIndexer>(),
            _provider.GetRequiredService<IChangeSource>(),
            _provider.GetRequiredService<ContentReductionPipeline>(),
            _provider.GetRequiredService<ISecretRedactor>(),
            _provider.GetRequiredService<IGeneratedCodeDetector>(),
            _provider.GetRequiredService<IndexCoordinator>(),
            _provider.GetRequiredService<IWorkspaceIndexJobManager>());
        return new FuseHostService(context, NullLogger<FuseHostService>.Instance, servedRoot);
    }

    private static string SessionToken(FuseHostService service) => service.Handshake().SessionToken;

    private static string NewFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-served-root", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Widget.cs"), "public class Widget { public void Run() { } }");
        return dir.AsIsolatedRepo();
    }

    // Release the pooled connections to a fixture's store before deleting it, or the open fuse.db handle blocks
    // the recursive delete.
    private static void CleanupFixture(string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(
            new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={Fuse.Reduction.Caching.FuseStorePaths.ResolveDatabasePath(root)}"));
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<string> RootBoundMethods => new()
    {
        "indexStart",
        "indexStatus",
        "indexCancel",
        "graph",
        "scope",
        "explain",
        "diagnostics",
        "check",
        "checkOverlay",
    };

    [Theory]
    [MemberData(nameof(RootBoundMethods))]
    public async Task RootBoundMethod_RejectsMismatchedServedRoot(string method)
    {
        var served = NewFixture();
        var other = NewFixture();
        try
        {
            var service = NewService(served);
            var token = SessionToken(service);

            var ex = await Assert.ThrowsAnyAsync<LocalRpcException>(() => InvokeRootBoundAsync(service, token, method, other));

            Assert.Contains("served root", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal((int)JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        }
        finally
        {
            CleanupFixture(served);
            CleanupFixture(other);
        }
    }

    [Theory]
    [MemberData(nameof(RootBoundMethods))]
    public async Task RootBoundMethod_AcceptsMatchingServedRoot(string method)
    {
        var served = NewFixture();
        try
        {
            var service = NewService(served);
            var token = SessionToken(service);

            await InvokeRootBoundAsync(service, token, method, served);
        }
        finally
        {
            CleanupFixture(served);
        }
    }

    private static async Task InvokeRootBoundAsync(FuseHostService service, string token, string method, string root)
    {
        switch (method)
        {
            case "indexStart":
                await service.IndexStartAsync(token, root);
                break;
            case "indexStatus":
                _ = service.IndexStatus(token, root);
                break;
            case "indexCancel":
                await service.IndexCancelAsync(token, root);
                break;
            case "graph":
                await service.GraphAsync(token, root, "Files");
                break;
            case "scope":
                await service.ScopeAsync(token, root, "search", null, "widget", null, 20000);
                break;
            case "explain":
                await service.ExplainAsync(token, root, "search", null, "widget", null);
                break;
            case "diagnostics":
                await service.DiagnosticsAsync(token, root);
                break;
            case "check":
                await service.CheckDeltaAsync(token, root, "session-1");
                break;
            case "checkOverlay":
                await service.CheckOverlayAsync(token, root, "Widget.cs", "public class Widget { }", includeAnalyzers: false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown root-bound RPC method.");
        }
    }

    public void Dispose()
    {
        _provider.Dispose();
    }
}
