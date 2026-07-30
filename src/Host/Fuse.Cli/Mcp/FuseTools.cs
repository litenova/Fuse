using System.ComponentModel;
using System.Text;
using Fuse.Cli;
using Fuse.Cli.Services;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Indexing;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP tool definitions for Fuse, exposed to AI agents through the Model Context Protocol server.
/// </summary>
/// <remarks>
///     Each method maps to an MCP tool whose name is set by <see cref="McpServerToolAttribute" /> (for example
///     <c>fuse_find</c>). The eight loop tools (workspace, find, context, impact, check, test, refactor, review)
///     plus <c>fuse_reduce</c> work over the persistent semantic index; read tools build the index on first use.
///     Tools return errors as descriptive strings rather than throwing.
/// </remarks>
[McpServerToolType]
public sealed partial class FuseTools
{
    /// <summary>
    ///     Prints a map of the indexed workspace (symbols, routes, counts).
    /// </summary>
    /// <param name="indexer">The semantic indexer (used to build the index on first use).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="detail">The detail to include: symbols, routes, or all.</param>
    /// <param name="maxRows">The maximum rows per section.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The workspace map.</returns>
    // Reached through fuse_workspace (action=map); kept as an internal helper the workspace tool calls.
    public static async Task<string> FuseMapAsync(
        SemanticIndexer indexer,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Detail to include: symbols, routes, or all. Default: all.")] string detail = "all",
        [Description("Maximum rows per section.")] int maxRows = 200,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        var toolRuntime = ResolveRuntime(runtime, indexer);
        await using var store = await OpenIndexedAsync(toolRuntime, indexer, path, cancellationToken);
        var renderer = new WorkspaceMapRenderer(store);
        return await renderer.RenderAsync(ParseDetail(detail), maxRows, cancellationToken);
    }

    /// <summary>
    ///     The workspace status and lifecycle tool: the single entry point for the state of the workspace and
    ///     its index. <c>action=status</c> reports the index mode, verify grade, and freshness; <c>index</c> builds
    ///     or refreshes the index; <c>map</c> prints the symbol and route map; <c>doctor</c> diagnoses the semantic
    ///     load per project; <c>apply</c> is the one explicit tree-write path (Decision D2), a dry run unless
    ///     <c>write=true</c>.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="jobs">The daemon-aware lifecycle client for index start, status, and cancellation.</param>
    /// <param name="action">The action: status, index, cancel, map, doctor, or apply.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="detail">For the map action: the detail to include (symbols, routes, all).</param>
    /// <param name="maxRows">For the map action: the maximum rows per section.</param>
    /// <param name="file">For the apply action: the repository-relative file to write.</param>
    /// <param name="content">For the apply action: the complete replacement content.</param>
    /// <param name="write">For the apply action: whether to write instead of returning a dry-run report.</param>
    /// <param name="expectedHash">For the apply action: the SHA-256 hash of the source content used to derive the edit.</param>
    /// <param name="refresh">For the doctor action: whether to force a live MSBuild diagnosis.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The action's result, or a descriptive error.</returns>
    [McpServerTool(Name = "fuse_workspace", ReadOnly = false)]
    [Description("Workspace status and lifecycle (the loop's first stop). action=status (default): index mode, verification grade, freshness, and active job. action=index: start or join the syntax index job. action=cancel: stop the active index job. action=map: symbols, routes, and counts. action=doctor: daemon, configuration, storage, compiler-target, and job diagnostics. action=apply: write a proposed single-file edit (file + content) to the working tree; it is a dry run unless write=true and refuses paths outside the workspace root.")]
    public static Task<string> FuseWorkspaceAsync(
        SemanticIndexer indexer,
        IndexJobClient jobs,
        [Description("The action: status, index, cancel, map, doctor, or apply.")] string action = "status",
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("For the map action: detail to include (symbols, routes, all).")] string detail = "all",
        [Description("For the map action: maximum rows per section.")] int maxRows = 200,
        [Description("For the apply action: the repo-relative file to write.")] string file = "",
        [Description("For the apply action: the full new content to write to that file.")] string content = "",
        [Description("For the apply action: actually write (otherwise a dry run reports the change without writing).")] bool write = false,
        [Description("For the apply action: the SHA-256 (hex) of the file content this edit was derived from. When set, apply refuses if the file changed since (a concurrent edit), rather than clobbering it.")] string expectedHash = "",
        [Description("For the doctor action: force a live MSBuild load diagnosis instead of reporting the diagnosis stamped in the warm index (R43). Default false reports from the index in sub-second time when it is present.")] bool refresh = false,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        ExecuteReadMcpAsync(() => FuseWorkspaceCoreAsync(
            ResolveRuntime(runtime, indexer), indexer, jobs, action, path, detail, maxRows, file, content, write, expectedHash, refresh, cancellationToken));

