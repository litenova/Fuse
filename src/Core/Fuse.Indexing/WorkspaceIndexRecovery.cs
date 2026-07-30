using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Fuse.Indexing;

/// <summary>
///     Serialized delete-and-recreate recovery for a corrupt <c>fuse.db</c> (R21). The index is derived data;
///     a malformed file is deleted and rebuilt rather than failing the run.
/// </summary>
internal static class WorkspaceIndexRecovery
{
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADb = 26;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns whether <paramref name="exception" /> indicates a corrupt or non-database file.</summary>
    internal static bool IsCorrupt(SqliteException exception) =>
        exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADb;

    /// <summary>
    ///     Runs <paramref name="work" /> under a per-database-path lock so concurrent opens do not race recovery.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="databasePath">The absolute path to <c>fuse.db</c>.</param>
    /// <param name="work">The work to run while holding the lock.</param>
    /// <returns>The work result.</returns>
    internal static async Task<T> SerializeAsync<T>(string databasePath, Func<Task<T>> work)
    {
        var key = Path.GetFullPath(databasePath);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     Deletes the database file and its WAL sidecar files after clearing only the owning connection pool.
    /// </summary>
    /// <param name="connectionFactory">The factory that owns the database connection pool.</param>
    internal static void DeleteDatabaseFiles(WorkspaceIndexConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        connectionFactory.ClearPool();
        var databasePath = connectionFactory.DatabasePath;
        foreach (var path in EnumerateDatabaseFiles(databasePath))
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        connectionFactory.ClearPool();
    }

    /// <summary>
    ///     Removes one incompatible index and the exact obsolete derived files that shared its directory.
    /// </summary>
    /// <param name="connectionFactory">The factory owning the incompatible database pool.</param>
    /// <remarks>
    ///     Configuration, ignore files, and capture bundles are not derived index data and are never candidates for
    ///     this operation. Every file name is explicit so an upgrade cannot remove an unrelated file from `.fuse`.
    /// </remarks>
    internal static void DeleteIncompatibleDerivedFiles(WorkspaceIndexConnectionFactory connectionFactory)
    {
        DeleteDatabaseFiles(connectionFactory);

        var directory = Path.GetDirectoryName(connectionFactory.DatabasePath)!;
        foreach (var fileName in new[]
                 {
                     "fuse-cache.db",
                     "fuse-cache.db-wal",
                     "fuse-cache.db-shm",
                     "fuse-cache.db-journal",
                     "r60-semantics.json",
                 })
        {
            DeleteFileIfPresent(Path.Combine(directory, fileName));
        }
    }

    private static IEnumerable<string> EnumerateDatabaseFiles(string databasePath)
    {
        yield return databasePath;
        yield return databasePath + "-wal";
        yield return databasePath + "-shm";
        yield return databasePath + "-journal";
    }

    private static void DeleteFileIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
