namespace Fuse.Indexing;

/// <summary>
///     The SQLite schema for the workspace semantic index: the target version and the DDL that
///     creates every Fuse-owned table and index.
/// </summary>
/// <remarks>
///     The schema is rebuilt from scratch whenever the on-disk version is below
///     <see cref="TargetVersion" /> (see <see cref="IndexSchemaMigrator" />); there is no
///     incremental migration path in V3. The full-text search virtual table is created separately
///     so a runtime lacking FTS5 can still build the relational schema and fall back.
/// </remarks>
public static class WorkspaceIndexSchema
{
    /// <summary>
    ///     The current schema version. The existing cache database carries a lower or absent version,
    ///     so it is dropped and rebuilt on the first V3 run.
    /// </summary>
    /// <remarks>
    ///     Version 15 (v4 R5): the semantic analyzers now emit type-level <c>references</c> edges. That is an
    ///     extraction-contract change (a stale index has no reference edges), so the version bump forces a
    ///     rebuild rather than serving an index missing the new edges.
    ///     <para>
    ///     Version 16 (v4.1 K1): the dense embedding channel was retired. The <c>chunk_embeddings</c> table is
    ///     dropped from the schema; the version bump forces a stale index that still carries the table to rebuild
    ///     without it (the migrator drops and recreates, so the bump is the whole migration).
    ///     </para>
    ///     <para>
    ///     Version 17 (v4.2 R60): <c>tfm_availability</c> records the target-framework availability of canonical
    ///     multi-target semantic declarations and graph facts.
    ///     <para>
    ///     Version 18 (v4.4): <c>files.index_detail</c> records whether full source, declarations only, or
    ///     inventory metadata was retained. Full-text rows move to contentless-delete FTS5 keyed by
    ///     <c>search_documents</c>, and the unused <c>git_cochange</c> table is removed.
    ///     </para>
    ///     </para>
    /// </remarks>
    public const int TargetVersion = 18;

    /// <summary>
    ///     The extraction-contract version: what the indexer extracts (symbol, edge, chunk, and route semantics),
    ///     independent of the relational <see cref="TargetVersion" /> and of the product version.
    /// </summary>
    /// <remarks>
    ///     Index reuse is gated on a schema-version match AND an extraction-version match, never on the product
    ///     version (R22). A minor or patch product bump that does not change what is extracted reuses a good index;
    ///     the <c>fuse_version</c> stamp is kept for diagnostics only and no longer forces a rebuild. Bump this
    ///     constant in the same change as any extractor behavior change (symbol, edge, chunk, or route semantics)
    ///     so a stale index rebuilds; a forgotten bump is the only failure mode, never routine over-rebuilding.
    ///     Version 1 (v4.2 R22): the extraction contract is decoupled from the product version. Set to the value
    ///     that describes the extractor as of the schema-16 index; increment on the next extractor change.
    ///     Version 2 (v4.2 R60): canonical multi-target union preserves every declaration and graph fact while
    ///     recording its target-framework availability.
    ///     Version 3 (v4.2): tier-1 build capture projects cross-project <c>tests</c> edges before the graph is
    ///     stored, so <c>fuse_test</c> selects the same covering tests as the ordinary semantic workspace path.
    ///     Version 4 (v4.4): generated files retain declarations and routes but omit method bodies and comments;
    ///     files above the source limit retain inventory metadata only.
    /// </remarks>
    public const int ExtractionContractVersion = 4;

    /// <summary>
    ///     Database-level pragmas applied once at schema creation. WAL journaling and
    ///     <c>synchronous = NORMAL</c> persist with the file.
    /// </summary>
    public const string CreatePragmas =
        "PRAGMA journal_mode = WAL;" +
        "PRAGMA synchronous = NORMAL;" +
        "PRAGMA wal_autocheckpoint = 1000;" +
        "PRAGMA journal_size_limit = 67108864;" +
        "PRAGMA auto_vacuum = INCREMENTAL;";

