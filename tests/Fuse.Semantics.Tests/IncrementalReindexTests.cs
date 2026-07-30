using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Semantics.Tests;

// R7: single-file incremental re-index updates only the changed file's rows, leaving other files untouched.
public sealed class IncrementalReindexTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-incremental-tests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), "fuse-incremental-db", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "A.cs"), "namespace Demo; public class Alpha { public void M() {} }");
        File.WriteAllText(Path.Combine(_root, "B.cs"), "namespace Demo; public class Beta { }");
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await CreateIndexer().IndexAsync(_root, _store, CancellationToken.None);
    }

    [Fact]
    public async Task ReindexFile_updates_only_the_changed_file()
    {
        var alphaBefore = await CountSymbolsInFileAsync("A.cs");
        Assert.True(alphaBefore > 0);
        Assert.Equal(0, await CountSymbolNamedAsync("Gamma"));

        // Edit B.cs to add a new type, then incrementally re-index only that file.
        File.WriteAllText(Path.Combine(_root, "B.cs"), "namespace Demo; public class Beta { } public class Gamma { }");
        var reindexed = await CreateIndexer().ReindexFileAsync(_root, "B.cs", _store, CancellationToken.None);

        Assert.True(reindexed > 0);
        // The new type is now indexed, and A.cs is untouched (same symbol rows).
        Assert.True(await CountSymbolNamedAsync("Gamma") > 0);
        Assert.Equal(alphaBefore, await CountSymbolsInFileAsync("A.cs"));
        Assert.True(await CountSymbolNamedAsync("Alpha") > 0);
    }

    private async Task<long> CountSymbolsInFileAsync(string normalizedPath) =>
        await ScalarAsync(
            "SELECT count(*) FROM symbols s JOIN files f ON f.file_id = s.file_id WHERE f.normalized_path = $p;",
            ("$p", normalizedPath));

    private async Task<long> CountSymbolNamedAsync(string name) =>
        await ScalarAsync("SELECT count(*) FROM symbols WHERE name = $n;", ("$n", name));

    private async Task<long> ScalarAsync(string sql, params (string, string)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, paramValue) in parameters)
            command.Parameters.AddWithValue(key, paramValue);
        return await command.ExecuteScalarAsync(CancellationToken.None) is long result ? result : 0;
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

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        foreach (var dir in new[] { _root, Path.GetDirectoryName(_databasePath)! })
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
