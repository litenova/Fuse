using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Fuse.Semantics.Tests;

// P3.4: syntax-first and explicit semantic indexing of a real .csproj.
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
    public async Task IndexAsync_defaults_to_syntax_without_compiler_facts()
    {
        var indexer = CreateIndexer();

        var result = await indexer.IndexAsync(_projectRoot, _store, CancellationToken.None);

        foreach (var diagnostic in result.Diagnostics)
            _output.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");

        Assert.Equal("syntax", result.Mode);
        Assert.Equal(0, result.ProjectCount);
        Assert.True(result.SymbolCount > 0);

        var state = await _store.GetStateAsync(CancellationToken.None);
        Assert.Equal(result.Mode, state.Mode);

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM files WHERE project_id IS NOT NULL;"));
        Assert.True(await CountAsync("SELECT count(*) FROM symbols WHERE symbol_id LIKE 'symbol:fallback:%';") > 0,
            "syntax mode should emit fallback symbol ids");
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
    public async Task IndexSyntaxFirstAsync_DoesNotRequireAnUnambiguousCompilerWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-semantic-index-tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, ".fuse", "fuse.db");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Widget.cs"),
            "namespace Demo; public sealed class Widget { }");
        // Discovery correctly refuses this repository for compiler work. The syntax pass must not call it.
        await File.WriteAllTextAsync(Path.Combine(root, "Product.slnf"), "{ \"name\": \"product\" }");
        await File.WriteAllTextAsync(Path.Combine(root, "Tests.slnf"), "{ \"name\": \"tests\" }");

        try
        {
            await Assert.ThrowsAsync<WorkspaceConfigurationException>(
                () => new DotNetWorkspaceDiscoverer().DiscoverAsync(root, CancellationToken.None));

            await using (var store = new WorkspaceIndexStore(databasePath))
            {
                await store.InitializeAsync(CancellationToken.None);

                var result = await CreateIndexer().IndexSyntaxFirstAsync(root, store, CancellationToken.None);

                Assert.Equal("syntax", result.Mode);
                Assert.True((await WorkspaceIndexManifest.ValidateAsync(root, store, CancellationToken.None)).Ready);
                var diagnosis = await store.GetMetaAsync(WorkspaceIndexStore.LoadDiagnosisMetaKey, CancellationToken.None);
                Assert.Contains("\"tier\":\"syntax\"", diagnosis, StringComparison.Ordinal);
                Assert.DoesNotContain("Product.slnf", diagnosis, StringComparison.Ordinal);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of this test's exact temporary database directory.
            }
        }
    }

    [Fact]
    public async Task IndexSyntaxFirstAsync_Reports_inventory_and_incremental_syntax_progress()
    {
        var indexer = CreateIndexer();
        var progress = new ProgressCapture();

        await indexer.IndexSyntaxFirstAsync(_projectRoot, _store, CancellationToken.None, progress);

        var updates = progress.Updates;
        Assert.Contains(updates, update => update.Stage == SemanticIndexStage.Inventory && update.TotalUnits is > 0);
        Assert.Contains(updates, update => update.Stage == SemanticIndexStage.SyntaxExtraction);
        Assert.Contains(updates, update => update.Stage == SemanticIndexStage.SyntaxPersistence);
        var extraction = updates.Where(update => update.Stage == SemanticIndexStage.SyntaxExtraction).ToList();
        Assert.All(extraction, update =>
        {
            if (update.TotalUnits is { } total)
                Assert.InRange(update.CompletedUnits, 0, total);
        });
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

    private sealed class ProgressCapture : IProgress<SemanticIndexProgress>
    {
        private readonly List<SemanticIndexProgress> _updates = [];

        public IReadOnlyList<SemanticIndexProgress> Updates
        {
            get
            {
                lock (_updates)
                    return _updates.ToArray();
            }
        }

        public void Report(SemanticIndexProgress value)
        {
            lock (_updates)
                _updates.Add(value);
        }
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
