using Fuse.Cli.Extensions;
using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// R38: the index warms eagerly at serve/daemon start, before the first tool call, so a cold repo does not pay
// the full cold cost on the first read. Default-on with a FUSE_EAGER_INDEX=0 opt-out.
public sealed class EagerIndexTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
    private EagerIndex Eager => _provider.GetRequiredService<EagerIndex>();

    [Fact]
    public void IsEnabled_DefaultsOn_AndOptsOut()
    {
        var original = Environment.GetEnvironmentVariable(EagerIndex.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EagerIndex.EnvVar, null);
            Assert.True(EagerIndex.IsEnabled());
            Environment.SetEnvironmentVariable(EagerIndex.EnvVar, "0");
            Assert.False(EagerIndex.IsEnabled());
            Environment.SetEnvironmentVariable(EagerIndex.EnvVar, "off");
            Assert.False(EagerIndex.IsEnabled());
            Environment.SetEnvironmentVariable(EagerIndex.EnvVar, "1");
            Assert.True(EagerIndex.IsEnabled());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EagerIndex.EnvVar, original);
        }
    }

    [Fact]
    public void Start_WhenDisabled_ReturnsNull()
    {
        var original = Environment.GetEnvironmentVariable(EagerIndex.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EagerIndex.EnvVar, "0");
            Assert.Null(Eager.Start(NewRoot()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EagerIndex.EnvVar, original);
        }
    }

    [Fact]
    public async Task WarmAsync_OnColdRepo_PopulatesTheStore_BeforeAnyRead()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git")); // isolate the store under this root.
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "Widget.cs"), "namespace Shop; public class Widget { public int Id { get; set; } }");

        await Eager.WarmAsync(root, CancellationToken.None);

        // The store is warm before any tool call: it has indexed files.
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        await using var store = new WorkspaceIndexStore(databasePath);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await store.OpenForReadAsync(CancellationToken.None));
        var state = await store.GetStateAsync(CancellationToken.None);
        Assert.True(state.FileCount > 0, "eager warm should have indexed the cold repo's files");
    }

    [Fact]
    public async Task WarmAsync_AfterCorruptStoreRecovery_PopulatesACompleteIndex()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(root, "Widget.cs"), "namespace Shop; public class Widget { }");
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await File.WriteAllTextAsync(databasePath, "not a sqlite database");

        await Eager.WarmAsync(root, CancellationToken.None);

        await using var store = new WorkspaceIndexStore(databasePath);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await store.OpenForReadAsync(CancellationToken.None));
        Assert.True((await WorkspaceIndexManifest.ValidateAsync(root, store, CancellationToken.None)).Ready);
        Assert.Equal(1, (await store.GetStateAsync(CancellationToken.None)).FileCount);
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "fuse-eager", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _provider.Dispose();
    }
}
