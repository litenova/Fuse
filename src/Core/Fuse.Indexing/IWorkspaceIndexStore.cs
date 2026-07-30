namespace Fuse.Indexing;

/// <summary>
///     The persistent SQLite store backing the workspace semantic index.
/// </summary>
/// <remarks>
///     This supersedes the earlier key-value and relevance-postings caches as the canonical store.
///     The upsert, delete, search, and edge-traversal members are added in later overhaul phases; this
///     phase establishes the database lifecycle (open, migrate, report state, dispose).
/// </remarks>
public interface IWorkspaceIndexStore : IAsyncDisposable
{
    /// <summary>
    ///     Write initialization: opens the database, brings the schema to the current version (rebuilding
    ///     from scratch when the on-disk version is older), probes FTS availability, and stamps
    ///     <c>index_meta</c>. Use on first create, schema migration, incompatible-version rebuild, and
    ///     explicit <c>fuse index</c>; foreground read tools should prefer
    ///     <see cref="OpenForReadAsync" /> when the database already exists at the target schema.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel initialization.</param>
    /// <returns>
    ///     The initialization outcome. When <see cref="WorkspaceIndexInitializeOutcome.RebuiltEmptyStore" /> is
    ///     <see langword="true" />, the store is empty and must be re-indexed from source before serving reads.
    /// </returns>
    Task<WorkspaceIndexInitializeOutcome> InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Discards every Fuse-owned index table and recreates an empty current schema.</summary>
    /// <param name="cancellationToken">A token to cancel the reset.</param>
    /// <returns>A task that completes when the empty searchable schema is ready.</returns>
    /// <remarks>This is the destructive derived-data path behind an explicit <c>fuse index --force</c>.</remarks>
    Task ResetAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Read-only warm open: verifies the on-disk schema version and Fuse build stamp without running
    ///     migrations or writing <c>index_meta</c>. Returns <see cref="WorkspaceIndexReadOpenStatus.Ready" />
    ///     when the database is usable for reads; otherwise the caller should fall back to
    ///     <see cref="InitializeAsync" />.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the open.</param>
    /// <returns>The open outcome.</returns>
    Task<WorkspaceIndexReadOpenStatus> OpenForReadAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the current schema version, status, and record counts.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>A <see cref="WorkspaceIndexState" /> snapshot.</returns>
    Task<WorkspaceIndexState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or updates files, preserving the integer <c>file_id</c> across re-index.</summary>
    /// <param name="files">The files to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    /// <remarks>The whole batch is committed in a single transaction.</remarks>
    Task UpsertFilesAsync(IReadOnlyList<IndexedFileRecord> files, CancellationToken cancellationToken);

    /// <summary>Inserts or updates projects.</summary>
    /// <param name="projects">The projects to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertProjectsAsync(IReadOnlyList<ProjectRecord> projects, CancellationToken cancellationToken);

    /// <summary>Replaces the stored target-framework availability facts with one complete canonical union.</summary>
    /// <param name="availability">The availability rows from the complete capture union.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the replacement is committed.</returns>
    Task ReplaceTfmAvailabilityAsync(IReadOnlyList<TfmAvailabilityRecord> availability, CancellationToken cancellationToken);

    /// <summary>Lists the stored target-framework availability facts in deterministic order.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The canonical availability rows.</returns>
    Task<IReadOnlyList<TfmAvailabilityRecord>> GetTfmAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or updates semantic graph nodes.</summary>
    /// <param name="nodes">The nodes to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    /// <remarks>Nodes must exist before the edges that reference them are upserted.</remarks>
    Task UpsertNodesAsync(IReadOnlyList<NodeRecord> nodes, CancellationToken cancellationToken);

