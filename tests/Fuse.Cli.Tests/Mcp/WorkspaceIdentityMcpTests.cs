using Fuse.Cli.Extensions;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Tests.Mcp;

[Collection("FuseToolsResidentProvider")]
public sealed class WorkspaceIdentityMcpTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
    private readonly List<string> _roots = [];
    private IndexJobClient Jobs => new(_services.GetRequiredService<IWorkspaceIndexJobManager>(), daemonEnabled: false);

    [Fact]
    public async Task IndexedToolRefusesFolderWithoutRepositoryIdentity()
    {
        var root = CreateTempDirectory(markAsRepository: false);

        var result = await FuseTools.FuseWorkspaceAsync(
            _services.GetRequiredService<SemanticIndexer>(),
            Jobs,
            action: "status",
            path: root);

        Assert.StartsWith(FuseOperationalErrors.WorkspaceIdentityUnresolvedPrefix, result);
        Assert.Contains("native file tools", result, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, ".fuse")));
    }

    [Fact]
    public async Task PackageImpactRefusesFolderWithoutRepositoryIdentity()
    {
        var root = CreateTempDirectory(markAsRepository: false);

        var result = await FuseTools.FuseImpactAsync(
            _services.GetRequiredService<SemanticIndexer>(),
            path: root,
            package: "Example.Package",
            fromVersion: "1.0.0",
            toVersion: "2.0.0");

        Assert.StartsWith(FuseOperationalErrors.WorkspaceIdentityUnresolvedPrefix, result);
        Assert.False(Directory.Exists(Path.Combine(root, ".fuse")));
    }

    [Fact]
    public async Task NonEmptyIndexWithoutManifestRebuildsFromRepositoryRoot()
    {
        var root = CreateTempDirectory(markAsRepository: true);
        await File.WriteAllTextAsync(Path.Combine(root, "A.cs"), "public class Alpha { }");
        await File.WriteAllTextAsync(Path.Combine(root, "B.cs"), "public class Beta { }");
        var nested = Path.Combine(root, "tests", "Sample.Tests", "bin", "Release", "net10.0");
        Directory.CreateDirectory(nested);

        var databasePath = FuseStorePaths.ResolveDatabasePath(nested);
        await using (var seed = new WorkspaceIndexStore(databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.UpsertFilesAsync(
                [new IndexedFileRecord("fuse.runtimeconfig.json", "fuse.runtimeconfig.json", ".json", 10, 1, "stale")],
                CancellationToken.None);
            await seed.SetMetaAsync("index_mode", "syntax", CancellationToken.None);
        }

        var started = await FuseTools.FuseWorkspaceAsync(
            _services.GetRequiredService<SemanticIndexer>(),
            Jobs,
            action: "index",
            path: nested);
        Assert.Contains("index job:", started, StringComparison.Ordinal);

        var completed = await _services.GetRequiredService<IWorkspaceIndexJobManager>()
            .WaitForCompletionAsync(root, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(IndexJobState.Completed, completed!.State);

        var result = await FuseTools.FuseWorkspaceAsync(
            _services.GetRequiredService<SemanticIndexer>(),
            Jobs,
            action: "status",
            path: nested);
        Assert.Contains($"workspace: {root}", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("files indexed: 2", result, StringComparison.Ordinal);
        Assert.Contains("index_state: ready", result, StringComparison.Ordinal);

        await using var store = new WorkspaceIndexStore(FuseStorePaths.ResolveDatabasePath(root));
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await store.OpenForReadAsync(CancellationToken.None));
        var hashes = await store.GetAllFileHashesAsync(CancellationToken.None);
        Assert.Equal(["A.cs", "B.cs"], hashes.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.True((await WorkspaceIndexManifest.ValidateAsync(root, store, CancellationToken.None)).Ready);
    }

    [Fact]
    public async Task CoveringTestSelectionWarmsColdRepositoryBeforeReadingIndex()
    {
        var root = CreateTempDirectory(markAsRepository: true);
        await File.WriteAllTextAsync(Path.Combine(root, "OrderService.cs"), "public class OrderService { }");

        var result = await FuseTools.FuseTestAsync(
            _services.GetRequiredService<SemanticIndexer>(),
            symbol: "OrderService",
            path: root);

        Assert.Contains("covering tests for OrderService: none", result, StringComparison.Ordinal);
        await using var store = new WorkspaceIndexStore(FuseStorePaths.ResolveDatabasePath(root));
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await store.OpenForReadAsync(CancellationToken.None));
        Assert.True((await WorkspaceIndexManifest.ValidateAsync(root, store, CancellationToken.None)).Ready);
    }

    private string CreateTempDirectory(bool markAsRepository)
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-workspace-identity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (markAsRepository)
            Directory.CreateDirectory(Path.Combine(root, ".git"));
        _roots.Add(root);
        return root;
    }

    public void Dispose()
    {
        _services.Dispose();
        foreach (var root in _roots)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
