using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Markers;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Skeleton;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Reduction.Security;
using Fuse.Reduction.Tokenization;

namespace Fuse.Reduction.Tests.Caching;

public sealed class ContentReductionPipelineCacheTests
{
    [Fact]
    public async Task ReduceAsync_SecondRun_UsesCacheHits()
    {
        var root = CreateTempRoot();
        var filePath = Path.Combine(root, "sample.txt");
        await File.WriteAllTextAsync(filePath, "  hello world  ");

        try
        {
            var (pipeline, sourceFile, provider, options) = CreatePipelineContext(filePath);
            await using var store = new MemoryStoreFactory().Open(root);
            var cache = new MemoryReductionCache(store);

            await pipeline.ReduceAsync([sourceFile], options, provider, 1, cache);
            await pipeline.ReduceAsync([sourceFile], options, provider, 1, cache);

            Assert.Equal(1, cache.Statistics.Hits);
            Assert.Equal(1, cache.Statistics.Misses);
            Assert.False(File.Exists(Path.Combine(root, ".fuse", "fuse-cache.db")));
            Assert.False(File.Exists(Path.Combine(root, ".fuse", "fuse.db")));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ReduceAsync_AfterReopen_UsesSharedHostMemoryCache()
    {
        var root = CreateTempRoot();
        var filePath = Path.Combine(root, "sample.txt");
        await File.WriteAllTextAsync(filePath, "  hello world  ");

        try
        {
            var (pipeline, sourceFile, provider, options) = CreatePipelineContext(filePath);

            var factory = new MemoryStoreFactory();
            await using (var store = factory.Open(root))
            {
                var cache = new MemoryReductionCache(store);
                await pipeline.ReduceAsync([sourceFile], options, provider, 1, cache);
                Assert.Equal(0, cache.Statistics.Hits);
                Assert.Equal(1, cache.Statistics.Misses);
            }

            await using (var store = factory.Open(root))
            {
                var cache = new MemoryReductionCache(store);
                await pipeline.ReduceAsync([sourceFile], options, provider, 1, cache);
                Assert.Equal(1, cache.Statistics.Hits);
                Assert.Equal(0, cache.Statistics.Misses);
            }
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-pipeline-cache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static (
        ContentReductionPipeline Pipeline,
        SourceFile SourceFile,
        StubContentProvider Provider,
        ReductionOptions Options) CreatePipelineContext(string filePath)
    {
        var candidate = new FileCandidate(filePath, "sample.txt", new FileInfo(filePath));
        var sourceFile = new SourceFile(candidate);
        var provider = new StubContentProvider(sourceFile, "  hello world  ");
        var reducers = new CapabilityRegistry<IContentReducer>(Array.Empty<IContentReducer>());
        var skeletons = new CapabilityRegistry<ISkeletonExtractor>(Array.Empty<ISkeletonExtractor>());
        var markers = new CapabilityRegistry<ISemanticMarkerGenerator>(Array.Empty<ISemanticMarkerGenerator>());
        var pipeline = new ContentReductionPipeline(
            reducers,
            skeletons,
            markers,
            new SimpleTokenCounter(),
            new DefaultSecretRedactor());
        var options = new ReductionOptions(trimContent: true, enableRedaction: false);
        return (pipeline, sourceFile, provider, options);
    }

    private sealed class StubContentProvider : ISourceContentProvider
    {
        private readonly SourceFile _file;
        private readonly string _content;

        public StubContentProvider(SourceFile file, string content)
        {
            _file = file;
            _content = content;
        }

        public Task<string> GetContentAsync(SourceFile file, CancellationToken cancellationToken = default) =>
            Task.FromResult(_content);

        public void Clear()
        {
        }
    }

    private sealed class SimpleTokenCounter : ITokenCounter
    {
        public int Count(string content) => content.Length;
    }
}