    /// <summary>Inserts or updates symbols.</summary>
    /// <param name="symbols">The symbols to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    /// <remarks>Symbols whose declaring file is not yet indexed are skipped.</remarks>
    Task UpsertSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken cancellationToken);

    /// <summary>Inserts or updates chunks.</summary>
    /// <param name="chunks">The chunks to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    /// <remarks>Chunks whose owning file is not yet indexed are skipped.</remarks>
    Task UpsertChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the normalized paths of indexed files tagged with a given language, so retrieval can filter or
    ///     blend by language over the language-agnostic tables.
    /// </summary>
    /// <param name="language">The language tag (for example <c>csharp</c>, <c>python</c>), as set by the selecting provider.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching files' normalized paths; empty when none carry the tag.</returns>
    Task<IReadOnlyList<string>> GetFilesByLanguageAsync(string language, CancellationToken cancellationToken);

    /// <summary>Returns the number of indexed routes.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The route count.</returns>
    Task<int> GetRouteCountAsync(CancellationToken cancellationToken);

    /// <summary>Returns the indexed file count per language tag, most files first.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The per-language counts; files with no tag are grouped under <c>unknown</c>.</returns>
    Task<IReadOnlyList<LanguageCount>> GetLanguageCountsAsync(CancellationToken cancellationToken);

    /// <summary>Returns the typed dependency edges resolved to distinct file pairs, for a graph projection.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The file-to-file edges (self-edges excluded).</returns>
    Task<IReadOnlyList<FileDependencyEdge>> GetFileDependencyEdgesAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or updates semantic edges.</summary>
    /// <param name="edges">The edges to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    /// <remarks>Both endpoint nodes must already exist; the edge identity is derived from its endpoints, type, and evidence file.</remarks>
    Task UpsertEdgesAsync(IReadOnlyList<SemanticEdgeRecord> edges, CancellationToken cancellationToken);

    /// <summary>Inserts or updates routes.</summary>
    /// <param name="routes">The routes to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertRoutesAsync(IReadOnlyList<RouteRecord> routes, CancellationToken cancellationToken);

    /// <summary>Inserts or updates dependency-injection registrations.</summary>
    /// <param name="registrations">The registrations to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertDiRegistrationsAsync(IReadOnlyList<DiRegistrationRecord> registrations, CancellationToken cancellationToken);

    /// <summary>Inserts or updates options/config bindings.</summary>
    /// <param name="bindings">The bindings to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertOptionsBindingsAsync(IReadOnlyList<OptionsBindingRecord> bindings, CancellationToken cancellationToken);

    /// <summary>
    ///     Deletes all per-file derived data (symbols, chunks, nodes, edges, routes, DI registrations,
    ///     options bindings) for a file, leaving the <c>files</c> row in place for re-upsert.
    /// </summary>
    /// <param name="normalizedPath">The normalized path of the file whose data to clear.</param>
    /// <param name="cancellationToken">A token to cancel the delete.</param>
    /// <returns>A task that completes when the delete is committed.</returns>
    /// <remarks>Used by incremental re-index: clear a changed file's data, then re-upsert it.</remarks>
    Task DeleteFileDataAsync(string normalizedPath, CancellationToken cancellationToken);

    /// <summary>Clears derived rows for a complete batch of files while retaining their parent file records.</summary>
    /// <param name="normalizedPaths">The normalized paths whose derived rows must be replaced.</param>
    /// <param name="cancellationToken">A token to cancel the delete.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    /// <remarks>Full index passes use this before upserting their authoritative extraction result.</remarks>
    Task ClearFileDataAsync(IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken);

    /// <summary>Deletes a file record and all of its derived index data.</summary>
    /// <param name="normalizedPath">The normalized path of the file to remove.</param>
    /// <param name="cancellationToken">A token to cancel the delete.</param>
    /// <returns>A task that completes when the delete is committed.</returns>
    /// <remarks>Used when a file no longer belongs to the current workspace inventory.</remarks>
    Task DeleteFileAsync(string normalizedPath, CancellationToken cancellationToken);

    /// <summary>Removes every stored file that is absent from the supplied workspace inventory.</summary>
    /// <param name="normalizedPaths">The complete set of normalized paths in the current inventory.</param>
    /// <param name="cancellationToken">A token to cancel the prune.</param>
    /// <returns>The number of stale file records removed.</returns>
    Task<int> PruneFilesAsync(IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken);

    /// <summary>
    ///     Runs a full-text search over indexed chunks, ranked by relevance.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="cancellationToken">A token to cancel the search.</param>
    /// <returns>
    ///     Ranked hits (highest score first). Empty when the query is blank or full-text search is
    ///     unavailable in the current runtime.
    /// </returns>
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);

    /// <summary>
    ///     Lists indexed symbols, public-API first then by name, for summaries such as the workspace map.
    /// </summary>
    /// <param name="limit">The maximum number of symbols to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The symbol summaries.</returns>
    Task<IReadOnlyList<SymbolListItem>> ListSymbolsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    ///     Lists indexed routes ordered by pattern then method.
    /// </summary>
    /// <param name="limit">The maximum number of routes to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The route summaries.</returns>
    Task<IReadOnlyList<RouteListItem>> ListRoutesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    ///     Finds indexed symbols whose simple name contains a fragment (case-insensitive), public-API first.
    /// </summary>
    /// <param name="nameFragment">The name fragment to match.</param>
    /// <param name="limit">The maximum number of symbols to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching symbol summaries.</returns>
    Task<IReadOnlyList<SymbolListItem>> FindSymbolsByNameAsync(string nameFragment, int limit, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns exact signature records for a batch of symbol names (matched by simple name or fully qualified
    ///     name), the store side of <c>fuse_find</c> (kind=signatures).
    /// </summary>
    /// <param name="names">The symbol names to look up (simple or fully qualified).</param>
    /// <param name="limitPerName">The maximum number of matches to return per requested name.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching signatures, public-API and exact-name matches first; empty when nothing matches.</returns>
    /// <remarks>
    ///     Serves an agent's most common lookup (the exact shape of N members) from the persisted symbol table in
    ///     one call instead of many grep-and-read round-trips. The signature is populated in semantic mode; in
    ///     syntax mode it may be null, and the caller says so rather than implying a signature it does not have.
    /// </remarks>
    Task<IReadOnlyList<SymbolSignature>> GetSignaturesByNamesAsync(
        IReadOnlyCollection<string> names, int limitPerName, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the member signatures of a type, matched by the member's <c>containing_type</c> against a
    ///     simple or fully qualified type name. The store side of the R6 repair packet: when a speculative
    ///     typecheck reports a missing member on a type, this enumerates the members that type actually has so a
    ///     nearest-name suggestion can be offered.
    /// </summary>
    /// <param name="typeName">The declaring type's simple or fully qualified name.</param>
    /// <param name="limit">The maximum number of members to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The type's member signatures, public-API first; empty when the type is unknown or has no recorded members.</returns>
    Task<IReadOnlyList<SymbolSignature>> GetMembersOfTypeAsync(
        string typeName, int limit, CancellationToken cancellationToken);

    /// <summary>Sets a key in the index metadata table.</summary>
    /// <param name="key">The metadata key.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the value is committed.</returns>
    Task SetMetaAsync(string key, string value, CancellationToken cancellationToken);

    /// <summary>Reads a key from the index metadata table.</summary>
    /// <param name="key">The metadata key.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The stored value, or null when the key is absent.</returns>
    Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken);

    /// <summary>Reads a single node by id.</summary>
    /// <param name="nodeId">The node id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The node, or null when absent. <see cref="NodeRecord.FilePath" /> is the file's normalized path.</returns>
    Task<NodeRecord?> GetNodeAsync(string nodeId, CancellationToken cancellationToken);

    /// <summary>Finds nodes whose display name matches exactly (case-insensitive).</summary>
    /// <param name="displayName">The display name to match.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching nodes.</returns>
    Task<IReadOnlyList<NodeRecord>> FindNodesByDisplayNameAsync(string displayName, CancellationToken cancellationToken);

    /// <summary>Returns the nodes declared in a file.</summary>
    /// <param name="normalizedPath">The file's normalized path.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The nodes whose file is the given path.</returns>
    Task<IReadOnlyList<NodeRecord>> GetNodesByFileAsync(string normalizedPath, CancellationToken cancellationToken);

    /// <summary>Returns every edge in the graph, for whole-graph evaluation and export.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>All edges; <see cref="SemanticEdgeRecord.EvidenceFilePath" /> is the evidence file's normalized path or null.</returns>
    Task<IReadOnlyList<SemanticEdgeRecord>> GetAllEdgesAsync(CancellationToken cancellationToken);

    /// <summary>Returns the edges leaving a node.</summary>
    /// <param name="nodeId">The source node id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The outgoing edges. <see cref="SemanticEdgeRecord.EvidenceFilePath" /> is the evidence file's normalized path or null.</returns>
    Task<IReadOnlyList<SemanticEdgeRecord>> GetOutgoingEdgesAsync(string nodeId, CancellationToken cancellationToken);

    /// <summary>Returns the edges entering a node.</summary>
    /// <param name="nodeId">The target node id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The incoming edges.</returns>
    Task<IReadOnlyList<SemanticEdgeRecord>> GetIncomingEdgesAsync(string nodeId, CancellationToken cancellationToken);

    /// <summary>Finds indexed files whose normalized path contains a fragment (case-insensitive).</summary>
    /// <param name="fragment">The path fragment to match.</param>
    /// <param name="limit">The maximum number of files to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching files.</returns>
    Task<IReadOnlyList<FileListItem>> FindFilesByPathAsync(string fragment, int limit, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the estimated reduced token cost of a file, summed over its chunks.
    /// </summary>
    /// <param name="normalizedPath">The file's normalized path.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The summed reduced token estimate, or 0 when the file has no chunks.</returns>
    Task<int> GetFileTokenEstimateAsync(string normalizedPath, CancellationToken cancellationToken);

    /// <summary>Returns the estimated reduced token cost of every indexed file, keyed by normalized path.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>A map from normalized path to summed reduced token estimate (0 for files with no chunks).</returns>
    Task<IReadOnlyDictionary<string, int>> GetFileTokenEstimatesAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the content hash of each of the given files, keyed by normalized path.
    /// </summary>
    /// <param name="normalizedPaths">The normalized file paths to look up.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>
    ///     A map from normalized path to content hash; a path with no indexed file is omitted. Used to collapse
    ///     byte-identical duplicates (for example copies that escaped exclusion) to one canonical result.
    /// </returns>
    Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the content hash of every indexed file, keyed by normalized path.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>A map from normalized path to content hash for all files currently in the index.</returns>
    /// <remarks>
    ///     Used by the freshness reconcile pass (the N6 contract): the reconciler hashes the current on-disk
    ///     content of each known file and compares it to the stored hash to find files edited or deleted since
    ///     the index was written, so a read tool reconciles them before answering rather than serving stale data.
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>> GetAllFileHashesAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Persists (or replaces) a check session's diagnostic baseline (S2): the set of diagnostics as of the
    ///     session's start or its last mark-green, against which <c>fuse_check</c> delta mode diffs the current
    ///     diagnostics. Persisting to the store lets a restarted process resume the session with its baseline
    ///     intact, so an hour of staged work is not lost to a crash.
    /// </summary>
    /// <param name="sessionId">The opaque session id.</param>
    /// <param name="root">The absolute workspace root the session is rooted at.</param>
    /// <param name="baseline">The baseline diagnostics to record.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the baseline is persisted.</returns>
    Task SaveCheckSessionBaselineAsync(
        string sessionId, string root, IReadOnlyList<CheckDiagnostic> baseline, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns a persisted check session's baseline (S2), or <c>null</c> when no session with that id exists.
    /// </summary>
    /// <param name="sessionId">The opaque session id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The session baseline, or <c>null</c> when the session is unknown.</returns>
    Task<CheckSessionBaseline?> GetCheckSessionBaselineAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    ///     Persists a session's accumulated claims ledger (U2) as an opaque JSON payload, keyed by session id. The
    ///     store keeps the payload verbatim (it does not know the claim shape, which lives in the retrieval layer),
    ///     so the caller serializes and deserializes. Additive table, idempotent DDL, so no schema version bump.
    /// </summary>
    /// <param name="sessionId">The opaque session id.</param>
    /// <param name="root">The absolute workspace root the session is rooted at.</param>
    /// <param name="claimsJson">The serialized claims payload.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the ledger is persisted.</returns>
    Task SaveClaimLedgerAsync(string sessionId, string root, string claimsJson, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    ///     Returns a session's persisted claims ledger (U2), or <c>null</c> when no session with that id exists.
    /// </summary>
    /// <param name="sessionId">The opaque session id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The ledger record, or <c>null</c> when the session is unknown.</returns>
    Task<ClaimLedgerRecord?> GetClaimLedgerAsync(string sessionId, CancellationToken cancellationToken)
        => Task.FromResult<ClaimLedgerRecord?>(null);

    /// <summary>
    ///     Lists the sessions the store knows for a root (G3): every session with a recorded check-diagnostics
    ///     baseline or an accumulated claim ledger, most recently written first. This is the read the extension
    ///     observability panel uses to show what an agent has been doing. Additive read over the existing
    ///     <c>check_sessions</c> and <c>claim_ledger</c> tables; no schema change.
    /// </summary>
    /// <param name="root">The absolute workspace root to filter sessions by.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The known sessions for the root, most recently written first (empty when there are none).</returns>
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string root, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SessionSummary>>([]);
}

/// <summary>
///     One session the store knows for a root (G3): its id, when it was last written, and whether it has a
///     check-diagnostics baseline and an accumulated claim ledger.
/// </summary>
/// <param name="SessionId">The opaque session id.</param>
/// <param name="UpdatedUtc">The ISO-8601 UTC time the session was last written (baseline save or claim append).</param>
/// <param name="HasBaseline">Whether the session has a recorded check-diagnostics baseline.</param>
/// <param name="HasClaims">Whether the session has an accumulated claim ledger.</param>
public sealed record SessionSummary(string SessionId, string UpdatedUtc, bool HasBaseline, bool HasClaims);

/// <summary>
///     A persisted check-session baseline (S2): the diagnostics recorded as of the session's start or last
///     mark-green, plus the session's root and the time the baseline was last written.
/// </summary>
/// <param name="SessionId">The opaque session id.</param>
/// <param name="Root">The absolute workspace root the session is rooted at.</param>
/// <param name="Diagnostics">The baseline diagnostics the delta is computed against.</param>
/// <param name="UpdatedUtc">The ISO-8601 UTC time the baseline was last written.</param>
public sealed record CheckSessionBaseline(
    string SessionId, string Root, IReadOnlyList<CheckDiagnostic> Diagnostics, string UpdatedUtc);

/// <summary>
///     A persisted claims ledger (U2): the serialized claims accumulated in a session, plus its root and the time
///     it was last written. The JSON payload is opaque to the store; the retrieval layer owns the claim shape.
/// </summary>
/// <param name="SessionId">The opaque session id.</param>
/// <param name="Root">The absolute workspace root the session is rooted at.</param>
/// <param name="ClaimsJson">The serialized claims payload.</param>
/// <param name="UpdatedUtc">The ISO-8601 UTC time the ledger was last written.</param>
public sealed record ClaimLedgerRecord(string SessionId, string Root, string ClaimsJson, string UpdatedUtc);
