using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Reads the repository's derived-index files and persisted job state without starting an index job.
/// </summary>
/// <remarks>
///     Every path this returns is a documented Fuse-derived file, so <c>fuse index clean</c> can delete exactly
///     what Fuse wrote and nothing else. Sizes are read from the filesystem, never by opening the database.
/// </remarks>
internal static class IndexStorageReader
{
    /// <summary>Gets the known derived files that cleanup may delete.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Only documented Fuse-derived file paths.</returns>
    internal static IReadOnlyList<string> DerivedFiles(string root)
    {
        var database = FuseStorePaths.ResolveDatabasePath(root);
        var directory = Path.GetDirectoryName(database)!;
        return
        [
            database,
            database + "-wal",
            database + "-shm",
            database + "-journal",
            // Obsolete sidecars from earlier versions: the reduction cache is in memory since 4.4 and the R60
            // semantics dump is gone, so cleanup removes them rather than leaving orphans behind.
            Path.Combine(directory, "fuse-cache.db"),
            Path.Combine(directory, "fuse-cache.db-wal"),
            Path.Combine(directory, "fuse-cache.db-shm"),
            Path.Combine(directory, "fuse-cache.db-journal"),
            Path.Combine(directory, "r60-semantics.json"),
        ];
    }

    /// <summary>Reads derived-index file sizes without creating a SQLite connection.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The current storage snapshot, or an empty snapshot when the directory cannot be read.</returns>
    internal static IndexStorageSnapshot Read(string root)
    {
        try
        {
            var database = FuseStorePaths.ResolveDatabasePath(root);
            var directory = Path.GetDirectoryName(database)!;
            return new IndexStorageSnapshot(
                SizeOf(database),
                SizeOf(database + "-wal"),
                SizeOf(database + "-shm"),
                Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Sum(SizeOf)
                    : 0);
        }
        catch (IOException)
        {
            return IndexStorageSnapshot.Empty;
        }
    }

    /// <summary>Reads index metadata and counts without starting or mutating an index job.</summary>
    /// <param name="root">The canonical repository root.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>
    ///     The persisted index summary, or <see cref="IndexStoreStatus.NotIndexed" /> when no readable database
    ///     exists.
    /// </returns>
    internal static async Task<IndexStoreStatus> ReadStoreAsync(string root, CancellationToken cancellationToken)
    {
        var database = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(database))
            return IndexStoreStatus.NotIndexed;

        try
        {
            await using var store = new WorkspaceIndexStore(database);
            if (await store.OpenForReadAsync(cancellationToken) is not WorkspaceIndexReadOpenStatus.Ready)
                return new IndexStoreStatus("unavailable", null, "schema_or_contract_mismatch", 0, 0, 0, 0, null, null);

            var state = await store.GetStateAsync(cancellationToken);
            var manifest = await WorkspaceIndexManifest.ValidateAsync(root, store, cancellationToken);
            var routes = await store.GetRouteCountAsync(cancellationToken);
            var completedAt = await store.GetMetaAsync(WorkspaceIndexManifest.CompletedUtcMetaKey, cancellationToken);
            return new IndexStoreStatus(
                state.Status.ToString().ToLowerInvariant(),
                state.Mode,
                manifest.Ready ? "ready" : manifest.Detail,
                state.FileCount,
                state.SymbolCount,
                state.ChunkCount,
                routes,
                completedAt,
                await ReadLastFailureAsync(store, cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            return new IndexStoreStatus("unavailable", null, "unreadable", 0, 0, 0, 0, null, null);
        }
    }

    private static async Task<string?> ReadLastFailureAsync(
        WorkspaceIndexStore store,
        CancellationToken cancellationToken)
    {
        var jobState = await store.GetMetaAsync(IndexJobMetaKeys.State, cancellationToken);
        if (!string.Equals(jobState, "failed", StringComparison.Ordinal))
            return null;

        var errorCode = await store.GetMetaAsync(IndexJobMetaKeys.ErrorCode, cancellationToken);
        var errorMessage = await store.GetMetaAsync(IndexJobMetaKeys.ErrorMessage, cancellationToken);
        return string.IsNullOrWhiteSpace(errorCode) ? errorMessage : $"{errorCode}: {errorMessage}";
    }

    private static long SizeOf(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
