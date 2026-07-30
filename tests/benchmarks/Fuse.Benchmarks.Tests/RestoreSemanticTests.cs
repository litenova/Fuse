using Fuse.Benchmarks;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// R0: restoring a checkout's packages must lift it from the syntax fallback to a semantic load. These
// tests prove the CorpusManager.RestoreAsync capability end to end on a hermetic, offline SDK project.
public sealed class RestoreSemanticTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task RestoreAsync_produces_project_assets_for_a_clean_sdk_project()
    {
        var dir = NewProject();
        try
        {
            var manager = new CorpusManager(NewTempDir(), NewTempDir());
            var restore = await manager.RestoreAsync(dir, Ct);

            Assert.True(restore.Ok, $"expected restore to succeed: {restore.Summary}");
            Assert.True(restore.RestoredProjects >= 1, $"expected at least one restored project: {restore.Summary}");
            Assert.True(File.Exists(Path.Combine(dir, "obj", "project.assets.json")),
                "restore should write obj/project.assets.json");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task Restored_project_indexes_semantically()
    {
        var dir = NewProject();
        try
        {
            var manager = new CorpusManager(NewTempDir(), NewTempDir());
            var restore = await manager.RestoreAsync(dir, Ct);
            Assert.True(restore.Ok, $"expected restore to succeed: {restore.Summary}");

            var databasePath = Path.Combine(NewTempDir(), "fuse.db");
            await using var store = new WorkspaceIndexStore(databasePath);
            await store.InitializeAsync(Ct);
            var result = await CreateIndexer().IndexAsync(dir, store, Ct);

            // A restored SDK project loads through MSBuild/Roslyn: semantic, or partial when a load warning
            // is present. The point is that it is no longer capped at the syntax fallback.
            Assert.True(result.Mode is "semantic" or "partial",
                $"expected semantic or partial after restore, got {result.Mode}");
            Assert.True(result.SymbolCount > 0);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static string NewProject()
    {
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(dir, "OrderService.cs"),
            "namespace Demo;\n\npublic sealed class OrderService\n{\n    public void Place() { }\n}\n");
        return dir;
    }

    private static SemanticIndexer CreateIndexer()
    {
        var fileSystem = new PhysicalFileSystem();
        var pipeline = new FileCollectionPipeline(
            fileSystem,
            new GitIgnoreParser(fileSystem),
            [new GitIgnoreFilter(), new ExtensionFilter(), new ExcludedDirectoryFilter(), new EmptyFileFilter(), new BinaryFileFilter(fileSystem)]);
        return new SemanticIndexer(
            new DotNetWorkspaceDiscoverer(),
            new RoslynWorkspaceLoader(),
            new WorkspaceFileScanner(pipeline),
            new SemanticSymbolExtractor(),
            new SyntaxSymbolExtractor(),
            new SyntaxRouteExtractor(),
            new FileHashService(),
            SemanticAnalysisRunner.CreateDefault());
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-restore-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