    /// <summary>
    ///     Idempotent DDL creating every Fuse-owned relational table and index, plus the
    ///     <c>schema_version</c> bookkeeping table. Excludes the FTS5 virtual table.
    /// </summary>
    public const string CreateTablesDdl = """
        CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS index_meta(
          key TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS files(
          file_id INTEGER PRIMARY KEY,
          path TEXT NOT NULL UNIQUE,
          normalized_path TEXT NOT NULL UNIQUE,
          extension TEXT NOT NULL,
          size_bytes INTEGER NOT NULL,
          mtime_utc_ticks INTEGER NOT NULL,
          content_hash TEXT NOT NULL,
          project_id INTEGER NULL,
          is_generated INTEGER NOT NULL DEFAULT 0,
          is_test INTEGER NOT NULL DEFAULT 0,
          language TEXT NULL,
          index_detail TEXT NOT NULL DEFAULT 'full',
          indexed_at_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_files_hash ON files(content_hash);
        CREATE INDEX IF NOT EXISTS idx_files_project ON files(project_id);
        CREATE INDEX IF NOT EXISTS idx_files_extension ON files(extension);
        CREATE INDEX IF NOT EXISTS idx_files_language ON files(language);

        CREATE TABLE IF NOT EXISTS projects(
          project_id INTEGER PRIMARY KEY,
          path TEXT NOT NULL UNIQUE,
          name TEXT NOT NULL,
          assembly_name TEXT NULL,
          target_framework TEXT NULL,
          project_hash TEXT NOT NULL,
          indexed_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS tfm_availability(
          entity_kind TEXT NOT NULL,
          entity_id TEXT NOT NULL,
          target_framework TEXT NOT NULL,
          PRIMARY KEY(entity_kind, entity_id, target_framework)
        );
        CREATE INDEX IF NOT EXISTS idx_tfm_availability_entity
          ON tfm_availability(entity_kind, entity_id);

        CREATE TABLE IF NOT EXISTS nodes(
          node_id TEXT PRIMARY KEY,
          kind TEXT NOT NULL,
          display_name TEXT NOT NULL,
          file_id INTEGER NULL,
          project_id INTEGER NULL,
          symbol_id TEXT NULL,
          stable_key TEXT NOT NULL,
          start_line INTEGER NULL,
          end_line INTEGER NULL,
          signature TEXT NULL,
          metadata_json TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_nodes_kind ON nodes(kind);
        CREATE INDEX IF NOT EXISTS idx_nodes_file ON nodes(file_id);
        CREATE INDEX IF NOT EXISTS idx_nodes_display ON nodes(display_name);
        CREATE INDEX IF NOT EXISTS idx_nodes_symbol ON nodes(symbol_id);

        CREATE TABLE IF NOT EXISTS symbols(
          symbol_id TEXT PRIMARY KEY,
          file_id INTEGER NOT NULL,
          project_id INTEGER NULL,
          kind TEXT NOT NULL,
          name TEXT NOT NULL,
          fully_qualified_name TEXT NOT NULL,
          metadata_name TEXT NULL,
          containing_type TEXT NULL,
          namespace TEXT NULL,
          assembly_name TEXT NULL,
          accessibility TEXT NULL,
          signature TEXT NULL,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          is_public_api INTEGER NOT NULL DEFAULT 0,
          FOREIGN KEY(file_id) REFERENCES files(file_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
        CREATE INDEX IF NOT EXISTS idx_symbols_fqn ON symbols(fully_qualified_name);
        CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);
        CREATE INDEX IF NOT EXISTS idx_symbols_kind ON symbols(kind);

        CREATE TABLE IF NOT EXISTS chunks(
          chunk_id TEXT PRIMARY KEY,
          file_id INTEGER NOT NULL,
          symbol_id TEXT NULL,
          kind TEXT NOT NULL,
          name TEXT NULL,
          stable_key TEXT NOT NULL,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          text_hash TEXT NOT NULL,
          token_estimate INTEGER NOT NULL,
          reduced_token_estimate INTEGER NOT NULL,
          signature TEXT NULL,
          outline TEXT NULL,
          FOREIGN KEY(file_id) REFERENCES files(file_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_chunks_file ON chunks(file_id);
        CREATE INDEX IF NOT EXISTS idx_chunks_symbol ON chunks(symbol_id);
        CREATE INDEX IF NOT EXISTS idx_chunks_kind ON chunks(kind);

        CREATE TABLE IF NOT EXISTS edges(
          edge_id TEXT PRIMARY KEY,
          from_node_id TEXT NOT NULL,
          to_node_id TEXT NOT NULL,
          edge_type TEXT NOT NULL,
          weight REAL NOT NULL,
          confidence REAL NOT NULL,
          evidence TEXT NULL,
          evidence_file_id INTEGER NULL,
          evidence_start_line INTEGER NULL,
          evidence_end_line INTEGER NULL,
          metadata_json TEXT NULL,
          FOREIGN KEY(from_node_id) REFERENCES nodes(node_id) ON DELETE CASCADE,
          FOREIGN KEY(to_node_id) REFERENCES nodes(node_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_edges_from ON edges(from_node_id);
        CREATE INDEX IF NOT EXISTS idx_edges_to ON edges(to_node_id);
        CREATE INDEX IF NOT EXISTS idx_edges_type ON edges(edge_type);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_edges_unique
          ON edges(from_node_id, to_node_id, edge_type, evidence_file_id);

        CREATE TABLE IF NOT EXISTS routes(
          route_id TEXT PRIMARY KEY,
          http_method TEXT NOT NULL,
          route_pattern TEXT NOT NULL,
          handler_symbol_id TEXT NULL,
          file_id INTEGER NOT NULL,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          source_kind TEXT NOT NULL,
          metadata_json TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_routes_pattern ON routes(route_pattern);
        CREATE INDEX IF NOT EXISTS idx_routes_method ON routes(http_method);

        CREATE TABLE IF NOT EXISTS di_registrations(
          registration_id TEXT PRIMARY KEY,
          service_symbol_id TEXT NULL,
          implementation_symbol_id TEXT NULL,
          service_name TEXT NOT NULL,
          implementation_name TEXT NULL,
          lifetime TEXT NOT NULL,
          file_id INTEGER NOT NULL,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          registration_kind TEXT NOT NULL,
          confidence REAL NOT NULL,
          evidence TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_di_service ON di_registrations(service_name);
        CREATE INDEX IF NOT EXISTS idx_di_impl ON di_registrations(implementation_name);

        CREATE TABLE IF NOT EXISTS options_bindings(
          binding_id TEXT PRIMARY KEY,
          options_symbol_id TEXT NULL,
          options_name TEXT NOT NULL,
          config_section TEXT NULL,
          file_id INTEGER NOT NULL,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          binding_kind TEXT NOT NULL,
          confidence REAL NOT NULL,
          evidence TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_options_name ON options_bindings(options_name);
        CREATE INDEX IF NOT EXISTS idx_options_section ON options_bindings(config_section);

        CREATE TABLE IF NOT EXISTS search_documents(
          document_id INTEGER PRIMARY KEY,
          chunk_id TEXT NOT NULL UNIQUE,
          file_id INTEGER NOT NULL,
          FOREIGN KEY(chunk_id) REFERENCES chunks(chunk_id) ON DELETE CASCADE,
          FOREIGN KEY(file_id) REFERENCES files(file_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_search_documents_file ON search_documents(file_id);

        CREATE TABLE IF NOT EXISTS check_sessions(
          session_id TEXT PRIMARY KEY,
          root TEXT NOT NULL,
          baseline_json TEXT NOT NULL,
          updated_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS claim_ledger(
          session_id TEXT PRIMARY KEY,
          root TEXT NOT NULL,
          claims_json TEXT NOT NULL,
          updated_utc TEXT NOT NULL
        );
        """;

