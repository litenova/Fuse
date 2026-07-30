using Fuse.Reduction.Caching;

namespace Fuse.Reduction.Tests.Caching;

public sealed class MemoryKeyValueStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-memory-store-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LeastRecentlyUsedEntryLeavesWhenCapacityIsExceeded()
    {
        var store = new MemoryKeyValueStore(capacityBytes: 4);
        store.Set("cache", "one", [1, 1]);
        store.Set("cache", "two", [2, 2]);
        Assert.True(store.TryGet("cache", "one", out _));

        store.Set("cache", "three", [3, 3]);

        Assert.True(store.TryGet("cache", "one", out _));
        Assert.False(store.TryGet("cache", "two", out _));
        Assert.True(store.TryGet("cache", "three", out _));
        Assert.InRange(store.RetainedBytes, 0, store.CapacityBytes);
    }

    [Fact]
    public void ReadsAndWritesDoNotExposeMutableCacheBuffers()
    {
        var store = new MemoryKeyValueStore();
        var source = new byte[] { 1, 2, 3 };
        store.Set("cache", "entry", source);
        source[0] = 9;

        Assert.True(store.TryGet("cache", "entry", out var first));
        Assert.Equal([1, 2, 3], first);
        first![1] = 8;

        Assert.True(store.TryGet("cache", "entry", out var second));
        Assert.Equal([1, 2, 3], second);
    }

    [Fact]
    public void ClearRemovesOnlyTheRequestedNamespace()
    {
        var store = new MemoryKeyValueStore();
        store.Set("reduction", "entry", [1]);
        store.Set("analysis", "entry", [2]);

        store.Clear("reduction");

        Assert.False(store.TryGet("reduction", "entry", out _));
        Assert.True(store.TryGet("analysis", "entry", out var analysis));
        Assert.Equal([2], analysis);
    }

    [Fact]
    public void FactoryScopesSameLogicalKeyToItsRepository()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateDirectory(Path.Combine(first, ".git"));
        Directory.CreateDirectory(Path.Combine(second, ".git"));
        var factory = new MemoryStoreFactory();

        Assert.Equal(64L * 1024 * 1024, factory.CapacityBytes);

        var firstStore = factory.Open(first);
        var secondStore = factory.Open(second);
        firstStore.Set("reduction", "same", [1]);
        secondStore.Set("reduction", "same", [2]);

        Assert.True(firstStore.TryGet("reduction", "same", out var firstValue));
        Assert.True(secondStore.TryGet("reduction", "same", out var secondValue));
        Assert.Equal([1], firstValue);
        Assert.Equal([2], secondValue);
        Assert.False(File.Exists(Path.Combine(first, ".fuse", "fuse-cache.db")));
        Assert.False(File.Exists(Path.Combine(second, ".fuse", "fuse-cache.db")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
