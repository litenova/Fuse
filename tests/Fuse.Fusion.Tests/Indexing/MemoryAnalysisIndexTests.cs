using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Fusion.Indexing;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Languages.CSharp.Roslyn;
using Fuse.Reduction.Caching;

namespace Fuse.Fusion.Tests.Indexing;

public sealed class MemoryAnalysisIndexTests : IDisposable
{
    private static readonly CapabilityRegistry<IDependencyExtractor> Extractors =
        new([new RoslynDependencyExtractor()]);

    private static readonly CapabilityRegistry<ITypeNameLocator> TypeLocators =
        new([new RoslynTypeNameLocator()]);

    private readonly string _root;

    public MemoryAnalysisIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-memory-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void MemoryAnalysisIndex_ReusesEntriesFromSharedMemoryStore()
    {
        var key = AnalysisHasher.Key("public class A { }", "tier");
        var analysis = new FileAnalysis(["B"], ["A"], ["A", "M"]);
        var store = new MemoryKeyValueStore();
        var writer = new MemoryAnalysisIndex(store);
        writer.Set(key, analysis);

        var reader = new MemoryAnalysisIndex(store);

        Assert.True(reader.TryGet(key, out var loaded));
        Assert.Equal(["B"], loaded!.ReferencedTypes);
        Assert.Equal(["A"], loaded.DeclaredTypes);
        Assert.Equal(["A", "M"], loaded.DeclaredSymbols);
        Assert.Equal(1, reader.Statistics.Hits);
    }

    [Fact]
    public void MemoryAnalysisIndex_MissRecordsMiss()
    {
        var index = new MemoryAnalysisIndex(new MemoryKeyValueStore());

        Assert.False(index.TryGet("deadbeef", out _));
        Assert.Equal(1, index.Statistics.Misses);
    }

    [Fact]
    public async Task GraphBuild_SecondRunHitsMemoryIndex()
    {
        File.WriteAllText(Path.Combine(_root, "A.cs"), "class A { void M(B b) { } }");
        File.WriteAllText(Path.Combine(_root, "B.cs"), "class B { }");
        var files = new[] { CreateFile("A.cs"), CreateFile("B.cs") };
        var store = new MemoryKeyValueStore();
        var index = new MemoryAnalysisIndex(store);
        var builder = new DependencyGraphBuilder();

        await builder.BuildAsync(files, new SourceContentProvider(new PhysicalFileSystem()),
            Extractors, TypeLocators, parallelism: 1, cancellationToken: default, index: index);
        var coldMisses = index.Statistics.Misses;

        var warm = new MemoryAnalysisIndex(store);
        var graph = await builder.BuildAsync(files, new SourceContentProvider(new PhysicalFileSystem()),
            Extractors, TypeLocators, parallelism: 1, cancellationToken: default, index: warm);

        Assert.Equal(2, coldMisses);
        Assert.Equal(2, warm.Statistics.Hits);
        Assert.Equal(0, warm.Statistics.Misses);
        Assert.Contains("B", graph.FileReferences["A.cs"]);
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
