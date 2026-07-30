using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Fusion.Indexing;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Languages.CSharp.Roslyn;
using Fuse.Reduction.Caching;

namespace Fuse.Fusion.Tests.Indexing;

/// <summary>
///     Verifies that malformed bytes in the <c>analysis</c> memory namespace are treated as cache misses and rebuilt.
/// </summary>
public sealed class MemoryAnalysisIndexMalformedEntryTests : IDisposable
{
    private static readonly CapabilityRegistry<IDependencyExtractor> Extractors =
        new([new RoslynDependencyExtractor()]);

    private static readonly CapabilityRegistry<ITypeNameLocator> TypeLocators =
        new([new RoslynTypeNameLocator()]);

    private static readonly string AnalysisTier =
        typeof(RoslynDependencyExtractor).FullName + "|" + typeof(RoslynTypeNameLocator).FullName;

    private readonly string _root;

    public MemoryAnalysisIndexMalformedEntryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-malformed-memory-analysis-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task GraphBuild_MalformedAnalysisEntry_MissesAndReindexes()
    {
        const string source = "class Alpha { void M(Beta b) { } }";
        var malformed = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        File.WriteAllText(Path.Combine(_root, "Alpha.cs"), source);
        File.WriteAllText(Path.Combine(_root, "Beta.cs"), "class Beta { }");
        var files = new[] { CreateFile("Alpha.cs"), CreateFile("Beta.cs") };
        var contentProvider = new SourceContentProvider(new PhysicalFileSystem());
        var builder = new DependencyGraphBuilder();
        var analysisKey = AnalysisHasher.Key(source, AnalysisTier);
        var store = new MemoryKeyValueStore();
        store.Set("analysis", analysisKey, malformed);

        var coldIndex = new MemoryAnalysisIndex(store);
        var graph = await builder.BuildAsync(
            files,
            contentProvider,
            Extractors,
            TypeLocators,
            parallelism: 1,
            cancellationToken: default,
            index: coldIndex);

        Assert.Equal(2, coldIndex.Statistics.Misses);
        Assert.Equal(0, coldIndex.Statistics.Hits);
        Assert.Contains("Beta", graph.FileReferences["Alpha.cs"]);
        Assert.True(store.TryGet("analysis", analysisKey, out var rebuilt));
        Assert.NotNull(rebuilt);
        Assert.False(rebuilt.SequenceEqual(malformed));
    }

    private SourceFile CreateFile(string relativePath)
    {
        var fullPath = Path.Combine(_root, relativePath);
        return new SourceFile(new FileCandidate(fullPath, relativePath, new FileInfo(fullPath)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
