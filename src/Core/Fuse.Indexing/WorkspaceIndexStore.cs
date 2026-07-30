using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Fuse.Indexing;

/// <summary>
///     SQLite-backed implementation of <see cref="IWorkspaceIndexStore" />, stored at
///     <c>.fuse/fuse.db</c> in WAL mode.
/// </summary>
/// <remarks>
///     <see cref="InitializeAsync" /> is the write path: pragmas, <see cref="IndexSchemaMigrator" />
///     (rebuild when the on-disk version is below <see cref="WorkspaceIndexSchema.TargetVersion" />),
///     FTS probe, and <c>index_meta</c> stamps. <see cref="OpenForReadAsync" /> is the warm read path:
///     schema verify only, no meta write, so concurrent foreground reads do not contend with a
///     background upgrade writer. Connections are pooled via <see cref="WorkspaceIndexConnectionFactory" />;
///     the pool is cleared on dispose.
///     Implementation is split across internal ports (<see cref="IndexSchemaMigrator" />,
///     <see cref="FtsSearchEngine" />, <see cref="SymbolGraphStore" />, <see cref="SessionStore" />);
///     this type is the only public entry for host and MCP callers.
/// </remarks>
public sealed class WorkspaceIndexStore : IWorkspaceIndexStore
{
    private readonly WorkspaceIndexConnectionFactory _connectionFactory;
    private readonly IndexSchemaMigrator _schema;
    private readonly FtsSearchEngine _fts;
    private readonly SymbolGraphStore _graph;
    private readonly SessionStore _sessions;
    private readonly ILogger<WorkspaceIndexStore>? _logger;
    private int _schemaVersion;
    private bool _initialized;

    /// <summary>
    ///     Initializes a new instance of the <see cref="WorkspaceIndexStore" /> class.
    /// </summary>
    /// <param name="databasePath">The absolute path to the index database file.</param>
    /// <param name="logger">An optional logger for lifecycle diagnostics.</param>
    /// <param name="busyTimeoutMilliseconds">
    ///     The SQLite <c>busy_timeout</c> for this store's connections. Defaults to the write-path timeout; a
    ///     read-tool open passes a short value so a contended store surfaces <c>index_busy</c> quickly (R18/R20).
    /// </param>
    public WorkspaceIndexStore(
        string databasePath,
        ILogger<WorkspaceIndexStore>? logger = null,
        int busyTimeoutMilliseconds = WorkspaceIndexConnectionFactory.DefaultBusyTimeoutMilliseconds)
    {
        _connectionFactory = new WorkspaceIndexConnectionFactory(databasePath, busyTimeoutMilliseconds);
        _logger = logger;
        _schema = new IndexSchemaMigrator(_connectionFactory, logger);
        _fts = new FtsSearchEngine(_connectionFactory, logger);
        _graph = new SymbolGraphStore(_connectionFactory, _fts);
        _sessions = new SessionStore(_connectionFactory);
    }

    /// <summary>
    ///     The <c>index_meta</c> key under which the indexer stamps the Fuse build that wrote the index, so a
    ///     later run can detect an incompatible upgrade and rebuild.
    /// </summary>
    public const string FuseVersionMetaKey = "fuse_version";

    /// <summary>
    ///     The <c>index_meta</c> key under which the indexer stamps the extraction-contract version
    ///     (<see cref="WorkspaceIndexSchema.ExtractionContractVersion" />). Index reuse is gated on this and the
    ///     schema version, not on the product version, so a minor or patch bump that does not change extraction
    ///     reuses a good index (R22).
    /// </summary>
    public const string ExtractionVersionMetaKey = "index_extraction_version";

    /// <summary>
    ///     The <c>index_meta</c> key under which a full index pass records the last integrity-check result (R31),
    ///     so the recorded health is auditable alongside the live check the read paths run.
    /// </summary>
    public const string IndexIntegrityMetaKey = "index_integrity";

    /// <summary>
    ///     The <c>index_meta</c> key under which an index pass records unreadable or permission-denied files, so
    ///     <c>doctor</c> can surface them.
    /// </summary>
    public const string SkippedFilesMetaKey = "skipped_files";