    /// <summary>
    ///     DDL for the FTS5 chunk search index. Created separately from <see cref="CreateTablesDdl" /> so a
    ///     runtime without FTS5 can still build the relational schema and fall back to no full-text search.
    /// </summary>
    /// <remarks>
    ///     Column order matters: the relevance weights in the search query (see the store's search path)
    ///     are positional. The integer FTS row id joins through <c>search_documents</c> to a chunk. The
    ///     contentless-delete table avoids retaining a second stored copy of indexed source text. <c>subtokens</c>
    ///     holds the subword expansion of the chunk's identifiers
    ///     (computed in C# by <see cref="IdentifierSplitter" /> and stored as text), so a prose query word
    ///     matches a compound name; it is weighted below the exact name but above the body. <c>stems</c> holds
    ///     the Porter-stemmed form of the chunk's identifiers and comments (computed in C# by
    ///     <see cref="PorterStemmer" />), so an inflected query word matches an inflected code or comment word;
    ///     it is weighted low, as a fuzzy bridge.
    /// </remarks>
    public const string CreateFtsDdl =
        "CREATE VIRTUAL TABLE IF NOT EXISTS chunk_fts USING fts5(" +
        "  path, name, symbols, signature, comments, body, subtokens, stems," +
        "  content = '', contentless_delete = 1, tokenize = 'unicode61');";
}
