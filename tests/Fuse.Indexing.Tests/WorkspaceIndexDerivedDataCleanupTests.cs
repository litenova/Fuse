using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

public sealed class WorkspaceIndexDerivedDataCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-derived-cleanup", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Incompatible_index_removes_only_known_derived_files()
    {
        var fuseDirectory = Path.Combine(_root, ".fuse");
        var databasePath = Path.Combine(fuseDirectory, "fuse.db");
        var configPath = Path.Combine(_root, "fuse.json");
        var ignorePath = Path.Combine(_root, ".fuseignore");
        var capturePath = Path.Combine(fuseDirectory, "captures", "capture.json");
        Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
        await File.WriteAllTextAsync(configPath, "{\"ignore\":[]}");
        await File.WriteAllTextAsync(ignorePath, "vendor/");
        await File.WriteAllTextAsync(capturePath, "capture");

        await using (var seed = new WorkspaceIndexStore(databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.SetMetaAsync(WorkspaceIndexStore.ExtractionVersionMetaKey, "3", CancellationToken.None);
            await seed.SetMetaAsync("obsolete_marker", "discard", CancellationToken.None);
        }

        var obsoletePaths = new[]
        {
            Path.Combine(fuseDirectory, "fuse-cache.db"),
            Path.Combine(fuseDirectory, "fuse-cache.db-wal"),
            Path.Combine(fuseDirectory, "fuse-cache.db-shm"),
            Path.Combine(fuseDirectory, "fuse-cache.db-journal"),
            Path.Combine(fuseDirectory, "r60-semantics.json"),
        };
        foreach (var obsoletePath in obsoletePaths)
            await File.WriteAllTextAsync(obsoletePath, "obsolete");

        await using var store = new WorkspaceIndexStore(databasePath);
        var outcome = await store.InitializeAsync(CancellationToken.None);

        Assert.True(outcome.RebuiltEmptyStore);
        Assert.Contains("extraction contract", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.GetMetaAsync("obsolete_marker", CancellationToken.None));
        Assert.All(obsoletePaths, path => Assert.False(File.Exists(path), $"Expected obsolete derived file to be removed: {path}"));
        Assert.True(File.Exists(configPath));
        Assert.True(File.Exists(ignorePath));
        Assert.True(File.Exists(capturePath));
        Assert.Equal(2, await ReadAutoVacuumModeAsync(databasePath));
    }

    private static async Task<long> ReadAutoVacuumModeAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA auto_vacuum;";
        var result = await command.ExecuteScalarAsync(CancellationToken.None);
        return result is long mode ? mode : -1;
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