    /// <summary>
    ///     The <c>index_meta</c> key listing files retained at declarations or inventory-only detail, so status
    ///     and doctor can name the path and reason rather than silently omitting source detail.
    /// </summary>
    public const string DetailLimitedFilesMetaKey = "detail_limited_files";

    /// <summary>
    ///     The <c>index_meta</c> key under which an index pass stamps the per-project semantic-load diagnosis (R43):
    ///     the achieved tier, the selected solution, and each project's load outcome, serialized as JSON. It is
    ///     stamped with the index pass (so it reflects what was actually indexed), letting
    ///     <c>fuse_workspace action=doctor</c> report the load tier and per-project reasons from the warm index in
    ///     sub-second time instead of re-running the full MSBuild/Roslyn load; a live re-load runs only on an
    ///     explicit refresh or when this stamp is absent.
    /// </summary>
    public const string LoadDiagnosisMetaKey = "load_diagnosis";

    /// <summary>
    ///     The <c>index_meta</c> key under which <c>fuse index --from-capture</c> stamps the absolute path to the
    ///     bundle's portable compiler log (C2), so <c>fuse_check</c> can answer oracle-grade from the captured
    ///     compilation without building on a machine that cannot restore or build.
    /// </summary>
    public const string CaptureComplogPathMetaKey = "capture_complog_path";

    /// <summary>The absolute path to the index database file.</summary>
    public string DatabasePath => _connectionFactory.DatabasePath;

    /// <summary>
    ///     Whether full-text search is available. False when the runtime lacks FTS5; searches then
    ///     return no hits until a fallback index is built.
    /// </summary>
    public bool FullTextSearchAvailable => _fts.Available;

    /// <inheritdoc />
    public Task<WorkspaceIndexInitializeOutcome> InitializeAsync(CancellationToken cancellationToken) =>
        WorkspaceIndexRecovery.SerializeAsync(
            _connectionFactory.DatabasePath,
            () => InitializeSerializedAsync(cancellationToken));

