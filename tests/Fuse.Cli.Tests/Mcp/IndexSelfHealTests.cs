using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Microsoft.Data.Sqlite;

namespace Fuse.Cli.Tests.Mcp;

// R21: corrupt or version-mismatched fuse.db self-heals and surfaces index_rebuilding: to MCP callers.
public sealed class IndexSelfHealTests : IDisposable
{
    private readonly string _root;
    private readonly string _databasePath;
    private readonly IndexCoordinator _coordinator = new();

    public IndexSelfHealTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-self-heal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        _databasePath = FuseStorePaths.ResolveDatabasePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
    }

    [Fact]
    public async Task CorruptDatabase_RecreatesSchema_OnInitialize()
    {
        await File.WriteAllTextAsync(_databasePath, "not a sqlite database");

        await using var store = new WorkspaceIndexStore(_databasePath);
        var outcome = await store.InitializeAsync(CancellationToken.None);

        Assert.True(outcome.RebuiltEmptyStore);
        Assert.Contains("corrupt", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await store.OpenForReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExtractionContractMismatch_ReturnsIndexRebuildingPrefix_FromCoordinator()
    {
        // R22: an older extraction-contract stamp forces a rebuild (the product version no longer does).
        await SeedPopulatedAsync(stampedExtractionVersion: "0");

        var ex = await Assert.ThrowsAsync<IndexRebuildingException>(() =>
            _coordinator.OpenForReadOnlyAsync(_root, CancellationToken.None));

        var message = FuseOperationalErrors.FromException(ex);
        Assert.StartsWith(FuseOperationalErrors.IndexRebuildingPrefix, message);
        Assert.Contains("extraction contract", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DifferentProductVersion_SameExtractionContract_IsReused_NoRebuild()
    {
        // R22: a minor/patch product bump (different fuse_version) with the current schema+extraction version must
        // reuse the good index, not discard it. Auto-update is default-on, so this is the common upgrade path.
        await SeedPopulatedAsync(stampedVersion: "999999.0.0");

        await using var store = await _coordinator.OpenForReadOnlyAsync(_root, CancellationToken.None);
        var marker = await store.GetMetaAsync("marker", CancellationToken.None);
        Assert.Equal("keep", marker); // the pre-existing data survived: no rebuild.
    }

    [Fact]
    public async Task CorruptDatabase_SubsequentInitialize_SucceedsAfterRecovery()
    {
        await File.WriteAllTextAsync(_databasePath, "not a sqlite database");

        await using (var first = new WorkspaceIndexStore(_databasePath))
        {
            var outcome = await first.InitializeAsync(CancellationToken.None);
            Assert.True(outcome.RebuiltEmptyStore);
        }

        await using var second = new WorkspaceIndexStore(_databasePath);
        var secondOutcome = await second.InitializeAsync(CancellationToken.None);
        Assert.False(secondOutcome.RebuiltEmptyStore);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await second.OpenForReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentRecovery_OnCorruptDatabase_IsSerialized()
    {
        await File.WriteAllTextAsync(_databasePath, "not a sqlite database");

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                await using var store = new WorkspaceIndexStore(_databasePath);
                return await store.InitializeAsync(CancellationToken.None);
            }))
            .ToArray();

        var outcomes = await Task.WhenAll(tasks);
        Assert.Contains(outcomes, o => o.RebuiltEmptyStore);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await new WorkspaceIndexStore(_databasePath).OpenForReadAsync(CancellationToken.None));
    }

    [Fact]
    public void SearchIndexUnavailable_MapsToIndexRebuilding_NotInternalError()
    {
        var message = FuseOperationalErrors.FromException(
            new SearchIndexUnavailableException("the full-text search index is missing; the index is rebuilding."));

        Assert.StartsWith(FuseOperationalErrors.IndexRebuildingPrefix, message);
        Assert.DoesNotContain(FuseOperationalErrors.InternalErrorPrefix, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawNoSuchTableSqlite_MapsToIndexRebuilding_NotInternalError()
    {
        await using (var store = new WorkspaceIndexStore(_databasePath))
            await store.InitializeAsync(CancellationToken.None);

        // The raw error the reproduction hit: a search against a store missing its FTS table. It must never
        // surface as internal_error: SQLite Error - the operational boundary maps it to index_rebuilding:.
        SqliteException raw;
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM chunk_fts_missing;";
            raw = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteReaderAsync());
        }

        var message = FuseOperationalErrors.FromException(raw);
        Assert.StartsWith(FuseOperationalErrors.IndexRebuildingPrefix, message);
        Assert.DoesNotContain("SQLite Error", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComputeIndexState_SymbolsButZeroChunks_NotReady()
    {
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);
        const string path = "src/Widget.cs";
        await store.UpsertFilesAsync(
        [
            new IndexedFileRecord(path, path, ".cs", 10, 0, "hash-w", Language: "csharp"),
        ], CancellationToken.None);
        await store.UpsertSymbolsAsync(
        [
            new SymbolRecord("sym-w", path, "type", "Widget", "Shop.Widget", IsPublicApi: true),
        ], CancellationToken.None);
        // Deliberately no chunks: an FTS-available store with symbols and zero chunks is internally inconsistent.

        var state = await store.GetStateAsync(CancellationToken.None);
        Assert.True(state.FtsAvailable);
        Assert.True(state.SymbolCount > 0);
        Assert.Equal(0, state.ChunkCount);

        var indexState = await IndexAvailabilityReporter.ComputeIndexStateAsync(store, state, _root, CancellationToken.None);
        Assert.NotEqual("ready", indexState);
        Assert.Equal("index_rebuilding", indexState);
    }

    [Fact]
    public async Task ComputeIndexState_UnknownMode_NotReady()
    {
        // R31: a populated, searchable store whose index_mode was never stamped fails the integrity check and is
        // never reported ready, even though it has chunks.
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);
        const string path = "src/Gadget.cs";
        await store.UpsertFilesAsync(
        [
            new IndexedFileRecord(path, path, ".cs", 10, 0, "hash-g", Language: "csharp"),
        ], CancellationToken.None);
        await store.UpsertSymbolsAsync(
        [
            new SymbolRecord("sym-g", path, "type", "Gadget", "Shop.Gadget", IsPublicApi: true),
        ], CancellationToken.None);
        await store.UpsertChunksAsync(
        [
            new ChunkRecord("chunk-g", path, "type", "stable-g", 1, 5, "th-g", 10, 5, SymbolId: "sym-g", Name: "Gadget", Body: "class Gadget {}", SymbolsText: "Gadget"),
        ], CancellationToken.None);
        // Deliberately do not set index_mode: the store is searchable but its mode is unknown.

        var state = await store.GetStateAsync(CancellationToken.None);
        Assert.True(state.ChunkCount > 0);
        Assert.True(string.IsNullOrWhiteSpace(state.Mode));

        var indexState = await IndexAvailabilityReporter.ComputeIndexStateAsync(store, state, _root, CancellationToken.None);
        Assert.Equal("index_rebuilding", indexState);
    }

    private async Task SeedPopulatedAsync(string? stampedVersion = null, string? stampedExtractionVersion = null)
    {
        await using (var seed = new WorkspaceIndexStore(_databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            // A file so the store is non-empty (a rebuilt empty store would also lose the marker meta).
            await seed.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/Seed.cs", "src/Seed.cs", ".cs", 10, 0, "hash-seed", Language: "csharp"),
            ], CancellationToken.None);
            await seed.SetMetaAsync("marker", "keep", CancellationToken.None);
            if (stampedVersion is not null)
                await seed.SetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, stampedVersion, CancellationToken.None);
            if (stampedExtractionVersion is not null)
                await seed.SetMetaAsync(WorkspaceIndexStore.ExtractionVersionMetaKey, stampedExtractionVersion, CancellationToken.None);
        }

    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
