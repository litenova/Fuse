using System.Security.Cryptography;
using System.Text;
using Fuse.Cli.Rpc;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using Fuse.Workspace;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Implements <c>fuse_workspace</c>: the state of the workspace and its index, plus the one explicit
///     tree-write path.
/// </summary>
/// <remarks>
///     <c>action=status</c> reports the index mode, verify grade, and freshness without creating an index;
///     <c>index</c> and <c>cancel</c> drive the repository-owned job; <c>map</c> prints the symbol and route map;
///     <c>doctor</c> diagnoses the semantic load per project; <c>apply</c> writes a proposed single-file edit
///     (Decision D2) and is a dry run unless <c>write=true</c>.
/// </remarks>
internal static class WorkspaceToolOperations
{
    /// <summary>Runs one workspace action and maps a not-ready index to its availability header.</summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="jobs">The daemon-aware lifecycle client for index start, status, and cancellation.</param>
    /// <param name="action">The action: status, index, cancel, map, doctor, or apply.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="detail">For the map action: the detail to include (symbols, routes, all).</param>
    /// <param name="maxRows">For the map action: the maximum rows per section.</param>
    /// <param name="file">For the apply action: the repository-relative file to write.</param>
    /// <param name="content">For the apply action: the complete replacement content.</param>
    /// <param name="write">For the apply action: whether to write instead of returning a dry-run report.</param>
    /// <param name="expectedHash">For the apply action: the SHA-256 hash of the content the edit was derived from.</param>
    /// <param name="refresh">For the doctor action: whether to force a live MSBuild diagnosis.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The action's result, or a descriptive error.</returns>
    internal static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        IndexJobClient jobs,
        string action = "status",
        string path = ".",
        string detail = "all",
        int maxRows = 200,
        string file = "",
        string content = "",
        bool write = false,
        string expectedHash = "",
        bool refresh = false,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        IndexedStoreAccess.ExecuteReadMcpAsync(() => DispatchAsync(
            IndexedStoreAccess.ResolveRuntime(runtime, indexer),
            indexer,
            jobs,
            action,
            path,
            detail,
            maxRows,
            file,
            content,
            write,
            expectedHash,
            refresh,
            cancellationToken));

    /// <summary>Prints a map of the indexed workspace (symbols, routes, counts).</summary>
    /// <param name="indexer">The semantic indexer (used to build the index on first use).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="detail">The detail to include: symbols, routes, or all.</param>
    /// <param name="maxRows">The maximum rows per section.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The workspace map.</returns>
    internal static async Task<string> MapAsync(
        SemanticIndexer indexer,
        string path = ".",
        string detail = "all",
        int maxRows = 200,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        var toolRuntime = IndexedStoreAccess.ResolveRuntime(runtime, indexer);
        await using var store = await IndexedStoreAccess.OpenIndexedAsync(toolRuntime, indexer, path, cancellationToken);
        var renderer = new WorkspaceMapRenderer(store, store);
        return await renderer.RenderAsync(ParseDetail(detail), maxRows, cancellationToken);
    }

    private static async Task<string> DispatchAsync(
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
        return action.Trim().ToLowerInvariant() switch
        {
            "index" => await StartIndexJobAsync(jobs, root, cancellationToken),
            "cancel" => await CancelIndexJobAsync(jobs, root, cancellationToken),
            "map" => await MapAsync(indexer, root, detail, maxRows, cancellationToken, runtime),
            "doctor" => await DoctorAsync(runtime, indexer, jobs, root, refresh, cancellationToken),
            "apply" => await ApplyAsync(root, file, content, write, expectedHash, cancellationToken),
            "status" or "" => await StatusAsync(runtime, jobs, root, cancellationToken),
            _ => FuseOperationalErrors.Format(
                FuseOperationalErrors.ValidationErrorPrefix,
                $"unknown workspace action '{action}'. Use status, index, cancel, map, doctor, or apply."),
        };
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
        return IndexJobSnapshotFormatter.Format(start.Result.Snapshot, start.Result.Joined, start.UsesDaemon);
    }