    private static async Task<string> FuseWorkspaceCoreAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        IndexJobClient jobs,
        string action,
        string path,
        string detail,
        int maxRows,
        string file,
        string content,
        bool write,
        string expectedHash,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        switch (action.Trim().ToLowerInvariant())
        {
            case "index":
                return await StartIndexJobAsync(jobs, root, cancellationToken);
            case "cancel":
                return await CancelIndexJobAsync(jobs, root, cancellationToken);
            case "map":
                return await FuseMapAsync(indexer, root, detail, maxRows, cancellationToken, runtime);
            case "doctor":
                return await WorkspaceDoctorAsync(runtime, indexer, jobs, root, refresh, cancellationToken);
            case "apply":
                return await WorkspaceApplyAsync(root, file, content, write, expectedHash, cancellationToken);
            case "status":
            case "":
                return await WorkspaceStatusAsync(runtime, jobs, root, cancellationToken);
            default:
                return FuseOperationalErrors.Format(
                    FuseOperationalErrors.ValidationErrorPrefix,
                    $"unknown workspace action '{action}'. Use status, index, cancel, map, doctor, or apply.");
        }
    }

    private static async Task<string> StartIndexJobAsync(
        IndexJobClient jobs,
        string root,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            return FuseOperationalErrors.FormatWorkspaceNotFound(root);

        var start = await jobs.StartAsync(
            new IndexJobRequest(root, IndexDepth.Syntax, Force: false, CaptureBundlePath: null),
            cancellationToken);
        return FormatJobSnapshot(start.Result.Snapshot, start.Result.Joined, start.UsesDaemon);
    }

    private static async Task<string> CancelIndexJobAsync(
        IndexJobClient jobs,
        string root,
        CancellationToken cancellationToken)
    {
        var result = await jobs.CancelAsync(root, cancellationToken);
        return result.Snapshot is null
            ? $"workspace: {root}{Environment.NewLine}index job: none"
            : FormatJobSnapshot(result.Snapshot, joined: null, result.UsesDaemon);
    }

    // The one explicit tree-write path (Decision D2): write a single file's proposed content, guarded so the
    // server never writes outside the workspace and never writes silently. A dry run (write=false, the default)
    // reports what would change; write=true performs the one write. The path is refused if it escapes the root.
    // R36: the write is atomic (temp file + rename) and conflict-checked (refuses when the file changed since the
    // edit was derived from, when expectedHash is supplied), so it never clobbers a concurrent edit or leaves a
    // partially written file.
    private static async Task<string> WorkspaceApplyAsync(
        string path, string file, string content, bool write, string expectedHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file))
            return FuseOperationalErrors.Format(
                FuseOperationalErrors.ValidationErrorPrefix,
                "apply needs a file (the repo-relative path to write) and content.");

        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        if (!Directory.Exists(root))
            return FuseOperationalErrors.FormatWorkspaceNotFound(root);

        // Resolve and confine: GetFullPath normalizes any ../ segments; the result must stay under the root, or a
        // crafted path could escape the workspace. This is the guard the D2 write path exists to enforce.
        var full = Path.GetFullPath(Path.Combine(root, file));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            return $"Error: refusing to write '{file}': it resolves outside the workspace root. The apply path only writes inside {root}.";

        var exists = File.Exists(full);

        // R36: conflict check. When the caller supplies the hash of the content the edit was derived from, refuse
        // (rather than clobber) if the file on disk no longer matches - a concurrent edit landed since the read.
        if (!string.IsNullOrWhiteSpace(expectedHash))
        {
            var currentHash = exists ? await ComputeFileHashAsync(full, cancellationToken) : null;
            if (!string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                var found = currentHash is null ? "the file does not exist" : $"found {Short(currentHash)}";
                return FuseOperationalErrors.Format(
                    FuseOperationalErrors.ValidationErrorPrefix,
                    $"conflict: {file} changed since the edit was derived (expected {Short(expectedHash)}, {found}). Re-read the file and re-derive the edit, then apply again.");
            }
        }

        if (!write)
            return $"dry run (no write): would {(exists ? "overwrite" : "create")} {file} ({content.Length} chars). Re-run with write=true to apply.";

        var dir = Path.GetDirectoryName(full);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        // R36: atomic write. Write to a temp file on the same volume, then rename into place, so a reader never
        // sees a half-written file and an interrupted write leaves the original intact.
        var tempPath = full + ".fuse-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
            File.Move(tempPath, full, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }

        return $"applied: {(exists ? "overwrote" : "created")} {file} ({content.Length} chars) in the working tree (atomic write).";
    }

    private static async Task<string> ComputeFileHashAsync(string fullPath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Status never creates or refreshes the index. It is the lifecycle probe agents use before broad discovery,
    // so it must reveal a cold workspace and any active shared job without racing a daemon-owned writer.
    private static async Task<string> WorkspaceStatusAsync(
        FuseMcpRuntime runtime,
        IndexJobClient jobs,
        string path,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        var job = await jobs.StatusAsync(root, cancellationToken);
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
            return AppendJobSnapshot(
                await BuildFastStatusOutputAsync(root, store: null, state: null, runtime.ResidentWorkspaces, cancellationToken),
                job.Snapshot,
                job.UsesDaemon);

        await using var store = new WorkspaceIndexStore(databasePath);
        if (await store.OpenForReadAsync(cancellationToken) is not WorkspaceIndexReadOpenStatus.Ready)
            return AppendJobSnapshot(
                await BuildFastStatusOutputAsync(root, store: null, state: null, runtime.ResidentWorkspaces, cancellationToken),
                job.Snapshot,
                job.UsesDaemon);

        var state = await store.GetStateAsync(cancellationToken);
        return AppendJobSnapshot(
            await BuildFastStatusOutputAsync(root, store, state, runtime.ResidentWorkspaces, cancellationToken),
            job.Snapshot,
            job.UsesDaemon);
    }

    // The doctor action: the per-project semantic-load diagnosis, so a downgrade names its reason per project.
    // The summary header uses the same fast read-only meta path as status (R16). R43: the diagnosis is served from
    // the diagnosis stamped in the warm index (sub-second, no MSBuild load); a live load runs only when refresh is
    // requested or no stamp is present.
    private static async Task<string> WorkspaceDoctorAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        IndexJobClient jobs,
        string path,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);

        var builder = new StringBuilder();
        builder.AppendLine(await BuildFastDoctorSummaryHeaderAsync(root, runtime.ResidentWorkspaces, cancellationToken));

        var persisted = refresh ? null : await TryReadPersistedDiagnosisAsync(root, cancellationToken);
        builder.AppendLine($"workspace: {root}");
        if (persisted is not null)
        {
            // R43: reported from the index stamp, so doctor is sub-second and reflects what was actually indexed.
            builder.AppendLine("diagnosis source: warm index (stamped at index time; pass refresh=true for a live load)");
            builder.AppendLine($"load tier: {persisted.Tier}");
            builder.AppendLine($"selected solution: {persisted.SelectedSolution ?? "none (syntax-only)"}");
            if (persisted.SelectionNote is not null)
                builder.AppendLine($"WARNING: {persisted.SelectionNote}");
            builder.AppendLine($"projects loaded: {persisted.ProjectsLoaded}/{persisted.ProjectsTotal}");
            if (persisted.Projects.Count == 0)
                builder.AppendLine("no projects: the workspace has no solution or project, or none opened; indexing is syntax-only.");
            else
                foreach (var project in persisted.Projects)
                    builder.AppendLine($"  {project.Name}: {(project.Loaded ? "loaded" : "not loaded")} - {project.Reason}");
        }
        else
        {
            var diagnosis = await indexer.DiagnoseLoadAsync(root, cancellationToken);
            builder.AppendLine($"diagnosis source: live MSBuild load{(refresh ? " (refresh=true)" : " (no index stamp yet)")}");
            builder.AppendLine($"load tier: {diagnosis.Tier}");
            builder.AppendLine($"selected solution: {diagnosis.SelectedSolution ?? "none (syntax-only)"}");
            if (diagnosis.SelectionNote is not null)
                builder.AppendLine($"WARNING: {diagnosis.SelectionNote}");
            builder.AppendLine($"projects loaded: {diagnosis.ProjectsLoaded}/{diagnosis.ProjectsTotal}");
            if (diagnosis.Projects.Count == 0)
                builder.AppendLine("no projects: the workspace has no solution or project, or none opened; indexing is syntax-only.");
            else
                foreach (var project in diagnosis.Projects)
                    builder.AppendLine($"  {project.Name}: {(project.Loaded ? "loaded" : "not loaded")} - {project.Reason}");
        }

        // R31: report the store's integrity so a self-inconsistent index (which is never served ready) is visible.
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (File.Exists(databasePath))
        {
            // R37: store health - size on disk and when it was last written - so a stale or bloated store is visible.
            var dbInfo = new FileInfo(databasePath);
            builder.AppendLine($"store size: {dbInfo.Length / 1024} KB; last written: {dbInfo.LastWriteTimeUtc:O}");
            try
            {
                await using var integrityStore = new WorkspaceIndexStore(databasePath);
                if (await integrityStore.OpenForReadAsync(cancellationToken) is WorkspaceIndexReadOpenStatus.Ready)
                {
                    var integrityState = await integrityStore.GetStateAsync(cancellationToken);
                    builder.AppendLine($"index integrity: {IndexIntegrity.Check(integrityState).Summary()}");
                    // R35: surface files the scanner could not read, so a coverage gap is visible.
                    var skipped = await integrityStore.GetMetaAsync(WorkspaceIndexStore.SkippedFilesMetaKey, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(skipped) && skipped != "0")
                        builder.AppendLine($"skipped files: {skipped}");
                    var detailLimited = await integrityStore.GetMetaAsync(WorkspaceIndexStore.DetailLimitedFilesMetaKey, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(detailLimited) && detailLimited != "0")
                        builder.AppendLine($"detail-limited files: {detailLimited}");
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
            }
        }

        // R37: degraded-state counts this process has served, so a fallback or not-ready read is never silent.
        var degraded = new[]
        {
            (DegradedStateKind.IndexBusy, "index_busy"),
            (DegradedStateKind.IndexRebuilding, "index_rebuilding"),
            (DegradedStateKind.IntegrityFailed, "integrity_failed"),
            (DegradedStateKind.Deferred, "deferred"),
            (DegradedStateKind.LexicalFallback, "lexical_fallback"),
            (DegradedStateKind.VerifyAbstained, "verify_abstained"),
        };
        var degradedSummary = degraded
            .Where(d => FuseMetrics.GetDegradedCount(d.Item1) > 0)
            .Select(d => $"{d.Item2}={FuseMetrics.GetDegradedCount(d.Item1)}")
            .ToList();
        builder.AppendLine($"degraded states this session: {(degradedSummary.Count == 0 ? "none" : string.Join(", ", degradedSummary))}");

        // R28: list the running fuse host daemons with their served root and version, so a stale or mismatched
        // daemon (accumulated across respawns or left after an upgrade) is visible rather than an invisible orphan.
        var daemons = Fuse.Cli.Rpc.DaemonRegistry.List();
        builder.AppendLine($"running daemons: {daemons.Count}");
        foreach (var daemon in daemons)
            builder.AppendLine($"  PID {daemon.ProcessId} (fuse host {daemon.Version}) serving {daemon.Root}");

        var job = await jobs.StatusAsync(root, cancellationToken);
        return AppendJobSnapshot(builder.ToString().TrimEnd(), job.Snapshot, job.UsesDaemon);
    }

    private static string AppendJobSnapshot(string output, IndexJobSnapshot? snapshot, bool usesDaemon)
    {
        if (snapshot is null)
            return output + Environment.NewLine + "index job: none";

        return output + Environment.NewLine + FormatJobSnapshot(snapshot, joined: null, usesDaemon);
    }

    private static string FormatJobSnapshot(IndexJobSnapshot snapshot, bool? joined, bool usesDaemon)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"index job: {snapshot.JobId}");
        builder.AppendLine($"index job owner: {(usesDaemon ? "daemon" : "in-process")}");
        if (joined is not null)
            builder.AppendLine($"index job joined: {joined.Value.ToString().ToLowerInvariant()}");
        builder.AppendLine($"index job state: {snapshot.State}");
        builder.AppendLine($"index job phase: {snapshot.Phase} ({snapshot.PhaseNumber}/{snapshot.PhaseCount})");
        builder.AppendLine(snapshot.TotalUnits is null
            ? $"index job progress: {snapshot.CompletedUnits} units"
            : $"index job progress: {snapshot.CompletedUnits}/{snapshot.TotalUnits} ({snapshot.PhasePercent ?? 0:F0}%)");
        if (!string.IsNullOrWhiteSpace(snapshot.CurrentItem))
            builder.AppendLine($"index job current item: {snapshot.CurrentItem}");
        builder.AppendLine($"index storage: database {snapshot.Storage.DatabaseBytes} bytes, WAL {snapshot.Storage.WalBytes} bytes, total {snapshot.Storage.TotalFuseBytes} bytes");
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorCode))
            builder.AppendLine($"index job error: {snapshot.ErrorCode}: {snapshot.ErrorMessage}");
        if (snapshot.State is IndexJobState.Queued or IndexJobState.Running or IndexJobState.Cancelling)
            builder.AppendLine("next_action: call fuse_workspace with action=status for progress, or action=cancel to stop the job.");
        return builder.ToString().TrimEnd();
    }

    // R43: read the load diagnosis stamped into index_meta at index time, so doctor reports the tier and per-project
    // reasons without a live MSBuild load. Null when there is no stamp; the caller falls back to a live load.
    private static Task<Fuse.Semantics.PersistedLoadDiagnosis?> TryReadPersistedDiagnosisAsync(
        string root, CancellationToken cancellationToken) =>
        Fuse.Cli.Services.PersistedDiagnosisReader.TryReadAsync(root, cancellationToken);

    /// <summary>
    ///     Exact lookup over the index: symbols by name, files by path, and chunks by full-text.
    /// </summary>
    /// <param name="indexer">The semantic indexer (used to build the index on first use).</param>
    /// <param name="changeSource">The git change source for task localization and review-aware lookup.</param>
    /// <param name="query">The name, path fragment, or text to find.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="kind">Restrict to one kind: symbol, path, text, or all.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The matches grouped by kind.</returns>
    [McpServerTool(Name = "fuse_find", ReadOnly = true)]
    [Description("The find union: locate what a task needs by kind. Exact lookup - kind=symbol (by name), path (by fragment), text (full-text), or all. Wiring - kind=service, request, route, or config resolves the query to its implementation/handler/action/options. kind=signatures returns the query symbol's exact signature. kind=neighbors returns the query symbol's callers and implementers. kind=task ranks candidate files for the query with the graded refuse-and-route contract. Use instead of broad grep when the name, wiring, or task is known.")]
    public static Task<string> FuseFindAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        [Description("The name, path fragment, text, wiring identifier, or task to find.")] string query,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The kind: symbol, path, text, all (exact); service, request, route, config (wiring); signatures; neighbors; task.")] string kind = "all",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        ExecuteReadMcpAsync(() => FuseFindCoreAsync(
            ResolveRuntime(runtime, indexer), indexer, changeSource, query, path, kind, cancellationToken));

    private static async Task<string> FuseFindCoreAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        IChangeSource changeSource,
        string query,
        string path,
        string kind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return FuseOperationalErrors.Format(FuseOperationalErrors.ValidationErrorPrefix, "provide a query to find.");

        var normalizedKind = kind.Trim().ToLowerInvariant();

        // A wiring, signatures, neighbors, or task kind routes to the specialized engine logic, keyed by the query.
        switch (normalizedKind)
        {
            case "service":
                return await FuseResolveAsync(indexer, path, service: query, cancellationToken: cancellationToken, runtime: runtime);
            case "request":
                return await FuseResolveAsync(indexer, path, request: query, cancellationToken: cancellationToken, runtime: runtime);
            case "route":
                return await FuseResolveAsync(indexer, path, route: query, cancellationToken: cancellationToken, runtime: runtime);
            case "config":
                return await FuseResolveAsync(indexer, path, config: query, cancellationToken: cancellationToken, runtime: runtime);
            case "signatures":
                return await FuseSignaturesAsync(indexer, [query], path, cancellationToken: cancellationToken, runtime: runtime);
            case "neighbors":
                return await FuseNeighborsAsync(indexer, path, symbol: query, cancellationToken: cancellationToken, runtime: runtime);
            case "task":
                {
                    var taskRoot = WorkspacePathResolver.ResolveRepositoryRoot(path);
                    await using var taskStore = await OpenIndexedAsync(runtime, indexer, path, cancellationToken);
                    if (!taskStore.FullTextSearchAvailable)
                    {
                        return AvailabilityHeaderHelpers.FormatTaskLocalizationFtsRefusal(
                            await OracleAvailabilityHeaderAsync(taskStore, taskRoot, cancellationToken, residentWorkspaces: runtime.ResidentWorkspaces));
                    }

                    return await FuseLocalizeAsync(indexer, changeSource, path, task: query, cancellationToken: cancellationToken, runtime: runtime);
                }
        }

        // R30: the opt-in inline lexical fallback. When the index is not semantic-ready (a cold/building/rebuilding
        // store makes OpenIndexedAsync signal a deferral) and FUSE_LEXICAL_FALLBACK is on, serve a scoped, ranked
        // raw-text result graded lexical-fallback instead of the bare deferral signal - for fuse-only/CLI setups
        // with no native search to defer to. Default (flag off): the deferral signal is returned as usual.
        WorkspaceIndexStore store;
        try
        {
            store = await OpenIndexedAsync(runtime, indexer, path, cancellationToken);
        }
        catch (IndexBlockedReadException) when (LexicalFallback.IsEnabled())
        {
            FuseMetrics.RecordDegraded(DegradedStateKind.LexicalFallback);
            return await LexicalFallback.SearchAsync(WorkspacePathResolver.ResolveRepositoryRoot(path), query, 50, cancellationToken);
        }

        await using (store)
        {
            var builder = new StringBuilder();

            if (normalizedKind is "all" or "symbol")
            {
                var symbols = await store.FindSymbolsByNameAsync(query, 50, cancellationToken);
                builder.AppendLine($"symbols ({symbols.Count}):");
                foreach (var symbol in symbols)
                    builder.AppendLine($"  {symbol.Kind} {symbol.FullyQualifiedName}  ({symbol.FilePath}:{symbol.StartLine})");
            }

            if (normalizedKind is "all" or "path")
            {
                var files = await store.FindFilesByPathAsync(query, 50, cancellationToken);
                builder.AppendLine($"paths ({files.Count}):");
                foreach (var file in files)
                    builder.AppendLine($"  {file.NormalizedPath}");
            }

            if (normalizedKind is "all" or "text")
            {
                var hits = await store.SearchAsync(new SearchQuery(query, 50), cancellationToken);
                builder.AppendLine($"text ({hits.Count}):");
                foreach (var hit in hits)
                    builder.AppendLine($"  {hit.Name ?? hit.Kind}  ({hit.FilePath}:{hit.StartLine})");
            }

            return builder.ToString();
        }
    }

    /// <summary>
    ///     Compacts a specific set of files (or raw content) without collecting a whole directory.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator.</param>
    /// <param name="templateRegistry">The project template registry.</param>
    /// <param name="path">Base directory for resolving relative file paths.</param>
    /// <param name="files">Explicit file paths to reduce.</param>
    /// <param name="content">Raw content to reduce instead of files.</param>
    /// <param name="extension">The extension selecting the reducer for content.</param>
    /// <param name="level">The reduction level.</param>
    /// <param name="maxTokens">The token ceiling, or zero for none.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>The reduced output, or a descriptive error.</returns>
    [McpServerTool(Name = "fuse_reduce", ReadOnly = true)]
    [Description("Compact a specific set of files (or raw content) by running Fuse's reduction, without collecting a whole directory. Pass `files` or `content` (+ `extension`).")]
    public static Task<string> FuseReduceAsync(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        [Description("Base directory for resolving relative file paths. Ignored in content mode.")] string path = ".",
        [Description("File paths to reduce, absolute or relative to path.")] string[]? files = null,
        [Description("Raw content to reduce instead of files. Provide extension to select the reducer.")] string? content = null,
        [Description("Extension that selects the reducer for content (for example .cs, .ts, .py). Defaults to .cs.")] string extension = ".cs",
        [Description("Reduction level: none, standard, aggressive, skeleton, publicApi. Defaults to standard.")] ReductionLevel level = ReductionLevel.Standard,
        [Description("Maximum tokens the reduced output may use, or 0 for no limit.")] int maxTokens = 0,
        CancellationToken cancellationToken = default) =>
        FuseOperationalErrors.ExecuteMcpAsync(() => FuseReduceCoreAsync(
            orchestrator, templateRegistry, path, files, content, extension, level, maxTokens, cancellationToken));

    private static Task<string> FuseReduceCoreAsync(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        string path,
        string[]? files,
        string? content,
        string extension,
        ReductionLevel level,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        int? maxTokenLimit = maxTokens > 0 ? maxTokens : null;

        if (!string.IsNullOrEmpty(content))
            return ReduceRunner.ReduceContentAsync(orchestrator, templateRegistry, content, extension, level, maxTokenLimit, cancellationToken);

        if (files is { Length: > 0 })
            return ReduceRunner.ReduceFilesAsync(orchestrator, templateRegistry, path, files, level, maxTokenLimit, cancellationToken);

        return Task.FromResult(FuseOperationalErrors.Format(
            FuseOperationalErrors.ValidationErrorPrefix,
            "provide either files (paths) or content to reduce."));
    }

    // Opens the store and builds the index on first use, so read tools work without an explicit index call.
    // The shared syntax job remains independent of this caller's wait. A semantic pass, when requested, continues
    // after syntax is readable and does not block this open.
    private static Task<WorkspaceIndexStore> OpenIndexedAsync(
        SemanticIndexer indexer,
        string path,
        CancellationToken cancellationToken) =>
        OpenIndexedAsync(ResolveRuntime(runtime: null, indexer), indexer, path, cancellationToken);

    private static async Task<WorkspaceIndexStore> OpenIndexedAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        string path,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        try
        {
            var store = await runtime.IndexAccess.OpenIndexedAsync(
                indexer,
                root,
                cancellationToken);
            await RecordIndexModeAsync(store, root, cancellationToken);
            return store;
        }
        catch (ColdStartInProgressException)
        {
            // R27: the cold syntax build did not finish within the deadline; return a bounded building_syntax
            // header as the tool body while the build continues, instead of blocking the read for the whole build.
            throw new IndexBlockedReadException(await FormatBuildingSyntaxHeaderAsync(root, runtime.IndexJobs, cancellationToken));
        }
        catch (Exception ex) when (IsIndexContention(ex))
        {
            throw new IndexBlockedReadException(await FormatBlockedReadHeaderAsync(root, cancellationToken));
        }
    }

    /// <summary>
    ///     Runs a read MCP tool body and returns the availability header as the tool result when the index cannot
    ///     be opened yet (R20), instead of a generic <c>index_busy:</c> prefix or an unbounded hang.
    /// </summary>
    /// <param name="action">The read tool implementation.</param>
    /// <returns>The tool result or a structured availability header on blocked read.</returns>
    internal static async Task<string> ExecuteReadMcpAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (IndexBlockedReadException ex)
        {
            // R30/R37: a not-ready read abstains and defers to native search (returns the fast structured signal,
            // not a diluted result). Count the deferral so it is never silent.
            FuseMetrics.RecordDegraded(DegradedStateKind.Deferred);
            return ex.AvailabilityHeader;
        }
        catch (Exception ex)
        {
            return FuseOperationalErrors.FromException(ex);
        }
    }

    private static bool IsIndexContention(Exception exception) =>
        exception is IndexBusyException
        || exception is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6
        || exception is IOException io && io.HResult == unchecked((int)0x80070020);

    // The ambient availability header (R3): one line that tells a client the grade of the answer that follows,
    // so an oracle read is never mistaken for oracle-grade when it is not. It reports the index mode (semantic,
    // partial, or syntax), whether tier-1 build capture is configured (the oracle-grade write path), and the
    // freshness stamp from the N6 reconcile contract (a nonzero stale count means a bulk change outran the
    // per-read reconcile, so the graph may lag the working tree). Store-backed oracle tools prepend it; the
    // compiler tools (fuse_check, fuse_refactor) carry their own explicit "cannot verify/rename" abstention,
    // which is the same signal at higher resolution.
    internal static async Task<string> OracleAvailabilityHeaderAsync(
        WorkspaceIndexStore store,
        string root,
        CancellationToken cancellationToken,
        string? indexStateOverride = null,
        Fuse.Workspace.IResidentWorkspaceProvider? residentWorkspaces = null)
    {
        var state = await store.GetStateAsync(cancellationToken);
        var indexState = indexStateOverride
            ?? await ComputeIndexStateAsync(store, state, root, cancellationToken);
        return await FormatAvailabilityHeaderAsync(
            store,
            root,
            indexState,
            state.FileCount,
            residentWorkspaces ?? Fuse.Workspace.NullResidentWorkspaceProvider.Instance,
            cancellationToken);
    }

    internal static Task<string> FormatNotIndexedAvailabilityHeaderAsync(
        string root,
        CancellationToken cancellationToken,
        Fuse.Workspace.IResidentWorkspaceProvider? residentWorkspaces = null) =>
        FormatAvailabilityHeaderAsync(
            store: null,
            root,
            "not_indexed",
            filesIndexed: 0,
            residentWorkspaces ?? Fuse.Workspace.NullResidentWorkspaceProvider.Instance,
            cancellationToken);

    // R27: the availability header for a cold read whose background syntax build is still running. Reports
    // building_syntax plus files_indexed so far, so the agent sees progress and retries rather than blocking.
    internal static Task<string> FormatBuildingSyntaxHeaderAsync(string root, CancellationToken cancellationToken) =>
        FormatBuildingSyntaxHeaderAsync(root, jobs: null, cancellationToken);

    internal static async Task<string> FormatBuildingSyntaxHeaderAsync(
        string root,
        IWorkspaceIndexJobManager? jobs,
        CancellationToken cancellationToken,
        Fuse.Workspace.IResidentWorkspaceProvider? residentWorkspaces = null)
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
            return await FormatAvailabilityHeaderAsync(
                store: null,
                root,
                "building_syntax",
                filesIndexed: 0,
                residentWorkspaces ?? Fuse.Workspace.NullResidentWorkspaceProvider.Instance,
                cancellationToken);

        try
        {
            await using var store = new WorkspaceIndexStore(
                databasePath,
                busyTimeoutMilliseconds: IndexCoordinator.ReadBusyTimeoutMilliseconds);
            if (await store.OpenForReadAsync(cancellationToken) is WorkspaceIndexReadOpenStatus.Ready)
            {
                var state = await store.GetStateAsync(cancellationToken);
                return await FormatAvailabilityHeaderAsync(
                        store,
                        root,
                        "building_syntax",
                        state.FileCount,
                        residentWorkspaces ?? Fuse.Workspace.NullResidentWorkspaceProvider.Instance,
                        cancellationToken)
                    + ElapsedProgressSuffix(root, jobs);
            }
        }
        catch (SqliteException)
        {
        }

        return await FormatAvailabilityHeaderAsync(
                store: null,
                root,
                "building_syntax",
                filesIndexed: 0,
                residentWorkspaces ?? Fuse.Workspace.NullResidentWorkspaceProvider.Instance,
                cancellationToken)
            + ElapsedProgressSuffix(root, jobs);
    }

    // R37: a progress suffix naming how long the in-flight cold build has been running, so a building header is
    // visible progress rather than a bare state. Empty when no build elapsed is known.
    private static string ElapsedProgressSuffix(string root, IWorkspaceIndexJobManager? jobs)
    {
        var elapsed = jobs?.GetStatus(root)?.Elapsed;
        return elapsed is null ? string.Empty : $"{Environment.NewLine}progress: building for ~{(int)elapsed.Value.TotalSeconds}s";
    }

    internal static async Task<string> FormatBlockedReadHeaderAsync(
        string root,
        CancellationToken cancellationToken,
        Fuse.Workspace.IResidentWorkspaceProvider? residentWorkspaces = null)
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
            return await FormatNotIndexedAvailabilityHeaderAsync(root, cancellationToken, residentWorkspaces);

        try
        {
            await using var store = new WorkspaceIndexStore(
                databasePath,
                busyTimeoutMilliseconds: IndexCoordinator.ReadBusyTimeoutMilliseconds);
            if (await store.OpenForReadAsync(cancellationToken) is WorkspaceIndexReadOpenStatus.Ready)
            {
                var state = await store.GetStateAsync(cancellationToken);
                return await FormatAvailabilityHeaderAsync(
                    store,
                    root,
                    "index_busy",
                    state.FileCount,
                    residentWorkspaces ?? Fuse.Workspace.NullResidentWorkspaceProvider.Instance,
                    cancellationToken);
            }
        }
        catch (SqliteException)
        {
        }

        return await FormatAvailabilityHeaderAsync(
            store: null,
            root,
            "index_busy",
            filesIndexed: -1,
            residentWorkspaces ?? Fuse.Workspace.NullResidentWorkspaceProvider.Instance,
            cancellationToken);
    }

    private static async Task<string> FormatAvailabilityHeaderAsync(
        WorkspaceIndexStore? store,
        string root,
        string indexState,
        int filesIndexed,
        Fuse.Workspace.IResidentWorkspaceProvider residentWorkspaces,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"index_state: {indexState}");
        if (filesIndexed >= 0)
            builder.AppendLine($"files_indexed: {filesIndexed}");
        // R30/R37: on any not-ready state, name the deferred grade and carry an actionable wait hint, so a cold or
        // contended read abstains and defers to native search rather than a silent stall or a diluted result - the
        // agent uses its native search meanwhile and retries fuse once index_state is ready.
        var hint = WaitHintFor(indexState);
        if (hint is not null)
        {
            builder.AppendLine("grade: deferred (not semantic-ready)");
            builder.AppendLine($"hint: {hint}");
        }
        builder.Append(await BuildAvailabilityLineAsync(store, root, residentWorkspaces, cancellationToken));
        return builder.ToString().TrimEnd();
    }

    // R37: the actionable wait hint for a not-ready index_state, or null when the index is ready/not-indexed.
    private static string? WaitHintFor(string indexState) => indexState switch
    {
        "building_syntax" => "index warming (syntax tier building); use your native search meanwhile and retry fuse once index_state is ready.",
        "upgrade_pending" => "syntax tier is live; the semantic graph is upgrading. Retry fuse shortly for the richer answer.",
        "index_busy" => "the index is briefly contended; retry in a moment, or run a shared fuse host.",
        "index_rebuilding" => "the index is rebuilding derived data; use your native search meanwhile and retry fuse once index_state is ready.",
        "stale_as_of" => "a bulk change outran the incremental reconcile; results may lag the working tree until the next index pass.",
        _ => null,
    };

    private static async Task<string> BuildAvailabilityLineAsync(
        WorkspaceIndexStore? store,
        string root,
        Fuse.Workspace.IResidentWorkspaceProvider residentWorkspaces,
        CancellationToken cancellationToken)
    {
        if (store is null)
            return BuildNotIndexedAvailabilityLine(root, residentWorkspaces);

        var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
        var staleRaw = await store.GetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, cancellationToken);
        // Syntax rows stay readable while an explicit semantic job is active. Name that state so a client knows
        // that compiler facts are still being added instead of mistaking the syntax tier for the final word.
        var pendingRaw = await store.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, cancellationToken);
        var upgradePending = pendingRaw == "1";
        var tier1Available = new BuildCaptureClient().IsAvailable;
        var tier1 = tier1Available ? "configured" : "not configured";
        // Name the verification grade fuse_check can currently serve (T0, D11): oracle-grade when tier-1 build
        // capture is configured, otherwise the build-grade fallback (dotnet build scoped to the owning project).
        // The verify verb never shrugs where a project can be built; the grade names the latency to expect.
        var verifyGrade = tier1Available
            ? "verify serves oracle-grade"
            : "verify serves build-grade (fuse_check runs a scoped dotnet build)";
        // Name which truth answered (S1/D8): a live resident workspace (current as of its stamp) or the store.
        var resident = residentWorkspaces.DescribeResident(root);
        var residentClause = resident is null
            ? "store-backed"
            : $"resident ({resident.ProjectCount} project(s), current as of {resident.AsOf})";
        var freshness = int.TryParse(staleRaw, out var stale) && stale > 0
            ? $"{stale} known file(s) changed since index, results may lag the working tree"
            : "up to date";
        var upgradeClause = upgradePending
            ? " compiler analysis in progress;"
            : "";
        // R23 single source of truth: report the reconciled FTS availability (the stamp AND the chunk_fts table),
        // exactly as the status body does via WorkspaceIndexState.FtsAvailable. The store's FullTextSearchAvailable
        // property reflects only whether THIS store instance initialized FTS; on the fast-status path the state is
        // read without opening the store, so that property stays default-false and disagreed with the body.
        var ftsState = await store.GetStateAsync(cancellationToken);
        var ftsClause = AvailabilityHeaderHelpers.FormatFtsAvailabilityClause(ftsState.FtsAvailable);
        return $"availability: index mode {mode}; {ftsClause}; tier-1 build capture {tier1}; {verifyGrade}; workspace {residentClause};{upgradeClause} {freshness}.";
    }

    private static async Task RecordIndexModeAsync(
        WorkspaceIndexStore store, string root, CancellationToken cancellationToken)
    {
        var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
        FuseMetrics.RecordIndexMode(root, mode);
    }

    private static MapDetail ParseDetail(string detail) => detail.Trim().ToLowerInvariant() switch
    {
        "symbols" => MapDetail.Symbols,
        "routes" => MapDetail.Routes,
        _ => MapDetail.All,
    };

    // R16 fast status output: index_state, availability header, counts, and daemon visibility without indexing.
    private static async Task<string> BuildFastStatusOutputAsync(
        string root,
        WorkspaceIndexStore? store,
        WorkspaceIndexState? state,
        Fuse.Workspace.IResidentWorkspaceProvider residentWorkspaces,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        if (store is null || state is null)
        {
            builder.AppendLine(await FormatNotIndexedAvailabilityHeaderAsync(root, cancellationToken, residentWorkspaces));
            builder.AppendLine($"workspace: {root}");
            builder.AppendLine("index mode: not_indexed");
            builder.AppendLine("files indexed: 0");
            builder.AppendLine("full-text search: unavailable");
        }
        else
        {
            builder.AppendLine(await OracleAvailabilityHeaderAsync(store, root, cancellationToken, residentWorkspaces: residentWorkspaces));
            builder.AppendLine($"workspace: {root}");
            builder.AppendLine($"index mode: {state.Mode ?? "unknown"}");
            builder.AppendLine($"files indexed: {state.FileCount}");
            builder.AppendLine($"full-text search: {(state.FtsAvailable ? "available" : "unavailable")}");
            builder.AppendLine($"index integrity: {IndexIntegrity.Check(state).Summary()}"); // R31
            var detailLimited = await store.GetMetaAsync(WorkspaceIndexStore.DetailLimitedFilesMetaKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(detailLimited) && detailLimited != "0")
                builder.AppendLine($"detail-limited files: {detailLimited}");
        }

        var daemon = await Fuse.Cli.Rpc.FuseHostClient.TryStatsAsync(root, TimeSpan.FromMilliseconds(500), cancellationToken);
        builder.AppendLine(daemon is null
            ? "daemon: none (this process serves the workspace directly)"
            : $"daemon: PID {daemon.ProcessId}, uptime {daemon.UptimeMs / 1000}s, RSS {daemon.WorkingSetBytes / (1024 * 1024)} MB (fuse host {daemon.HostVersion})");
        return builder.ToString().TrimEnd();
    }

    // R16: the doctor summary header reads index_meta only; it does not wait for an active semantic job.
    private static async Task<string> BuildFastDoctorSummaryHeaderAsync(
        string root,
        Fuse.Workspace.IResidentWorkspaceProvider residentWorkspaces,
        CancellationToken cancellationToken)
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
            return await FormatNotIndexedAvailabilityHeaderAsync(root, cancellationToken, residentWorkspaces);

        await using var store = new WorkspaceIndexStore(databasePath);
        return await OracleAvailabilityHeaderAsync(store, root, cancellationToken, residentWorkspaces: residentWorkspaces);
    }

    internal static async Task<string> ComputeIndexStateAsync(
        WorkspaceIndexStore store, WorkspaceIndexState state, string root, CancellationToken cancellationToken)
    {
        var manifest = await WorkspaceIndexManifest.ValidateAsync(root, store, cancellationToken);
        if (!manifest.Ready)
            return state.FileCount == 0 ? "not_indexed" : "index_rebuilding";

        var pending = await store.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, cancellationToken);
        if (pending == "1")
        {
            var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
            return mode == "syntax" ? "building_syntax" : "upgrade_pending";
        }

        var staleRaw = await store.GetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, cancellationToken);
        if (int.TryParse(staleRaw, out var stale) && stale > 0)
            return "stale_as_of";

        // R31: a store that fails its integrity invariants (unknown mode, missing schema version, or symbols with
        // no chunks on an FTS-available runtime) is internally inconsistent; never report it ready. Signal a
        // rebuild so the read path repairs it rather than serving a silent-empty "ready" index.
        if (!IndexIntegrity.Check(state).Healthy)
        {
            FuseMetrics.RecordDegraded(DegradedStateKind.IntegrityFailed); // R37
            return "index_rebuilding";
        }

        return "ready";
    }

    private static string BuildNotIndexedAvailabilityLine(
        string root,
        Fuse.Workspace.IResidentWorkspaceProvider residentWorkspaces)
    {
        var tier1Available = new BuildCaptureClient().IsAvailable;
        var tier1 = tier1Available ? "configured" : "not configured";
        var verifyGrade = tier1Available
            ? "verify serves oracle-grade"
            : "verify serves build-grade (fuse_check runs a scoped dotnet build)";
        var resident = residentWorkspaces.DescribeResident(root);
        var residentClause = resident is null
            ? "store-backed"
            : $"resident ({resident.ProjectCount} project(s), current as of {resident.AsOf})";
        return $"availability: index mode not_indexed; full-text search unavailable; tier-1 build capture {tier1}; {verifyGrade}; workspace {residentClause}; not indexed (run fuse_workspace action=index to build).";
    }

    private static FuseMcpRuntime ResolveRuntime(FuseMcpRuntime? runtime, SemanticIndexer indexer) =>
        runtime ?? FuseMcpRuntime.CreateIsolated(indexer);
}

/// <summary>
///     Thrown when a read tool cannot open the index yet. Mapped to the structured availability header at the
///     MCP boundary (R20) instead of a bare <see cref="FuseOperationalErrors.IndexBusyPrefix" /> line.
/// </summary>
internal sealed class IndexBlockedReadException : Exception
{
    /// <summary>Initializes a new instance with the full availability header to return as the tool body.</summary>
    /// <param name="availabilityHeader">The multi-line availability header (index_state, files_indexed, availability).</param>
    public IndexBlockedReadException(string availabilityHeader)
        : base(availabilityHeader)
    {
        AvailabilityHeader = availabilityHeader;
    }

    /// <summary>The structured header to return as the MCP tool result.</summary>
    public string AvailabilityHeader { get; }
}
