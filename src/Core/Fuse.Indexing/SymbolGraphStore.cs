using System.IO.Hashing;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Fuse.Indexing;

/// <summary>
///     Symbol resolution, semantic graph nodes and edges, file records, and related relational queries.
/// </summary>
internal sealed class SymbolGraphStore
{
    private const string NodeSelect =
        "SELECT n.node_id, n.kind, n.display_name, n.stable_key, files.normalized_path, n.symbol_id, " +
        "n.start_line, n.end_line, n.signature, n.metadata_json " +
        "FROM nodes n LEFT JOIN files ON files.file_id = n.file_id";

    private readonly WorkspaceIndexConnectionFactory _connectionFactory;
    private readonly FtsSearchEngine _fts;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SymbolGraphStore" /> class.
    /// </summary>
    /// <param name="connectionFactory">The connection factory for the index database.</param>
    /// <param name="fts">The FTS engine used when chunk rows change.</param>
    public SymbolGraphStore(WorkspaceIndexConnectionFactory connectionFactory, FtsSearchEngine fts)
    {
        _connectionFactory = connectionFactory;
        _fts = fts;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.UpsertFilesAsync" />
    public async Task UpsertFilesAsync(IReadOnlyList<IndexedFileRecord> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var projectIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO files(path, normalized_path, extension, size_bytes, mtime_utc_ticks, content_hash,
                              project_id, is_generated, is_test, language, index_detail, indexed_at_utc)
            VALUES($path, $norm, $ext, $size, $mtime, $hash, $project, $generated, $test, $language, $detail, $indexed)
            ON CONFLICT(normalized_path) DO UPDATE SET
              path = excluded.path, extension = excluded.extension, size_bytes = excluded.size_bytes,
              mtime_utc_ticks = excluded.mtime_utc_ticks, content_hash = excluded.content_hash,
              project_id = excluded.project_id, is_generated = excluded.is_generated,
              is_test = excluded.is_test, language = excluded.language, index_detail = excluded.index_detail,
              indexed_at_utc = excluded.indexed_at_utc;
            """;
        var pathParam = command.Parameters.Add("$path", SqliteType.Text);
        var normParam = command.Parameters.Add("$norm", SqliteType.Text);
        var extParam = command.Parameters.Add("$ext", SqliteType.Text);
        var sizeParam = command.Parameters.Add("$size", SqliteType.Integer);
        var mtimeParam = command.Parameters.Add("$mtime", SqliteType.Integer);
        var hashParam = command.Parameters.Add("$hash", SqliteType.Text);
        var projectParam = command.Parameters.Add("$project", SqliteType.Integer);
        var generatedParam = command.Parameters.Add("$generated", SqliteType.Integer);
        var testParam = command.Parameters.Add("$test", SqliteType.Integer);
        var languageParam = command.Parameters.Add("$language", SqliteType.Text);
        var detailParam = command.Parameters.Add("$detail", SqliteType.Text);
        var indexedParam = command.Parameters.Add("$indexed", SqliteType.Text);

        foreach (var file in files)
        {
            pathParam.Value = file.Path;
            normParam.Value = file.NormalizedPath;
            extParam.Value = file.Extension;
            sizeParam.Value = file.SizeBytes;
            mtimeParam.Value = file.MtimeUtcTicks;
            hashParam.Value = file.ContentHash;
            projectParam.Value = (object?)await ResolveProjectIdAsync(connection, transaction, file.ProjectPath, projectIds, cancellationToken) ?? DBNull.Value;
            generatedParam.Value = file.IsGenerated ? 1 : 0;
            testParam.Value = file.IsTest ? 1 : 0;
            languageParam.Value = (object?)file.Language ?? DBNull.Value;
            detailParam.Value = ToDetailValue(file.DetailLevel);
            indexedParam.Value = (file.IndexedAtUtc ?? DateTimeOffset.UtcNow).ToString("o");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.UpsertProjectsAsync" />
    public async Task UpsertProjectsAsync(IReadOnlyList<ProjectRecord> projects, CancellationToken cancellationToken)
    {
        if (projects.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO projects(path, name, assembly_name, target_framework, project_hash, indexed_at_utc)
            VALUES($path, $name, $assembly, $tfm, $hash, $indexed)
            ON CONFLICT(path) DO UPDATE SET
              name = excluded.name, assembly_name = excluded.assembly_name,
              target_framework = excluded.target_framework, project_hash = excluded.project_hash,
              indexed_at_utc = excluded.indexed_at_utc;
            """;
        var pathParam = command.Parameters.Add("$path", SqliteType.Text);
        var nameParam = command.Parameters.Add("$name", SqliteType.Text);
        var assemblyParam = command.Parameters.Add("$assembly", SqliteType.Text);
        var tfmParam = command.Parameters.Add("$tfm", SqliteType.Text);
        var hashParam = command.Parameters.Add("$hash", SqliteType.Text);
        var indexedParam = command.Parameters.Add("$indexed", SqliteType.Text);

        foreach (var project in projects)
        {
            pathParam.Value = project.Path;
            nameParam.Value = project.Name;
            assemblyParam.Value = (object?)project.AssemblyName ?? DBNull.Value;
            tfmParam.Value = (object?)project.TargetFramework ?? DBNull.Value;
            hashParam.Value = project.ProjectHash;
            indexedParam.Value = (project.IndexedAtUtc ?? DateTimeOffset.UtcNow).ToString("o");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.ReplaceTfmAvailabilityAsync" />
    public async Task ReplaceTfmAvailabilityAsync(
        IReadOnlyList<TfmAvailabilityRecord> availability,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM tfm_availability;";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        if (availability.Count > 0)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO tfm_availability(entity_kind, entity_id, target_framework)
                VALUES($kind, $id, $tfm);
                """;
            var kind = insert.Parameters.Add("$kind", SqliteType.Text);
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var tfm = insert.Parameters.Add("$tfm", SqliteType.Text);

            foreach (var item in availability)
            {
                kind.Value = item.EntityKind;
                id.Value = item.EntityId;
                tfm.Value = item.TargetFramework;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetTfmAvailabilityAsync" />
    public async Task<IReadOnlyList<TfmAvailabilityRecord>> GetTfmAvailabilityAsync(CancellationToken cancellationToken)
    {
        var result = new List<TfmAvailabilityRecord>();
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT entity_kind, entity_id, target_framework
            FROM tfm_availability
            ORDER BY entity_kind, entity_id, target_framework;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TfmAvailabilityRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return result;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.UpsertNodesAsync" />
    public async Task UpsertNodesAsync(IReadOnlyList<NodeRecord> nodes, CancellationToken cancellationToken)
    {
        if (nodes.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);
        var projectIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO nodes(node_id, kind, display_name, file_id, project_id, symbol_id,
                                         stable_key, start_line, end_line, signature, metadata_json)
            VALUES($id, $kind, $display, $file, $project, $symbol, $stable, $start, $end, $sig, $meta);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var kindParam = command.Parameters.Add("$kind", SqliteType.Text);
        var displayParam = command.Parameters.Add("$display", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var projectParam = command.Parameters.Add("$project", SqliteType.Integer);
        var symbolParam = command.Parameters.Add("$symbol", SqliteType.Text);
        var stableParam = command.Parameters.Add("$stable", SqliteType.Text);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var sigParam = command.Parameters.Add("$sig", SqliteType.Text);
        var metaParam = command.Parameters.Add("$meta", SqliteType.Text);

        foreach (var node in nodes)
        {
            idParam.Value = node.NodeId;
            kindParam.Value = node.Kind;
            displayParam.Value = node.DisplayName;
            fileParam.Value = (object?)await ResolveFileIdAsync(connection, transaction, node.FilePath, fileIds, cancellationToken) ?? DBNull.Value;
            projectParam.Value = (object?)await ResolveProjectIdAsync(connection, transaction, node.ProjectPath, projectIds, cancellationToken) ?? DBNull.Value;
            symbolParam.Value = (object?)node.SymbolId ?? DBNull.Value;
            stableParam.Value = node.StableKey;
            startParam.Value = (object?)node.StartLine ?? DBNull.Value;
            endParam.Value = (object?)node.EndLine ?? DBNull.Value;
            sigParam.Value = (object?)node.Signature ?? DBNull.Value;
            metaParam.Value = (object?)node.MetadataJson ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.UpsertSymbolsAsync" />
    public async Task UpsertSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);
        var projectIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO symbols(symbol_id, file_id, project_id, kind, name, fully_qualified_name,
                                           metadata_name, containing_type, namespace, assembly_name,
                                           accessibility, signature, start_line, end_line, is_public_api)
            VALUES($id, $file, $project, $kind, $name, $fqn, $meta, $containing, $ns, $assembly,
                   $access, $sig, $start, $end, $public);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var projectParam = command.Parameters.Add("$project", SqliteType.Integer);
        var kindParam = command.Parameters.Add("$kind", SqliteType.Text);
        var nameParam = command.Parameters.Add("$name", SqliteType.Text);
        var fqnParam = command.Parameters.Add("$fqn", SqliteType.Text);
        var metaParam = command.Parameters.Add("$meta", SqliteType.Text);
        var containingParam = command.Parameters.Add("$containing", SqliteType.Text);
        var nsParam = command.Parameters.Add("$ns", SqliteType.Text);
        var assemblyParam = command.Parameters.Add("$assembly", SqliteType.Text);
        var accessParam = command.Parameters.Add("$access", SqliteType.Text);
        var sigParam = command.Parameters.Add("$sig", SqliteType.Text);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var publicParam = command.Parameters.Add("$public", SqliteType.Integer);

        foreach (var symbol in symbols)
        {
            var fileId = await ResolveFileIdAsync(connection, transaction, symbol.FilePath, fileIds, cancellationToken);
            if (fileId is null)
                continue;

            idParam.Value = symbol.SymbolId;
            fileParam.Value = fileId.Value;
            projectParam.Value = (object?)await ResolveProjectIdAsync(connection, transaction, symbol.ProjectPath, projectIds, cancellationToken) ?? DBNull.Value;
            kindParam.Value = symbol.Kind;
            nameParam.Value = symbol.Name;
            fqnParam.Value = symbol.FullyQualifiedName;
            metaParam.Value = (object?)symbol.MetadataName ?? DBNull.Value;
            containingParam.Value = (object?)symbol.ContainingType ?? DBNull.Value;
            nsParam.Value = (object?)symbol.Namespace ?? DBNull.Value;
            assemblyParam.Value = (object?)symbol.AssemblyName ?? DBNull.Value;
            accessParam.Value = (object?)symbol.Accessibility ?? DBNull.Value;
            sigParam.Value = (object?)symbol.Signature ?? DBNull.Value;
            startParam.Value = symbol.StartLine;
            endParam.Value = symbol.EndLine;
            publicParam.Value = symbol.IsPublicApi ? 1 : 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.UpsertChunksAsync" />
    public async Task UpsertChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);
        var indexedDocuments = new List<FtsDocument>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO chunks(chunk_id, file_id, symbol_id, kind, name, stable_key,
                               start_line, end_line, text_hash, token_estimate,
                               reduced_token_estimate, signature, outline)
            VALUES($id, $file, $symbol, $kind, $name, $stable, $start, $end, $hash, $tokens,
                   $reduced, $sig, $outline)
            ON CONFLICT(chunk_id) DO UPDATE SET
              file_id = excluded.file_id, symbol_id = excluded.symbol_id, kind = excluded.kind,
              name = excluded.name, stable_key = excluded.stable_key, start_line = excluded.start_line,
              end_line = excluded.end_line, text_hash = excluded.text_hash,
              token_estimate = excluded.token_estimate,
              reduced_token_estimate = excluded.reduced_token_estimate,
              signature = excluded.signature, outline = excluded.outline;
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var symbolParam = command.Parameters.Add("$symbol", SqliteType.Text);
        var kindParam = command.Parameters.Add("$kind", SqliteType.Text);
        var nameParam = command.Parameters.Add("$name", SqliteType.Text);
        var stableParam = command.Parameters.Add("$stable", SqliteType.Text);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var hashParam = command.Parameters.Add("$hash", SqliteType.Text);
        var tokensParam = command.Parameters.Add("$tokens", SqliteType.Integer);
        var reducedParam = command.Parameters.Add("$reduced", SqliteType.Integer);
        var sigParam = command.Parameters.Add("$sig", SqliteType.Text);
        var outlineParam = command.Parameters.Add("$outline", SqliteType.Text);

        foreach (var chunk in chunks)
        {
            var fileId = await ResolveFileIdAsync(connection, transaction, chunk.FilePath, fileIds, cancellationToken);
            if (fileId is null)
                continue;

            idParam.Value = chunk.ChunkId;
            fileParam.Value = fileId.Value;
            symbolParam.Value = (object?)chunk.SymbolId ?? DBNull.Value;
            kindParam.Value = chunk.Kind;
            nameParam.Value = (object?)chunk.Name ?? DBNull.Value;
            stableParam.Value = chunk.StableKey;
            startParam.Value = chunk.StartLine;
            endParam.Value = chunk.EndLine;
            hashParam.Value = chunk.TextHash;
            tokensParam.Value = chunk.TokenEstimate;
            reducedParam.Value = chunk.ReducedTokenEstimate;
            sigParam.Value = (object?)chunk.Signature ?? DBNull.Value;
            outlineParam.Value = (object?)chunk.Outline ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
            var documentId = await UpsertSearchDocumentAsync(
                connection,
                transaction,
                chunk.ChunkId,
                fileId.Value,
                cancellationToken);
            indexedDocuments.Add(new FtsDocument(documentId, chunk));
        }

        await _fts.IndexChunksAsync(connection, transaction, indexedDocuments, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.UpsertEdgesAsync" />
    public async Task UpsertEdgesAsync(IReadOnlyList<SemanticEdgeRecord> edges, CancellationToken cancellationToken)
    {
        if (edges.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO edges(edge_id, from_node_id, to_node_id, edge_type, weight, confidence,
                                         evidence, evidence_file_id, evidence_start_line, evidence_end_line,
                                         metadata_json)
            VALUES($id, $from, $to, $type, $weight, $confidence, $evidence, $file, $start, $end, $meta);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var fromParam = command.Parameters.Add("$from", SqliteType.Text);
        var toParam = command.Parameters.Add("$to", SqliteType.Text);
        var typeParam = command.Parameters.Add("$type", SqliteType.Text);
        var weightParam = command.Parameters.Add("$weight", SqliteType.Real);
        var confidenceParam = command.Parameters.Add("$confidence", SqliteType.Real);
        var evidenceParam = command.Parameters.Add("$evidence", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var metaParam = command.Parameters.Add("$meta", SqliteType.Text);

        foreach (var edge in edges)
        {
            var evidenceFileId = await ResolveFileIdAsync(connection, transaction, edge.EvidenceFilePath, fileIds, cancellationToken);
            idParam.Value = BuildEdgeId(edge.FromNodeId, edge.ToNodeId, edge.EdgeType, evidenceFileId);
            fromParam.Value = edge.FromNodeId;
            toParam.Value = edge.ToNodeId;
            typeParam.Value = edge.EdgeType;
            weightParam.Value = edge.Weight;
            confidenceParam.Value = edge.Confidence;
            evidenceParam.Value = (object?)edge.Evidence ?? DBNull.Value;
            fileParam.Value = (object?)evidenceFileId ?? DBNull.Value;
            startParam.Value = (object?)edge.EvidenceStartLine ?? DBNull.Value;
            endParam.Value = (object?)edge.EvidenceEndLine ?? DBNull.Value;
            metaParam.Value = (object?)edge.MetadataJson ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.UpsertRoutesAsync" />
    public async Task UpsertRoutesAsync(IReadOnlyList<RouteRecord> routes, CancellationToken cancellationToken)
    {
        if (routes.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO routes(route_id, http_method, route_pattern, handler_symbol_id, file_id,
                                          start_line, end_line, source_kind, metadata_json)
            VALUES($id, $method, $pattern, $handler, $file, $start, $end, $source, $meta);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var methodParam = command.Parameters.Add("$method", SqliteType.Text);
        var patternParam = command.Parameters.Add("$pattern", SqliteType.Text);
        var handlerParam = command.Parameters.Add("$handler", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var sourceParam = command.Parameters.Add("$source", SqliteType.Text);
        var metaParam = command.Parameters.Add("$meta", SqliteType.Text);

        foreach (var route in routes)
        {
            var fileId = await ResolveFileIdAsync(connection, transaction, route.FilePath, fileIds, cancellationToken);
            if (fileId is null)
                continue;

            idParam.Value = route.RouteId;
            methodParam.Value = route.HttpMethod;
            patternParam.Value = route.RoutePattern;
            handlerParam.Value = (object?)route.HandlerSymbolId ?? DBNull.Value;
            fileParam.Value = fileId.Value;
            startParam.Value = route.StartLine;
            endParam.Value = route.EndLine;
            sourceParam.Value = route.SourceKind;
            metaParam.Value = (object?)route.MetadataJson ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.UpsertDiRegistrationsAsync" />
    public async Task UpsertDiRegistrationsAsync(IReadOnlyList<DiRegistrationRecord> registrations, CancellationToken cancellationToken)
    {
        if (registrations.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO di_registrations(registration_id, service_symbol_id, implementation_symbol_id,
                                                    service_name, implementation_name, lifetime, file_id,
                                                    start_line, end_line, registration_kind, confidence, evidence)
            VALUES($id, $service_symbol, $impl_symbol, $service, $impl, $lifetime, $file, $start, $end,
                   $kind, $confidence, $evidence);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var serviceSymbolParam = command.Parameters.Add("$service_symbol", SqliteType.Text);
        var implSymbolParam = command.Parameters.Add("$impl_symbol", SqliteType.Text);
        var serviceParam = command.Parameters.Add("$service", SqliteType.Text);
        var implParam = command.Parameters.Add("$impl", SqliteType.Text);
        var lifetimeParam = command.Parameters.Add("$lifetime", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var kindParam = command.Parameters.Add("$kind", SqliteType.Text);
        var confidenceParam = command.Parameters.Add("$confidence", SqliteType.Real);
        var evidenceParam = command.Parameters.Add("$evidence", SqliteType.Text);

        foreach (var registration in registrations)
        {
            var fileId = await ResolveFileIdAsync(connection, transaction, registration.FilePath, fileIds, cancellationToken);
            if (fileId is null)
                continue;

            idParam.Value = registration.RegistrationId;
            serviceSymbolParam.Value = (object?)registration.ServiceSymbolId ?? DBNull.Value;
            implSymbolParam.Value = (object?)registration.ImplementationSymbolId ?? DBNull.Value;
            serviceParam.Value = registration.ServiceName;
            implParam.Value = (object?)registration.ImplementationName ?? DBNull.Value;
            lifetimeParam.Value = registration.Lifetime;
            fileParam.Value = fileId.Value;
            startParam.Value = registration.StartLine;
            endParam.Value = registration.EndLine;
            kindParam.Value = registration.RegistrationKind;
            confidenceParam.Value = registration.Confidence;
            evidenceParam.Value = (object?)registration.Evidence ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.UpsertOptionsBindingsAsync" />
    public async Task UpsertOptionsBindingsAsync(IReadOnlyList<OptionsBindingRecord> bindings, CancellationToken cancellationToken)
    {
        if (bindings.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO options_bindings(binding_id, options_symbol_id, options_name, config_section,
                                                    file_id, start_line, end_line, binding_kind, confidence, evidence)
            VALUES($id, $symbol, $name, $section, $file, $start, $end, $kind, $confidence, $evidence);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var symbolParam = command.Parameters.Add("$symbol", SqliteType.Text);
        var nameParam = command.Parameters.Add("$name", SqliteType.Text);
        var sectionParam = command.Parameters.Add("$section", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var kindParam = command.Parameters.Add("$kind", SqliteType.Text);
        var confidenceParam = command.Parameters.Add("$confidence", SqliteType.Real);
        var evidenceParam = command.Parameters.Add("$evidence", SqliteType.Text);

        foreach (var binding in bindings)
        {
            var fileId = await ResolveFileIdAsync(connection, transaction, binding.FilePath, fileIds, cancellationToken);
            if (fileId is null)
                continue;

            idParam.Value = binding.BindingId;
            symbolParam.Value = (object?)binding.OptionsSymbolId ?? DBNull.Value;
            nameParam.Value = binding.OptionsName;
            sectionParam.Value = (object?)binding.ConfigSection ?? DBNull.Value;
            fileParam.Value = fileId.Value;
            startParam.Value = binding.StartLine;
            endParam.Value = binding.EndLine;
            kindParam.Value = binding.BindingKind;
            confidenceParam.Value = binding.Confidence;
            evidenceParam.Value = (object?)binding.Evidence ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.DeleteFileDataAsync" />
    public Task DeleteFileDataAsync(string normalizedPath, CancellationToken cancellationToken) =>
        DeleteFileCoreAsync(normalizedPath, removeFileRecord: false, cancellationToken);

    /// <inheritdoc cref="IWorkspaceIndexStore.DeleteFileAsync" />
    public Task DeleteFileAsync(string normalizedPath, CancellationToken cancellationToken) =>
        DeleteFileCoreAsync(normalizedPath, removeFileRecord: true, cancellationToken);

    private async Task DeleteFileCoreAsync(
        string normalizedPath,
        bool removeFileRecord,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);
        var fileId = await ResolveFileIdAsync(connection, transaction, normalizedPath, fileIds, cancellationToken);
        if (fileId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await _fts.DeleteForFileAsync(connection, transaction, fileId.Value, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = removeFileRecord
                ? """
                DELETE FROM edges WHERE evidence_file_id = $file;
                DELETE FROM chunks WHERE file_id = $file;
                DELETE FROM symbols WHERE file_id = $file;
                DELETE FROM routes WHERE file_id = $file;
                DELETE FROM di_registrations WHERE file_id = $file;
                DELETE FROM options_bindings WHERE file_id = $file;
                DELETE FROM nodes WHERE file_id = $file;
                DELETE FROM files WHERE file_id = $file;
                """
                : """
                DELETE FROM edges WHERE evidence_file_id = $file;
                DELETE FROM chunks WHERE file_id = $file;
                DELETE FROM symbols WHERE file_id = $file;
                DELETE FROM routes WHERE file_id = $file;
                DELETE FROM di_registrations WHERE file_id = $file;
                DELETE FROM options_bindings WHERE file_id = $file;
                DELETE FROM nodes WHERE file_id = $file;
                """;
            command.Parameters.AddWithValue("$file", fileId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.ClearFileDataAsync" />
    public async Task ClearFileDataAsync(
        IReadOnlyCollection<string> normalizedPaths,
        CancellationToken cancellationToken)
    {
        if (normalizedPaths.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await CreateAndPopulatePathTableAsync(
            connection,
            transaction,
            "target_fuse_files",
            normalizedPaths,
            cancellationToken);

        var fileIds = new List<long>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT file_id FROM files
                WHERE normalized_path IN (SELECT normalized_path FROM target_fuse_files);
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                fileIds.Add(reader.GetInt64(0));
        }

        foreach (var fileId in fileIds)
            await _fts.DeleteForFileAsync(connection, transaction, fileId, cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM edges WHERE evidence_file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path IN (SELECT normalized_path FROM target_fuse_files));
                DELETE FROM chunks WHERE file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path IN (SELECT normalized_path FROM target_fuse_files));
                DELETE FROM symbols WHERE file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path IN (SELECT normalized_path FROM target_fuse_files));
                DELETE FROM routes WHERE file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path IN (SELECT normalized_path FROM target_fuse_files));
                DELETE FROM di_registrations WHERE file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path IN (SELECT normalized_path FROM target_fuse_files));
                DELETE FROM options_bindings WHERE file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path IN (SELECT normalized_path FROM target_fuse_files));
                DELETE FROM nodes WHERE file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path IN (SELECT normalized_path FROM target_fuse_files));
                """;
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.PruneFilesAsync" />
    public async Task<int> PruneFilesAsync(
        IReadOnlyCollection<string> normalizedPaths,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await CreateAndPopulatePathTableAsync(
            connection,
            transaction,
            "current_fuse_files",
            normalizedPaths,
            cancellationToken);

        var stale = new List<long>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT file_id
                FROM files
                WHERE NOT EXISTS (
                    SELECT 1 FROM current_fuse_files current
                    WHERE current.normalized_path = files.normalized_path);
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                stale.Add(reader.GetInt64(0));
        }

        foreach (var fileId in stale)
            await _fts.DeleteForFileAsync(connection, transaction, fileId, cancellationToken);

        if (stale.Count > 0)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM edges WHERE evidence_file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path NOT IN (SELECT normalized_path FROM current_fuse_files));
                DELETE FROM chunks WHERE file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path NOT IN (SELECT normalized_path FROM current_fuse_files));
                DELETE FROM symbols WHERE file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path NOT IN (SELECT normalized_path FROM current_fuse_files));
                DELETE FROM routes WHERE file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path NOT IN (SELECT normalized_path FROM current_fuse_files));
                DELETE FROM di_registrations WHERE file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path NOT IN (SELECT normalized_path FROM current_fuse_files));
                DELETE FROM options_bindings WHERE file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path NOT IN (SELECT normalized_path FROM current_fuse_files));
                DELETE FROM nodes WHERE file_id IN (
                    SELECT file_id FROM files
                    WHERE normalized_path NOT IN (SELECT normalized_path FROM current_fuse_files));
                DELETE FROM files
                WHERE normalized_path NOT IN (SELECT normalized_path FROM current_fuse_files);
                """;
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return stale.Count;
    }

    private static async Task CreateAndPopulatePathTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        IReadOnlyCollection<string> normalizedPaths,
        CancellationToken cancellationToken)
    {
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = $"""
                CREATE TEMP TABLE IF NOT EXISTS {tableName}(normalized_path TEXT PRIMARY KEY);
                DELETE FROM {tableName};
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT OR IGNORE INTO {tableName}(normalized_path) VALUES($path);";
        var pathParameter = insert.Parameters.Add("$path", SqliteType.Text);
        foreach (var path in normalizedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pathParameter.Value = path;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.ListSymbolsAsync" />
    public async Task<IReadOnlyList<SymbolListItem>> ListSymbolsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.symbol_id, s.name, s.kind, s.fully_qualified_name, files.normalized_path,
                   s.start_line, s.is_public_api
            FROM symbols s
            JOIN files ON files.file_id = s.file_id
            ORDER BY s.is_public_api DESC, s.name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var items = new List<SymbolListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SymbolListItem(
                SymbolId: reader.GetString(0),
                Name: reader.GetString(1),
                Kind: reader.GetString(2),
                FullyQualifiedName: reader.GetString(3),
                FilePath: reader.GetString(4),
                StartLine: reader.GetInt32(5),
                IsPublicApi: reader.GetInt64(6) != 0));
        }

        return items;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.FindSymbolsByNameAsync" />
    public async Task<IReadOnlyList<SymbolListItem>> FindSymbolsByNameAsync(string nameFragment, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.symbol_id, s.name, s.kind, s.fully_qualified_name, files.normalized_path,
                   s.start_line, s.is_public_api
            FROM symbols s
            JOIN files ON files.file_id = s.file_id
            WHERE s.name LIKE '%' || $n || '%' COLLATE NOCASE
            ORDER BY s.is_public_api DESC, length(s.name), s.name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$n", nameFragment);
        command.Parameters.AddWithValue("$limit", limit);

        var items = new List<SymbolListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SymbolListItem(
                SymbolId: reader.GetString(0),
                Name: reader.GetString(1),
                Kind: reader.GetString(2),
                FullyQualifiedName: reader.GetString(3),
                FilePath: reader.GetString(4),
                StartLine: reader.GetInt32(5),
                IsPublicApi: reader.GetInt64(6) != 0));
        }

        return items;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetSignaturesByNamesAsync" />
    public async Task<IReadOnlyList<SymbolSignature>> GetSignaturesByNamesAsync(
        IReadOnlyCollection<string> names, int limitPerName, CancellationToken cancellationToken)
    {
        var distinct = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0)
            return [];

        var results = new List<SymbolSignature>();
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        foreach (var name in distinct)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.name, s.kind, s.fully_qualified_name, s.signature, s.accessibility, s.containing_type,
                       files.normalized_path, s.start_line, s.is_public_api
                FROM symbols s
                JOIN files ON files.file_id = s.file_id
                WHERE s.name = $n COLLATE NOCASE OR s.fully_qualified_name = $n COLLATE NOCASE
                ORDER BY s.is_public_api DESC, length(s.fully_qualified_name), s.fully_qualified_name COLLATE NOCASE
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$n", name);
            command.Parameters.AddWithValue("$limit", limitPerName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new SymbolSignature(
                    Name: reader.GetString(0),
                    Kind: reader.GetString(1),
                    FullyQualifiedName: reader.GetString(2),
                    Signature: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Accessibility: reader.IsDBNull(4) ? null : reader.GetString(4),
                    ContainingType: reader.IsDBNull(5) ? null : reader.GetString(5),
                    FilePath: reader.GetString(6),
                    StartLine: reader.GetInt32(7),
                    IsPublicApi: reader.GetInt64(8) != 0));
            }
        }

        return results;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetMembersOfTypeAsync" />
    public async Task<IReadOnlyList<SymbolSignature>> GetMembersOfTypeAsync(
        string typeName, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return [];

        var simple = typeName.Contains('.', StringComparison.Ordinal)
            ? typeName[(typeName.LastIndexOf('.') + 1)..]
            : typeName;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.name, s.kind, s.fully_qualified_name, s.signature, s.accessibility, s.containing_type,
                   files.normalized_path, s.start_line, s.is_public_api
            FROM symbols s
            JOIN files ON files.file_id = s.file_id
            WHERE s.containing_type = $full COLLATE NOCASE
               OR s.containing_type = $simple COLLATE NOCASE
               OR s.containing_type LIKE '%.' || $simple COLLATE NOCASE
            ORDER BY s.is_public_api DESC, s.name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$full", typeName);
        command.Parameters.AddWithValue("$simple", simple);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<SymbolSignature>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SymbolSignature(
                Name: reader.GetString(0),
                Kind: reader.GetString(1),
                FullyQualifiedName: reader.GetString(2),
                Signature: reader.IsDBNull(3) ? null : reader.GetString(3),
                Accessibility: reader.IsDBNull(4) ? null : reader.GetString(4),
                ContainingType: reader.IsDBNull(5) ? null : reader.GetString(5),
                FilePath: reader.GetString(6),
                StartLine: reader.GetInt32(7),
                IsPublicApi: reader.GetInt64(8) != 0));
        }

        return results;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.ListRoutesAsync" />
    public async Task<IReadOnlyList<RouteListItem>> ListRoutesAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.route_id, r.http_method, r.route_pattern, files.normalized_path, r.start_line
            FROM routes r
            JOIN files ON files.file_id = r.file_id
            ORDER BY r.route_pattern COLLATE NOCASE, r.http_method
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var items = new List<RouteListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new RouteListItem(
                RouteId: reader.GetString(0),
                HttpMethod: reader.GetString(1),
                RoutePattern: reader.GetString(2),
                FilePath: reader.GetString(3),
                StartLine: reader.GetInt32(4)));
        }

        return items;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetNodeAsync" />
    public async Task<NodeRecord?> GetNodeAsync(string nodeId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = NodeSelect + " WHERE n.node_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", nodeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNode(reader) : null;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.FindNodesByDisplayNameAsync" />
    public async Task<IReadOnlyList<NodeRecord>> FindNodesByDisplayNameAsync(string displayName, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = NodeSelect + " WHERE n.display_name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", displayName);
        return await ReadNodesAsync(command, cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetNodesByFileAsync" />
    public async Task<IReadOnlyList<NodeRecord>> GetNodesByFileAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = NodeSelect + " WHERE files.normalized_path = $p;";
        command.Parameters.AddWithValue("$p", normalizedPath);
        return await ReadNodesAsync(command, cancellationToken);
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetAllEdgesAsync" />
    public async Task<IReadOnlyList<SemanticEdgeRecord>> GetAllEdgesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT e.from_node_id, e.to_node_id, e.edge_type, e.weight, e.confidence, e.evidence, " +
            "files.normalized_path, e.evidence_start_line, e.evidence_end_line, e.metadata_json " +
            "FROM edges e LEFT JOIN files ON files.file_id = e.evidence_file_id;";

        var edges = new List<SemanticEdgeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            edges.Add(new SemanticEdgeRecord(
                FromNodeId: reader.GetString(0),
                ToNodeId: reader.GetString(1),
                EdgeType: reader.GetString(2),
                Weight: reader.GetDouble(3),
                Confidence: reader.GetDouble(4),
                Evidence: reader.IsDBNull(5) ? null : reader.GetString(5),
                EvidenceFilePath: reader.IsDBNull(6) ? null : reader.GetString(6),
                EvidenceStartLine: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                EvidenceEndLine: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                MetadataJson: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return edges;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetOutgoingEdgesAsync" />
    public Task<IReadOnlyList<SemanticEdgeRecord>> GetOutgoingEdgesAsync(string nodeId, CancellationToken cancellationToken) =>
        GetEdgesAsync("from_node_id", nodeId, cancellationToken);

    /// <inheritdoc cref="IWorkspaceIndexStore.GetIncomingEdgesAsync" />
    public Task<IReadOnlyList<SemanticEdgeRecord>> GetIncomingEdgesAsync(string nodeId, CancellationToken cancellationToken) =>
        GetEdgesAsync("to_node_id", nodeId, cancellationToken);

    /// <inheritdoc cref="IWorkspaceIndexStore.FindFilesByPathAsync" />
    public async Task<IReadOnlyList<FileListItem>> FindFilesByPathAsync(string fragment, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT path, normalized_path, extension, is_test, is_generated FROM files " +
            "WHERE normalized_path LIKE '%' || $f || '%' COLLATE NOCASE ORDER BY length(normalized_path) LIMIT $limit;";
        command.Parameters.AddWithValue("$f", fragment);
        command.Parameters.AddWithValue("$limit", limit);

        var files = new List<FileListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new FileListItem(
                Path: reader.GetString(0),
                NormalizedPath: reader.GetString(1),
                Extension: reader.GetString(2),
                IsTest: reader.GetInt64(3) != 0,
                IsGenerated: reader.GetInt64(4) != 0));
        }

        return files;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetFileTokenEstimateAsync" />
    public async Task<int> GetFileTokenEstimateAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(SUM(c.reduced_token_estimate), 0) FROM chunks c " +
            "JOIN files f ON f.file_id = c.file_id WHERE f.normalized_path = $p;";
        command.Parameters.AddWithValue("$p", normalizedPath);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value ? (int)value : 0;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetFileTokenEstimatesAsync" />
    public async Task<IReadOnlyDictionary<string, int>> GetFileTokenEstimatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT files.normalized_path, COALESCE(SUM(c.reduced_token_estimate), 0) " +
            "FROM files LEFT JOIN chunks c ON c.file_id = files.file_id GROUP BY files.file_id, files.normalized_path;";

        var estimates = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            estimates[reader.GetString(0)] = (int)reader.GetInt64(1);
        return estimates;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetContentHashesAsync" />
    public async Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(
        IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = normalizedPaths.Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
            return result;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = new string[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            placeholders[i] = "$p" + i;
            command.Parameters.AddWithValue(placeholders[i], names[i]);
        }

        command.CommandText =
            $"SELECT normalized_path, content_hash FROM files WHERE normalized_path IN ({string.Join(',', placeholders)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetString(0)] = reader.GetString(1);

        return result;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetAllFileHashesAsync" />
    public async Task<IReadOnlyDictionary<string, string>> GetAllFileHashesAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT normalized_path, content_hash FROM files;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetString(0)] = reader.GetString(1);

        return result;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetFilesByLanguageAsync" />
    public async Task<IReadOnlyList<string>> GetFilesByLanguageAsync(string language, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT normalized_path FROM files WHERE language = $lang ORDER BY normalized_path;";
        command.Parameters.AddWithValue("$lang", language);

        var paths = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            paths.Add(reader.GetString(0));
        return paths;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetRouteCountAsync" />
    public async Task<int> GetRouteCountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM routes;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value ? (int)value : 0;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetLanguageCountsAsync" />
    public async Task<IReadOnlyList<LanguageCount>> GetLanguageCountsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(NULLIF(language, ''), 'unknown') AS lang, COUNT(*) AS n FROM files GROUP BY lang ORDER BY n DESC, lang;";

        var counts = new List<LanguageCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            counts.Add(new LanguageCount(reader.GetString(0), (int)reader.GetInt64(1)));
        return counts;
    }

    /// <inheritdoc cref="IWorkspaceIndexStore.GetFileDependencyEdgesAsync" />
    public async Task<IReadOnlyList<FileDependencyEdge>> GetFileDependencyEdgesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ff.normalized_path, tf.normalized_path, e.edge_type
            FROM edges e
            JOIN nodes fn ON fn.node_id = e.from_node_id
            JOIN nodes tn ON tn.node_id = e.to_node_id
            JOIN files ff ON ff.file_id = fn.file_id
            JOIN files tf ON tf.file_id = tn.file_id
            WHERE fn.file_id IS NOT NULL AND tn.file_id IS NOT NULL AND ff.file_id <> tf.file_id;
            """;

        var edges = new List<FileDependencyEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            edges.Add(new FileDependencyEdge(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return edges;
    }

    /// <summary>
    ///     Derives a stable edge id from edge components.
    /// </summary>
    /// <param name="fromNodeId">The source node id.</param>
    /// <param name="toNodeId">The target node id.</param>
    /// <param name="edgeType">The edge type.</param>
    /// <param name="evidenceFileId">The optional evidence file id.</param>
    /// <returns>A stable edge id string.</returns>
    public static string BuildEdgeId(string fromNodeId, string toNodeId, string edgeType, long? evidenceFileId)
    {
        var key = $"{fromNodeId}\u0001{toNodeId}\u0001{edgeType}\u0001{evidenceFileId?.ToString() ?? string.Empty}";
        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(key));
        return "edge:" + hash.ToString("x16");
    }

    private async Task<IReadOnlyList<SemanticEdgeRecord>> GetEdgesAsync(string column, string nodeId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT e.from_node_id, e.to_node_id, e.edge_type, e.weight, e.confidence, e.evidence, " +
            "files.normalized_path, e.evidence_start_line, e.evidence_end_line, e.metadata_json " +
            "FROM edges e LEFT JOIN files ON files.file_id = e.evidence_file_id " +
            $"WHERE e.{column} = $id;";
        command.Parameters.AddWithValue("$id", nodeId);

        var edges = new List<SemanticEdgeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            edges.Add(new SemanticEdgeRecord(
                FromNodeId: reader.GetString(0),
                ToNodeId: reader.GetString(1),
                EdgeType: reader.GetString(2),
                Weight: reader.GetDouble(3),
                Confidence: reader.GetDouble(4),
                Evidence: reader.IsDBNull(5) ? null : reader.GetString(5),
                EvidenceFilePath: reader.IsDBNull(6) ? null : reader.GetString(6),
                EvidenceStartLine: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                EvidenceEndLine: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                MetadataJson: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return edges;
    }

    private static async Task<IReadOnlyList<NodeRecord>> ReadNodesAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var nodes = new List<NodeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            nodes.Add(ReadNode(reader));
        return nodes;
    }

    private static NodeRecord ReadNode(SqliteDataReader reader) => new(
        NodeId: reader.GetString(0),
        Kind: reader.GetString(1),
        DisplayName: reader.GetString(2),
        StableKey: reader.GetString(3),
        FilePath: reader.IsDBNull(4) ? null : reader.GetString(4),
        SymbolId: reader.IsDBNull(5) ? null : reader.GetString(5),
        StartLine: reader.IsDBNull(6) ? null : reader.GetInt32(6),
        EndLine: reader.IsDBNull(7) ? null : reader.GetInt32(7),
        Signature: reader.IsDBNull(8) ? null : reader.GetString(8),
        MetadataJson: reader.IsDBNull(9) ? null : reader.GetString(9));

    private static async Task<long?> ResolveFileIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? normalizedPath,
        Dictionary<string, long?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(normalizedPath))
            return null;
        if (cache.TryGetValue(normalizedPath, out var cached))
            return cached;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT file_id FROM files WHERE normalized_path = $p LIMIT 1;";
        command.Parameters.AddWithValue("$p", normalizedPath);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var id = result is long value ? value : (long?)null;
        cache[normalizedPath] = id;
        return id;
    }

    private static async Task<long?> ResolveProjectIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? projectPath,
        Dictionary<string, long?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(projectPath))
            return null;
        if (cache.TryGetValue(projectPath, out var cached))
            return cached;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT project_id FROM projects WHERE path = $p LIMIT 1;";
        command.Parameters.AddWithValue("$p", projectPath);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var id = result is long value ? value : (long?)null;
        cache[projectPath] = id;
        return id;
    }

    private static async Task<long> UpsertSearchDocumentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string chunkId,
        long fileId,
        CancellationToken cancellationToken)
    {
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT document_id FROM search_documents WHERE chunk_id = $chunk LIMIT 1;";
            find.Parameters.AddWithValue("$chunk", chunkId);
            var existing = await find.ExecuteScalarAsync(cancellationToken);
            if (existing is long documentId)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE search_documents SET file_id = $file WHERE document_id = $id;";
                update.Parameters.AddWithValue("$file", fileId);
                update.Parameters.AddWithValue("$id", documentId);
                await update.ExecuteNonQueryAsync(cancellationToken);
                return documentId;
            }
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO search_documents(chunk_id, file_id) VALUES($chunk, $file);";
        insert.Parameters.AddWithValue("$chunk", chunkId);
        insert.Parameters.AddWithValue("$file", fileId);
        await insert.ExecuteNonQueryAsync(cancellationToken);

        await using var identity = connection.CreateCommand();
        identity.Transaction = transaction;
        identity.CommandText = "SELECT last_insert_rowid();";
        var inserted = await identity.ExecuteScalarAsync(cancellationToken);
        return inserted is long insertedDocumentId
            ? insertedDocumentId
            : throw new InvalidOperationException("search document insert did not return a row id.");
    }

    private static string ToDetailValue(IndexDetailLevel detailLevel) => detailLevel switch
    {
        IndexDetailLevel.Full => "full",
        IndexDetailLevel.Declarations => "declarations",
        IndexDetailLevel.InventoryOnly => "inventory_only",
        _ => throw new ArgumentOutOfRangeException(nameof(detailLevel), detailLevel, "unknown index detail level"),
    };
}
