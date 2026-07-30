using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Fuse.Semantics.Tests;

// P3.4: end-to-end semantic indexing of a real .csproj - project records, file linkage, index mode.
public sealed class SemanticIndexerTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-semantic-index-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;
    private string _projectRoot = null!;

    public SemanticIndexerTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _projectRoot = FixtureRoot();
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IndexesRealProjectSemanticallyWithLinkedFiles()
    {
        var indexer = CreateIndexer();

        var result = await indexer.IndexAsync(_projectRoot, _store, CancellationToken.None);

        foreach (var diagnostic in result.Diagnostics)
            _output.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");

        Assert.True(result.Mode is "semantic" or "partial", $"expected semantic or partial, got {result.Mode}");
        Assert.True(result.ProjectCount >= 1);
        Assert.True(result.SymbolCount > 0);

        var state = await _store.GetStateAsync(CancellationToken.None);
        Assert.Equal(result.Mode, state.Mode);

        // Files are linked to a project.
        Assert.True(await CountAsync("SELECT count(*) FROM files WHERE project_id IS NOT NULL;") > 0,
            "expected at least one file linked to a project");
        // Symbols use semantic (assembly-qualified) ids, not the syntax fallback form.
        Assert.True(await CountAsync("SELECT count(*) FROM symbols WHERE symbol_id LIKE 'symbol:fallback:%';") == 0,
            "semantic mode should not emit fallback symbol ids");
        Assert.True(await CountAsync("SELECT count(*) FROM symbols WHERE name = 'OrderService';") > 0);
    }

    [Fact]
    public async Task IndexSyntaxFirstAsync_ServesCompletedSyntaxTier()
    {
        // The default syntax pass produces a usable symbol and full-text index without an MSBuild load.
        // Compiler analysis is opt-in, so a completed syntax index has no pending semantic work.
        var indexer = CreateIndexer();

        var result = await indexer.IndexSyntaxFirstAsync(_projectRoot, _store, CancellationToken.None);

        Assert.Equal("syntax", result.Mode);
        Assert.True(result.SymbolCount > 0);
        Assert.Equal("0", await _store.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, CancellationToken.None));
        Assert.Equal("syntax", (await _store.GetStateAsync(CancellationToken.None)).Mode);
    }

    [Fact]
    public async Task UpgradeToSemanticAsync_LandsTheGraph()
    {
        var indexer = CreateIndexer();
        await indexer.IndexSyntaxFirstAsync(_projectRoot, _store, CancellationToken.None);
        Assert.Equal("0", await _store.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, CancellationToken.None));

        var upgraded = await indexer.UpgradeToSemanticAsync(_projectRoot, _store, CancellationToken.None);

        Assert.True(upgraded.Mode is "semantic" or "partial", $"expected semantic or partial, got {upgraded.Mode}");
        Assert.Equal("0", await _store.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, CancellationToken.None));
    }

    [Fact]
    public async Task IndexSyntaxFirstAsync_IsDeterministic_AcrossParallelRuns()
    {
        // Parallel per-file extraction must produce a positionally identical symbol stream across runs.
        var indexer = CreateIndexer();
        await indexer.IndexSyntaxFirstAsync(_projectRoot, _store, CancellationToken.None);
        var first = await SymbolIdSequenceAsync(_databasePath);

        var secondPath = Path.Combine(Path.GetTempPath(), "fuse-semantic-index-tests", Guid.NewGuid().ToString("N"), "fuse.db");
        await using (var secondStore = new WorkspaceIndexStore(secondPath))
        {
            await secondStore.InitializeAsync(CancellationToken.None);
            await indexer.IndexSyntaxFirstAsync(_projectRoot, secondStore, CancellationToken.None);
        }
        var second = await SymbolIdSequenceAsync(secondPath);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    private static async Task<List<string>> SymbolIdSequenceAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT symbol_id FROM symbols ORDER BY rowid;";
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
            ids.Add(reader.GetString(0));
        return ids;
    }

    private SemanticIndexer CreateIndexer()
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
            Fuse.Semantics.Analyzers.SemanticAnalysisRunner.CreateDefault());
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None) is long value ? value : 0;
    }

    private static string FixtureRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "fixtures")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        // Index just the Core project directory (a clean SDK project, no ASP.NET dependency).
        return Path.Combine(dir!, "tests", "fixtures", "SampleShop", "src", "SampleShop.Core");
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        var directory = Path.GetDirectoryName(_databasePath);
        try
        {
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
