using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Fuse.Indexing;

/// <summary>
///     Brings an index database to <see cref="WorkspaceIndexSchema.TargetVersion" /> and applies
///     schema-level pragmas, table creation, and <c>index_meta</c> reads and writes.
/// </summary>
/// <remarks>
///     V3 has no backward-compatibility requirement, so there is no incremental migration: if the
///     on-disk version is below the target (the existing cache database carries a lower or absent
///     version), every Fuse-owned table is dropped and the schema is rebuilt. Dropping is driven off
///     <c>sqlite_master</c> so a stale table from an earlier schema (for example the old <c>kv</c>
///     cache table) is removed too, not just the tables this version knows by name.
/// </remarks>
internal sealed class IndexSchemaMigrator
{
    private readonly ILogger? _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="IndexSchemaMigrator" /> class.
    /// </summary>
    /// <param name="connectionFactory">The connection factory for the index database.</param>
    /// <param name="logger">An optional logger for migration diagnostics.</param>
    public IndexSchemaMigrator(WorkspaceIndexConnectionFactory connectionFactory, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _logger = logger;
    }

    /// <summary>
    ///     Reads the current schema version and, if it is below the target, drops all Fuse-owned
    ///     tables and rebuilds the schema. A database already at the target version is left untouched.
    /// </summary>
    /// <param name="connection">An open connection to the index database.</param>
    /// <param name="cancellationToken">A token to cancel the migration.</param>
    /// <returns>The schema version in effect after migration.</returns>
    public static async Task<int> MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await EnsureVersionTableAsync(connection, cancellationToken);
        var currentVersion = await ReadVersionAsync(connection, cancellationToken);
        if (currentVersion >= WorkspaceIndexSchema.TargetVersion)
            return currentVersion;

        await RebuildAsync(connection, cancellationToken);
        return WorkspaceIndexSchema.TargetVersion;
    }

    /// <summary>
    ///     Drops every Fuse-owned table and recreates the schema at <see cref="WorkspaceIndexSchema.TargetVersion" />.
    /// </summary>
    /// <param name="connection">An open connection to the index database.</param>
    /// <param name="cancellationToken">A token to cancel the rebuild.</param>
    /// <returns>A task that completes when the schema has been rebuilt.</returns>
    /// <remarks>
    ///     Used both by <see cref="MigrateAsync" /> on a schema-version bump and by the store when the index was
    ///     written by an incompatible Fuse build (see <see cref="FuseBuildInfo.IsCompatible" />). The rebuild
    ///     empties the index; the caller re-indexes to repopulate it.
    /// </remarks>
    public static async Task RebuildAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // SQLite ignores PRAGMA foreign_keys changes inside an active transaction. Disable enforcement before the
        // rebuild transaction so legacy tables with references in an arbitrary sqlite_master order can be removed
        // as one owned derived-data unit, then restore enforcement before this connection is returned to the pool.
        await ExecuteAsync(connection, null, "PRAGMA foreign_keys = OFF;", cancellationToken);
        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await DropAllObjectsAsync(connection, transaction, cancellationToken);
            await ExecuteAsync(connection, transaction, WorkspaceIndexSchema.CreateTablesDdl, cancellationToken);
            await SetVersionAsync(connection, transaction, WorkspaceIndexSchema.TargetVersion, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await ExecuteAsync(connection, null, "PRAGMA foreign_keys = ON;", CancellationToken.None);
        }
    }

    /// <summary>
    ///     Applies database-level pragmas and ensures the version table exists.
    /// </summary>
    /// <param name="connection">An open connection to the index database.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when pragmas and the version table are in place.</returns>
    public async Task PrepareDatabaseAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ApplyDatabasePragmasAsync(connection, cancellationToken);
        await EnsureVersionTableForReadAsync(connection, cancellationToken);
    }

    /// <summary>
    ///     Ensures all relational tables exist (idempotent <c>IF NOT EXISTS</c> DDL).
    /// </summary>
    /// <param name="connection">An open connection to the index database.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when tables are ensured.</returns>
    public static async Task EnsureTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = WorkspaceIndexSchema.CreateTablesDdl;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Reads the current schema version from <c>schema_version</c>.
    /// </summary>
    /// <param name="connection">An open connection to the index database.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The highest recorded schema version, or 0 when absent.</returns>
    public static async Task<int> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT version FROM schema_version ORDER BY version DESC LIMIT 1;";
        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is long version ? (int)version : 0;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>
    ///     Reads a value from <c>index_meta</c>.
    /// </summary>
    /// <param name="connection">An open connection to the index database.</param>
    /// <param name="key">The meta key.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The stored value, or <see langword="null" /> when absent.</returns>
    public static async Task<string?> ReadMetaAsync(
        SqliteConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM index_meta WHERE key = $k LIMIT 1;";
        command.Parameters.AddWithValue("$k", key);
        try
        {
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Writes a value to <c>index_meta</c>.
    /// </summary>
    /// <param name="connection">An open connection to the index database.</param>
    /// <param name="key">The meta key.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the row is upserted.</returns>
    public static async Task WriteMetaAsync(
        SqliteConnection connection, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO index_meta(key, value) VALUES($k, $v) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Counts rows in a table for state reporting.
    /// </summary>
    /// <param name="connection">An open connection to the index database.</param>
    /// <param name="table">The table name.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The row count, or 0 when the table is absent.</returns>
    public static async Task<int> CountAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table};";
        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is long count ? (int)count : 0;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>
    ///     Whether a table or virtual table of the given name exists in the database.
    /// </summary>
    /// <param name="connection">An open connection to the index database.</param>
    /// <param name="table">The table name to probe.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns><see langword="true" /> when a table with that name exists.</returns>
    /// <remarks>
    ///     Used to make FTS availability a single source of truth (R23): the <c>fts_available</c> meta stamp and
    ///     the presence of <c>chunk_fts</c> can disagree after a partial rebuild, so callers verify the table
    ///     actually exists rather than trusting the stamp, and never issue a search that throws
    ///     <c>no such table: chunk_fts</c>.
    /// </remarks>
    public static async Task<bool> TableExistsAsync(
        SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type IN ('table','view') AND name = $n LIMIT 1;";
        command.Parameters.AddWithValue("$n", table);
        try
        {
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task EnsureVersionTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureVersionTableForReadAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await EnsureVersionTableAsync(connection, cancellationToken);
    }

    private static async Task DropAllObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var objects = new List<(string Type, string Name, bool IsVirtual)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT type, name, sql FROM sqlite_master " +
                "WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' AND name <> 'schema_version';";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var sql = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var isVirtual = sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase);
                objects.Add((reader.GetString(0), reader.GetString(1), isVirtual));
            }
        }

        foreach (var (type, name, _) in objects.OrderByDescending(o => o.IsVirtual))
        {
            var quoted = name.Replace("\"", "\"\"", StringComparison.Ordinal);
            await ExecuteAsync(connection, transaction, $"DROP {type.ToUpperInvariant()} IF EXISTS \"{quoted}\";", cancellationToken);
        }

        await ExecuteAsync(connection, transaction, "DELETE FROM schema_version;", cancellationToken);
    }

    private static async Task SetVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO schema_version(version) VALUES($v);";
        command.Parameters.AddWithValue("$v", version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyDatabasePragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = WorkspaceIndexSchema.CreatePragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