    private static async Task<string> CancelIndexJobAsync(
        IndexJobClient jobs,
        string root,
        CancellationToken cancellationToken)
    {
        var result = await jobs.CancelAsync(root, cancellationToken);
        return result.Snapshot is null
            ? $"workspace: {root}{Environment.NewLine}index job: none"
            : IndexJobSnapshotFormatter.Format(result.Snapshot, joined: null, result.UsesDaemon);
    }

    // The one explicit tree-write path (Decision D2): write a single file's proposed content, guarded so the
    // server never writes outside the workspace and never writes silently. A dry run (write=false, the default)
    // reports what would change; write=true performs the one write. The path is refused if it escapes the root.
    // R36: the write is atomic (temp file + rename) and conflict-checked (refuses when the file changed since the
    // edit was derived from, when expectedHash is supplied), so it never clobbers a concurrent edit or leaves a
    // partially written file.
    private static async Task<string> ApplyAsync(
        string path,
        string file,
        string content,
        bool write,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return FuseOperationalErrors.Format(
                FuseOperationalErrors.ValidationErrorPrefix,
                "apply needs a file (the repo-relative path to write) and content.");
        }

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
                var found = currentHash is null ? "the file does not exist" : $"found {ShortHash(currentHash)}";
                return FuseOperationalErrors.Format(
                    FuseOperationalErrors.ValidationErrorPrefix,
                    $"conflict: {file} changed since the edit was derived (expected {ShortHash(expectedHash)}, {found}). Re-read the file and re-derive the edit, then apply again.");
            }
        }

        if (!write)
            return $"dry run (no write): would {(exists ? "overwrite" : "create")} {file} ({content.Length} chars). Re-run with write=true to apply.";

        var directory = Path.GetDirectoryName(full);
        if (directory is not null)
            Directory.CreateDirectory(directory);

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
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string ShortHash(string hash) => hash.Length <= 12 ? hash : hash[..12];

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
    private static async Task<string> StatusAsync(
        FuseMcpRuntime runtime,
        IndexJobClient jobs,
        string path,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        var job = await jobs.StatusAsync(root, cancellationToken);
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
        {
            return AppendJobSnapshot(
                await BuildStatusBodyAsync(root, store: null, state: null, runtime.ResidentWorkspaces, cancellationToken),
                job.Snapshot,
                job.UsesDaemon);
        }

        await using var store = new WorkspaceIndexStore(databasePath);
        if (await store.OpenForReadAsync(cancellationToken) is not WorkspaceIndexReadOpenStatus.Ready)
        {
            return AppendJobSnapshot(
                await BuildStatusBodyAsync(root, store: null, state: null, runtime.ResidentWorkspaces, cancellationToken),
                job.Snapshot,
                job.UsesDaemon);
        }

        var state = await store.GetStateAsync(cancellationToken);
        return AppendJobSnapshot(
            await BuildStatusBodyAsync(root, store, state, runtime.ResidentWorkspaces, cancellationToken),
            job.Snapshot,
            job.UsesDaemon);
    }

    // The doctor action: the per-project semantic-load diagnosis, so a downgrade names its reason per project.
    // The summary header uses the same fast read-only meta path as status (R16). R43: the diagnosis is served from
    // the diagnosis stamped in the warm index (sub-second, no MSBuild load); a live load runs only when refresh is
    // requested or no stamp is present.
    private static async Task<string> DoctorAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        IndexJobClient jobs,
        string path,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        var builder = new StringBuilder();
        builder.AppendLine(await BuildDoctorSummaryHeaderAsync(root, runtime.ResidentWorkspaces, cancellationToken));
        builder.AppendLine($"workspace: {root}");

        var persisted = refresh ? null : await PersistedDiagnosisReader.TryReadAsync(root, cancellationToken);
        if (persisted is not null)
        {
            // R43: reported from the index stamp, so doctor is sub-second and reflects what was actually indexed.
            builder.AppendLine("diagnosis source: warm index (stamped at index time; pass refresh=true for a live load)");
            AppendDiagnosis(
                builder, persisted.Tier, persisted.SelectedSolution, persisted.SelectionNote,
                persisted.ProjectsLoaded, persisted.ProjectsTotal,
                persisted.Projects.Select(project => (project.Name, project.Loaded, project.Reason)).ToList());
        }
        else
        {
            var diagnosis = await indexer.DiagnoseLoadAsync(root, cancellationToken);
            builder.AppendLine($"diagnosis source: live MSBuild load{(refresh ? " (refresh=true)" : " (no index stamp yet)")}");
            AppendDiagnosis(
                builder, diagnosis.Tier, diagnosis.SelectedSolution, diagnosis.SelectionNote,
                diagnosis.ProjectsLoaded, diagnosis.ProjectsTotal,
                diagnosis.Projects.Select(project => (project.Name, project.Loaded, project.Reason)).ToList());
        }

        await AppendStoreHealthAsync(builder, root, cancellationToken);
        AppendDegradedStates(builder);
        AppendRunningDaemons(builder);

        var job = await jobs.StatusAsync(root, cancellationToken);
        return AppendJobSnapshot(builder.ToString().TrimEnd(), job.Snapshot, job.UsesDaemon);
    }

    private static void AppendDiagnosis(
        StringBuilder builder,
        string tier,
        string? selectedSolution,
        string? selectionNote,
        int projectsLoaded,
        int projectsTotal,
        IReadOnlyList<(string Name, bool Loaded, string Reason)> projects)
    {
        builder.AppendLine($"load tier: {tier}");
        builder.AppendLine($"selected solution: {selectedSolution ?? "none (syntax-only)"}");
        if (selectionNote is not null)
            builder.AppendLine($"WARNING: {selectionNote}");
        builder.AppendLine($"projects loaded: {projectsLoaded}/{projectsTotal}");
        if (projects.Count == 0)
        {
            builder.AppendLine("no projects: the workspace has no solution or project, or none opened; indexing is syntax-only.");
            return;
        }

        foreach (var project in projects)
            builder.AppendLine($"  {project.Name}: {(project.Loaded ? "loaded" : "not loaded")} - {project.Reason}");
    }

    // R31/R35/R37: report the store's integrity, size, and coverage gaps so a self-inconsistent, stale, or bloated
    // index is visible rather than silently served.
    private static async Task AppendStoreHealthAsync(StringBuilder builder, string root, CancellationToken cancellationToken)
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
            return;

        var databaseInfo = new FileInfo(databasePath);
        builder.AppendLine($"store size: {databaseInfo.Length / 1024} KB; last written: {databaseInfo.LastWriteTimeUtc:O}");
        try
        {
            await using var store = new WorkspaceIndexStore(databasePath);
            if (await store.OpenForReadAsync(cancellationToken) is not WorkspaceIndexReadOpenStatus.Ready)
                return;

            var state = await store.GetStateAsync(cancellationToken);
            builder.AppendLine($"index integrity: {IndexIntegrity.Check(state).Summary()}");
            var skipped = await store.GetMetaAsync(WorkspaceIndexStore.SkippedFilesMetaKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(skipped) && skipped != "0")
                builder.AppendLine($"skipped files: {skipped}");
            var detailLimited = await store.GetMetaAsync(WorkspaceIndexStore.DetailLimitedFilesMetaKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(detailLimited) && detailLimited != "0")
                builder.AppendLine($"detail-limited files: {detailLimited}");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
        }
    }

    // R37: degraded-state counts this process has served, so a fallback or not-ready read is never silent.
    private static void AppendDegradedStates(StringBuilder builder)
    {
        var kinds = new (DegradedStateKind Kind, string Name)[]
        {
            (DegradedStateKind.IndexBusy, "index_busy"),
            (DegradedStateKind.IndexRebuilding, "index_rebuilding"),
            (DegradedStateKind.IntegrityFailed, "integrity_failed"),
            (DegradedStateKind.Deferred, "deferred"),
            (DegradedStateKind.LexicalFallback, "lexical_fallback"),
            (DegradedStateKind.VerifyAbstained, "verify_abstained"),
        };
        var summary = kinds
            .Where(kind => FuseMetrics.GetDegradedCount(kind.Kind) > 0)
            .Select(kind => $"{kind.Name}={FuseMetrics.GetDegradedCount(kind.Kind)}")
            .ToList();
        builder.AppendLine($"degraded states this session: {(summary.Count == 0 ? "none" : string.Join(", ", summary))}");
    }

    // R28: list the running fuse host daemons with their served root and version, so a stale or mismatched daemon
    // (accumulated across respawns or left after an upgrade) is visible rather than an invisible orphan.
    private static void AppendRunningDaemons(StringBuilder builder)
    {
        var daemons = DaemonRegistry.List();
        builder.AppendLine($"running daemons: {daemons.Count}");
        foreach (var daemon in daemons)
            builder.AppendLine($"  PID {daemon.ProcessId} (fuse host {daemon.Version}) serving {daemon.Root}");
    }

    private static string AppendJobSnapshot(string output, IndexJobSnapshot? snapshot, bool usesDaemon) =>
        snapshot is null
            ? output + Environment.NewLine + "index job: none"
            : output + Environment.NewLine + IndexJobSnapshotFormatter.Format(snapshot, joined: null, usesDaemon);

    // R16 fast status output: index_state, availability header, counts, and daemon visibility without indexing.
    private static async Task<string> BuildStatusBodyAsync(
        string root,
        WorkspaceIndexStore? store,
        WorkspaceIndexState? state,
        IResidentWorkspaceProvider residentWorkspaces,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        if (store is null || state is null)
        {
            builder.AppendLine(await IndexAvailabilityReporter.NotIndexedHeaderAsync(root, cancellationToken, residentWorkspaces));
            builder.AppendLine($"workspace: {root}");
            builder.AppendLine("index mode: not_indexed");
            builder.AppendLine("files indexed: 0");
            builder.AppendLine("full-text search: unavailable");
        }
        else
        {
            builder.AppendLine(await IndexAvailabilityReporter.OracleHeaderAsync(
                store, root, cancellationToken, residentWorkspaces: residentWorkspaces));
            builder.AppendLine($"workspace: {root}");
            builder.AppendLine($"index mode: {state.Mode ?? "unknown"}");
            builder.AppendLine($"files indexed: {state.FileCount}");
            builder.AppendLine($"full-text search: {(state.FtsAvailable ? "available" : "unavailable")}");
            builder.AppendLine($"index integrity: {IndexIntegrity.Check(state).Summary()}"); // R31
            var detailLimited = await store.GetMetaAsync(WorkspaceIndexStore.DetailLimitedFilesMetaKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(detailLimited) && detailLimited != "0")
                builder.AppendLine($"detail-limited files: {detailLimited}");
        }

        var daemon = await FuseHostClient.TryStatsAsync(root, TimeSpan.FromMilliseconds(500), cancellationToken);
        builder.AppendLine(daemon is null
            ? "daemon: none (this process serves the workspace directly)"
            : $"daemon: PID {daemon.ProcessId}, uptime {daemon.UptimeMs / 1000}s, RSS {daemon.WorkingSetBytes / (1024 * 1024)} MB (fuse host {daemon.HostVersion})");
        return builder.ToString().TrimEnd();
    }

    // R16: the doctor summary header reads index_meta only; it does not wait for an active semantic job.
    private static async Task<string> BuildDoctorSummaryHeaderAsync(
        string root,
        IResidentWorkspaceProvider residentWorkspaces,
        CancellationToken cancellationToken)
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
            return await IndexAvailabilityReporter.NotIndexedHeaderAsync(root, cancellationToken, residentWorkspaces);

        await using var store = new WorkspaceIndexStore(databasePath);
        return await IndexAvailabilityReporter.OracleHeaderAsync(
            store, root, cancellationToken, residentWorkspaces: residentWorkspaces);
    }

    private static MapDetail ParseDetail(string detail) => detail.Trim().ToLowerInvariant() switch
    {
        "symbols" => MapDetail.Symbols,
        "routes" => MapDetail.Routes,
        _ => MapDetail.All,
    };
}