    /// <inheritdoc />
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await _schema.PrepareDatabaseAsync(connection, cancellationToken);
        await IndexSchemaMigrator.RebuildAsync(connection, cancellationToken);
        var ftsAvailable = await _fts.TryCreateAsync(connection, cancellationToken);
        await IndexSchemaMigrator.WriteMetaAsync(
            connection,
            IndexAvailability.FtsAvailableMetaKey,
            IndexAvailability.ToFtsMetaValue(ftsAvailable),
            cancellationToken);
        await StampExtractionVersionAsync(connection, cancellationToken);
        MarkInitialized(WorkspaceIndexSchema.TargetVersion, ftsAvailable);
    }

    private async Task<WorkspaceIndexInitializeOutcome> InitializeSerializedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await InitializeCoreAsync(cancellationToken);
        }
        catch (SqliteException ex) when (WorkspaceIndexRecovery.IsCorrupt(ex))
        {
            _logger?.LogWarning(
                ex,
                "Corrupt index at {DatabasePath}; deleting and recreating.",
                _connectionFactory.DatabasePath);
            WorkspaceIndexRecovery.DeleteDatabaseFiles(_connectionFactory);
            Directory.CreateDirectory(Path.GetDirectoryName(_connectionFactory.DatabasePath)!);
            await InitializeCoreAsync(cancellationToken);
            return new WorkspaceIndexInitializeOutcome(true, "corrupt database recovered");
        }
    }

    private async Task<WorkspaceIndexInitializeOutcome> InitializeCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_connectionFactory.DatabasePath)!);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await _schema.PrepareDatabaseAsync(connection, cancellationToken);

        var priorSchemaVersion = await IndexSchemaMigrator.ReadVersionAsync(connection, cancellationToken);
        _schemaVersion = await IndexSchemaMigrator.MigrateAsync(connection, cancellationToken);
        var schemaRebuilt = priorSchemaVersion > 0 && priorSchemaVersion < WorkspaceIndexSchema.TargetVersion;
        await IndexSchemaMigrator.EnsureTablesAsync(connection, cancellationToken);

        // R22: gate index reuse on the extraction-contract version, not the product version. A store carrying a
        // different fuse_version but the current schema and extraction version is reused (a minor or patch bump no
        // longer discards a good index); only an extraction-contract change forces a rebuild. The fuse_version stamp
        // is kept for diagnostics (read here only to detect a pre-R22 store that predates the extraction stamp).
        var storedExtractionRaw = await IndexSchemaMigrator.ReadMetaAsync(connection, ExtractionVersionMetaKey, cancellationToken);
        var storedFuseVersion = await IndexSchemaMigrator.ReadMetaAsync(connection, FuseVersionMetaKey, cancellationToken);
        var versionRebuilt = ExtractionContractChanged(storedExtractionRaw, storedFuseVersion);
        if (versionRebuilt)
        {
            _logger?.LogInformation(
                "Index at {DatabasePath} was built for extraction contract {StoredExtraction}; rebuilding for {CurrentExtraction}.",
                _connectionFactory.DatabasePath,
                storedExtractionRaw ?? "(unset)",
                WorkspaceIndexSchema.ExtractionContractVersion);
            await IndexSchemaMigrator.RebuildAsync(connection, cancellationToken);
            _schemaVersion = WorkspaceIndexSchema.TargetVersion;
        }

        // R23: a version-compatible store can still be internally inconsistent - it has indexed files but the
        // chunk_fts table went missing (a store rebuilt without FTS). Recreating an empty chunk_fts here would
        // leave search silent-empty over real source. Detect the inconsistency and force a full derived-data
        // rebuild so the caller re-indexes and repopulates both chunks and FTS, rather than serving a broken index.
        if (!versionRebuilt)
        {
            var fileCount = await IndexSchemaMigrator.CountAsync(connection, "files", cancellationToken);
            var ftsStamped = IndexAvailability.ParseFtsMeta(
                await IndexSchemaMigrator.ReadMetaAsync(connection, IndexAvailability.FtsAvailableMetaKey, cancellationToken));
            if (fileCount > 0 && ftsStamped
                && !await IndexSchemaMigrator.TableExistsAsync(connection, "chunk_fts", cancellationToken))
            {
                _logger?.LogWarning(
                    "Index at {DatabasePath} has {FileCount} files but chunk_fts is missing; rebuilding derived data.",
                    _connectionFactory.DatabasePath,
                    fileCount);
                await IndexSchemaMigrator.RebuildAsync(connection, cancellationToken);
                var ftsRepaired = await _fts.TryCreateAsync(connection, cancellationToken);
                await IndexSchemaMigrator.WriteMetaAsync(
                    connection,
                    IndexAvailability.FtsAvailableMetaKey,
                    IndexAvailability.ToFtsMetaValue(ftsRepaired),
                    cancellationToken);
                await StampExtractionVersionAsync(connection, cancellationToken);
                MarkInitialized(_schemaVersion, ftsRepaired);
                return new WorkspaceIndexInitializeOutcome(true, "search index missing; rebuilding derived data");
            }
        }

        // R23: probe FTS and stamp availability on every init path, including the version-mismatch and
        // schema-migration rebuilds. The earlier version-mismatch path returned before this, so a rebuilt store
        // was left without chunk_fts and with a stale fts_available stamp; the next search then threw
        // "no such table: chunk_fts". Flowing every rebuild through the probe keeps the rebuilt store searchable.
        var ftsAvailable = await _fts.TryCreateAsync(connection, cancellationToken);
        await IndexSchemaMigrator.WriteMetaAsync(
            connection,
            IndexAvailability.FtsAvailableMetaKey,
            IndexAvailability.ToFtsMetaValue(ftsAvailable),
            cancellationToken);
        await StampExtractionVersionAsync(connection, cancellationToken);
        MarkInitialized(_schemaVersion, ftsAvailable);
        _logger?.LogDebug(
            "Workspace index initialized at {DatabasePath} (schema v{SchemaVersion}, fts={FtsAvailable}).",
            _connectionFactory.DatabasePath,
            _schemaVersion,
            ftsAvailable);

        if (versionRebuilt)
            return new WorkspaceIndexInitializeOutcome(
                true, $"after upgrade to extraction contract v{WorkspaceIndexSchema.ExtractionContractVersion}");

        if (schemaRebuilt)
            return new WorkspaceIndexInitializeOutcome(true, "schema migration");

        return WorkspaceIndexInitializeOutcome.Normal;
    }

    // R22: index reuse is gated on the extraction-contract version and the schema version, never the product
    // version. A parseable stored version rebuilds only when it differs from the current contract; an unset stamp
    // over a populated store (a pre-R22 index carrying only fuse_version) rebuilds once to gain the stamp, while a
    // fresh empty store (neither stamp set) is not treated as a mismatch.
    private static bool ExtractionContractChanged(string? storedExtractionRaw, string? storedFuseVersion)
    {
        if (int.TryParse(storedExtractionRaw, out var stored))
            return stored != WorkspaceIndexSchema.ExtractionContractVersion;
        return !string.IsNullOrWhiteSpace(storedFuseVersion);
    }

    private static Task StampExtractionVersionAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        IndexSchemaMigrator.WriteMetaAsync(
            connection,
            ExtractionVersionMetaKey,
            WorkspaceIndexSchema.ExtractionContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);

    /// <inheritdoc />
    public async Task<WorkspaceIndexReadOpenStatus> OpenForReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_connectionFactory.DatabasePath))
            return WorkspaceIndexReadOpenStatus.DatabaseMissing;

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
            await _schema.PrepareDatabaseAsync(connection, cancellationToken);

            var version = await IndexSchemaMigrator.ReadVersionAsync(connection, cancellationToken);
            if (version != WorkspaceIndexSchema.TargetVersion)
                return WorkspaceIndexReadOpenStatus.SchemaMismatch;

            // R22: reuse is gated on the extraction-contract version, not the product version. A store built by a
            // different fuse_version but the current extraction contract opens ready (a minor bump reuses the index).
            var storedExtractionRaw = await IndexSchemaMigrator.ReadMetaAsync(connection, ExtractionVersionMetaKey, cancellationToken);
            var storedFuseVersion = await IndexSchemaMigrator.ReadMetaAsync(connection, FuseVersionMetaKey, cancellationToken);
            if (ExtractionContractChanged(storedExtractionRaw, storedFuseVersion))
                return WorkspaceIndexReadOpenStatus.IncompatibleVersion;

            var ftsStamped = IndexAvailability.ParseFtsMeta(
                await IndexSchemaMigrator.ReadMetaAsync(connection, IndexAvailability.FtsAvailableMetaKey, cancellationToken));
            // R23: FTS availability is a single source of truth. If the stamp says available but chunk_fts is
            // absent (a store rebuilt without the FTS table), do not open ready: report a mismatch so the
            // coordinator write-initializes and recreates chunk_fts, rather than serving a search that throws.
            if (ftsStamped && !await IndexSchemaMigrator.TableExistsAsync(connection, "chunk_fts", cancellationToken))
                return WorkspaceIndexReadOpenStatus.SchemaMismatch;

            var ftsAvailable = ftsStamped;
            MarkInitialized(version, ftsAvailable);
            _logger?.LogDebug(
                "Workspace index opened read-only at {DatabasePath} (schema v{SchemaVersion}, fts={FtsAvailable}).",
                _connectionFactory.DatabasePath,
                _schemaVersion,
                ftsAvailable);
            return WorkspaceIndexReadOpenStatus.Ready;
        }
        catch (SqliteException ex) when (WorkspaceIndexRecovery.IsCorrupt(ex))
        {
            return WorkspaceIndexReadOpenStatus.SchemaMismatch;
        }
    }

    private void MarkInitialized(int schemaVersion, bool ftsAvailable)
    {
        _schemaVersion = schemaVersion;
        _fts.MarkAvailable(ftsAvailable);
        _initialized = true;
    }

    /// <inheritdoc />
    public async Task<WorkspaceIndexState> GetStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var version = _initialized ? _schemaVersion : await IndexSchemaMigrator.ReadVersionAsync(connection, cancellationToken);
        var fileCount = await IndexSchemaMigrator.CountAsync(connection, "files", cancellationToken);
        var symbolCount = await IndexSchemaMigrator.CountAsync(connection, "symbols", cancellationToken);
        var chunkCount = await IndexSchemaMigrator.CountAsync(connection, "chunks", cancellationToken);
        var status = fileCount == 0 ? WorkspaceIndexStatus.Cold : WorkspaceIndexStatus.Warm;
        var mode = await IndexSchemaMigrator.ReadMetaAsync(connection, "index_mode", cancellationToken);
        var ftsStamped = _initialized
            ? _fts.Available
            : IndexAvailability.ParseFtsMeta(
                await IndexSchemaMigrator.ReadMetaAsync(connection, IndexAvailability.FtsAvailableMetaKey, cancellationToken));
        // R23: reconcile the FTS stamp with the actual chunk_fts table so the status line and body never disagree.
        // A stamp of "available" over a store missing chunk_fts is reported unavailable, matching what a search sees.
        var ftsAvailable = ftsStamped
            && await IndexSchemaMigrator.TableExistsAsync(connection, "chunk_fts", cancellationToken);
        return new WorkspaceIndexState(version, status, fileCount, symbolCount, mode, ftsAvailable, chunkCount);
    }

    /// <inheritdoc />
    public async Task SetMetaAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await IndexSchemaMigrator.WriteMetaAsync(connection, key, value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        return await IndexSchemaMigrator.ReadMetaAsync(connection, key, cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveCheckSessionBaselineAsync(
        string sessionId, string root, IReadOnlyList<CheckDiagnostic> baseline, CancellationToken cancellationToken) =>
        _sessions.SaveCheckSessionBaselineAsync(sessionId, root, baseline, cancellationToken);

    /// <inheritdoc />
    public Task<CheckSessionBaseline?> GetCheckSessionBaselineAsync(string sessionId, CancellationToken cancellationToken) =>
        _sessions.GetCheckSessionBaselineAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public Task SaveClaimLedgerAsync(string sessionId, string root, string claimsJson, CancellationToken cancellationToken) =>
        _sessions.SaveClaimLedgerAsync(sessionId, root, claimsJson, cancellationToken);

    /// <inheritdoc />
    public Task<ClaimLedgerRecord?> GetClaimLedgerAsync(string sessionId, CancellationToken cancellationToken) =>
        _sessions.GetClaimLedgerAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string root, CancellationToken cancellationToken) =>
        _sessions.ListSessionsAsync(root, cancellationToken);

    /// <inheritdoc />
    public Task UpsertFilesAsync(IReadOnlyList<IndexedFileRecord> files, CancellationToken cancellationToken) =>
        _graph.UpsertFilesAsync(files, cancellationToken);

    /// <inheritdoc />
    public Task UpsertProjectsAsync(IReadOnlyList<ProjectRecord> projects, CancellationToken cancellationToken) =>
        _graph.UpsertProjectsAsync(projects, cancellationToken);

    /// <inheritdoc />
    public Task ReplaceTfmAvailabilityAsync(IReadOnlyList<TfmAvailabilityRecord> availability, CancellationToken cancellationToken) =>
        _graph.ReplaceTfmAvailabilityAsync(availability, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TfmAvailabilityRecord>> GetTfmAvailabilityAsync(CancellationToken cancellationToken) =>
        _graph.GetTfmAvailabilityAsync(cancellationToken);

    /// <inheritdoc />
    public Task UpsertNodesAsync(IReadOnlyList<NodeRecord> nodes, CancellationToken cancellationToken) =>
        _graph.UpsertNodesAsync(nodes, cancellationToken);

    /// <inheritdoc />
    public Task UpsertSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken cancellationToken) =>
        _graph.UpsertSymbolsAsync(symbols, cancellationToken);

    /// <inheritdoc />
    public Task UpsertChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken) =>
        _graph.UpsertChunksAsync(chunks, cancellationToken);

    /// <inheritdoc />
    public Task UpsertEdgesAsync(IReadOnlyList<SemanticEdgeRecord> edges, CancellationToken cancellationToken) =>
        _graph.UpsertEdgesAsync(edges, cancellationToken);

    /// <inheritdoc />
    public Task UpsertRoutesAsync(IReadOnlyList<RouteRecord> routes, CancellationToken cancellationToken) =>
        _graph.UpsertRoutesAsync(routes, cancellationToken);

    /// <inheritdoc />
    public Task UpsertDiRegistrationsAsync(IReadOnlyList<DiRegistrationRecord> registrations, CancellationToken cancellationToken) =>
        _graph.UpsertDiRegistrationsAsync(registrations, cancellationToken);

    /// <inheritdoc />
    public Task UpsertOptionsBindingsAsync(IReadOnlyList<OptionsBindingRecord> bindings, CancellationToken cancellationToken) =>
        _graph.UpsertOptionsBindingsAsync(bindings, cancellationToken);

    /// <inheritdoc />
    public Task DeleteFileDataAsync(string normalizedPath, CancellationToken cancellationToken) =>
        _graph.DeleteFileDataAsync(normalizedPath, cancellationToken);

    /// <inheritdoc />
    public Task ClearFileDataAsync(IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken) =>
        _graph.ClearFileDataAsync(normalizedPaths, cancellationToken);

    /// <inheritdoc />
    public Task DeleteFileAsync(string normalizedPath, CancellationToken cancellationToken) =>
        _graph.DeleteFileAsync(normalizedPath, cancellationToken);

    /// <inheritdoc />
    public Task<int> PruneFilesAsync(IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken) =>
        _graph.PruneFilesAsync(normalizedPaths, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
        _fts.SearchAsync(query, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SymbolListItem>> ListSymbolsAsync(int limit, CancellationToken cancellationToken) =>
        _graph.ListSymbolsAsync(limit, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SymbolListItem>> FindSymbolsByNameAsync(string nameFragment, int limit, CancellationToken cancellationToken) =>
        _graph.FindSymbolsByNameAsync(nameFragment, limit, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SymbolSignature>> GetSignaturesByNamesAsync(
        IReadOnlyCollection<string> names, int limitPerName, CancellationToken cancellationToken) =>
        _graph.GetSignaturesByNamesAsync(names, limitPerName, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SymbolSignature>> GetMembersOfTypeAsync(
        string typeName, int limit, CancellationToken cancellationToken) =>
        _graph.GetMembersOfTypeAsync(typeName, limit, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<RouteListItem>> ListRoutesAsync(int limit, CancellationToken cancellationToken) =>
        _graph.ListRoutesAsync(limit, cancellationToken);

    /// <inheritdoc />
    public Task<NodeRecord?> GetNodeAsync(string nodeId, CancellationToken cancellationToken) =>
        _graph.GetNodeAsync(nodeId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<NodeRecord>> FindNodesByDisplayNameAsync(string displayName, CancellationToken cancellationToken) =>
        _graph.FindNodesByDisplayNameAsync(displayName, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<NodeRecord>> GetNodesByFileAsync(string normalizedPath, CancellationToken cancellationToken) =>
        _graph.GetNodesByFileAsync(normalizedPath, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SemanticEdgeRecord>> GetAllEdgesAsync(CancellationToken cancellationToken) =>
        _graph.GetAllEdgesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SemanticEdgeRecord>> GetOutgoingEdgesAsync(string nodeId, CancellationToken cancellationToken) =>
        _graph.GetOutgoingEdgesAsync(nodeId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SemanticEdgeRecord>> GetIncomingEdgesAsync(string nodeId, CancellationToken cancellationToken) =>
        _graph.GetIncomingEdgesAsync(nodeId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FileListItem>> FindFilesByPathAsync(string fragment, int limit, CancellationToken cancellationToken) =>
        _graph.FindFilesByPathAsync(fragment, limit, cancellationToken);

    /// <inheritdoc />
    public Task<int> GetFileTokenEstimateAsync(string normalizedPath, CancellationToken cancellationToken) =>
        _graph.GetFileTokenEstimateAsync(normalizedPath, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, int>> GetFileTokenEstimatesAsync(CancellationToken cancellationToken) =>
        _graph.GetFileTokenEstimatesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(
        IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken) =>
        _graph.GetContentHashesAsync(normalizedPaths, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> GetAllFileHashesAsync(CancellationToken cancellationToken) =>
        _graph.GetAllFileHashesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetFilesByLanguageAsync(string language, CancellationToken cancellationToken) =>
        _graph.GetFilesByLanguageAsync(language, cancellationToken);

    /// <inheritdoc />
    public Task<int> GetRouteCountAsync(CancellationToken cancellationToken) =>
        _graph.GetRouteCountAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<LanguageCount>> GetLanguageCountsAsync(CancellationToken cancellationToken) =>
        _graph.GetLanguageCountsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FileDependencyEdge>> GetFileDependencyEdgesAsync(CancellationToken cancellationToken) =>
        _graph.GetFileDependencyEdgesAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _connectionFactory.ClearPool();
        return ValueTask.CompletedTask;
    }
}
