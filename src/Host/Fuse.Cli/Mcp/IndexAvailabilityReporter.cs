using System.Text;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using Fuse.Workspace;
using Microsoft.Data.Sqlite;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Builds the ambient availability header every store-backed read tool prepends (R3).
/// </summary>
/// <remarks>
///     One line tells a client the grade of the answer that follows, so an oracle read is never mistaken for
///     oracle-grade when it is not. The header reports the index mode (semantic, partial, or syntax), whether
///     tier-1 build capture is configured (the oracle-grade write path), and the freshness stamp from the N6
///     reconcile contract; a nonzero stale count means a bulk change outran the per-read reconcile, so the graph
///     may lag the working tree. The compiler tools (<c>fuse_check</c>, <c>fuse_refactor</c>) carry their own
///     explicit abstention instead, which is the same signal at higher resolution.
/// </remarks>
internal static class IndexAvailabilityReporter
{
    /// <summary>Builds the availability header for an opened, readable index store.</summary>
    /// <param name="store">The readable index store.</param>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">A token to cancel the metadata reads.</param>
    /// <param name="indexStateOverride">An explicit index state, or null to compute it from the store.</param>
    /// <param name="residentWorkspaces">The resident workspace provider naming which truth answered.</param>
    /// <returns>The multi-line availability header.</returns>
    internal static async Task<string> OracleHeaderAsync(
        WorkspaceIndexStore store,
        string root,
        CancellationToken cancellationToken,
        string? indexStateOverride = null,
        IResidentWorkspaceProvider? residentWorkspaces = null)
    {
        var state = await store.GetStateAsync(cancellationToken);
        var indexState = indexStateOverride ?? await ComputeIndexStateAsync(store, state, root, cancellationToken);
        return await FormatAsync(
            store,
            root,
            indexState,
            state.FileCount,
            residentWorkspaces ?? NullResidentWorkspaceProvider.Instance,
            cancellationToken);
    }

    /// <summary>Builds the availability header for a repository with no readable index.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="residentWorkspaces">The resident workspace provider naming which truth answered.</param>
    /// <returns>The multi-line availability header.</returns>
    internal static Task<string> NotIndexedHeaderAsync(
        string root,
        CancellationToken cancellationToken,
        IResidentWorkspaceProvider? residentWorkspaces = null) =>
        FormatAsync(
            store: null,
            root,
            "not_indexed",
            filesIndexed: 0,
            residentWorkspaces ?? NullResidentWorkspaceProvider.Instance,
            cancellationToken);

    /// <summary>Builds the header for a cold read whose syntax build is still running (R27).</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The multi-line availability header.</returns>
    internal static Task<string> BuildingSyntaxHeaderAsync(string root, CancellationToken cancellationToken) =>
        BuildingSyntaxHeaderAsync(root, jobs: null, cancellationToken);

    /// <summary>
    ///     Builds the building-syntax header from a count the caller already has, so a compiler-answered read does
    ///     not re-open the store only to restate progress.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="filesIndexed">The files indexed so far.</param>
    /// <param name="residentWorkspaces">The resident workspace provider naming which truth answered.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The multi-line availability header.</returns>
    internal static Task<string> BuildingSyntaxHeaderAsync(
        string root,
        int filesIndexed,
        IResidentWorkspaceProvider residentWorkspaces,
        CancellationToken cancellationToken) =>
        FormatAsync(store: null, root, "building_syntax", filesIndexed, residentWorkspaces, cancellationToken);

    /// <summary>
    ///     Builds the header for a cold read whose syntax build is still running (R27), including the files
    ///     indexed so far and how long the in-flight build has run, so the agent sees progress and retries.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="jobs">The job manager whose active snapshot supplies counts and elapsed time, when present.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="residentWorkspaces">The resident workspace provider naming which truth answered.</param>
    /// <returns>The multi-line availability header.</returns>
    internal static async Task<string> BuildingSyntaxHeaderAsync(
        string root,
        IWorkspaceIndexJobManager? jobs,
        CancellationToken cancellationToken,
        IResidentWorkspaceProvider? residentWorkspaces = null)
    {
        var resident = residentWorkspaces ?? NullResidentWorkspaceProvider.Instance;
        var activeJob = jobs?.GetStatus(root);
        if (activeJob is { State: IndexJobState.Queued or IndexJobState.Running or IndexJobState.Cancelling })
        {
            return await FormatAsync(store: null, root, "building_syntax", activeJob.Counts.Files, resident, cancellationToken)
                + ElapsedProgressSuffix(root, jobs);
        }

        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
            return await FormatAsync(store: null, root, "building_syntax", filesIndexed: 0, resident, cancellationToken);

        var counted = await TryCountIndexedFilesAsync(databasePath, root, "building_syntax", resident, cancellationToken);
        return (counted ?? await FormatAsync(store: null, root, "building_syntax", filesIndexed: 0, resident, cancellationToken))
            + ElapsedProgressSuffix(root, jobs);
    }

    /// <summary>Builds the header for a read blocked by index contention.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="residentWorkspaces">The resident workspace provider naming which truth answered.</param>
    /// <returns>The multi-line availability header.</returns>
    internal static async Task<string> BlockedReadHeaderAsync(
        string root,
        CancellationToken cancellationToken,
        IResidentWorkspaceProvider? residentWorkspaces = null)
    {
        var resident = residentWorkspaces ?? NullResidentWorkspaceProvider.Instance;
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
            return await NotIndexedHeaderAsync(root, cancellationToken, resident);

        return await TryCountIndexedFilesAsync(databasePath, root, "index_busy", resident, cancellationToken)
            ?? await FormatAsync(store: null, root, "index_busy", filesIndexed: -1, resident, cancellationToken);
    }

    /// <summary>
    ///     Computes the index state a read tool reports: not_indexed, index_rebuilding, building_syntax,
    ///     upgrade_pending, stale_as_of, or ready.
    /// </summary>
    /// <param name="store">The readable index store.</param>
    /// <param name="state">The store's counted state.</param>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">A token to cancel the metadata reads.</param>
    /// <returns>The index state name.</returns>
    internal static async Task<string> ComputeIndexStateAsync(
        WorkspaceIndexStore store,
        WorkspaceIndexState state,
        string root,
        CancellationToken cancellationToken)
    {
        var manifest = await WorkspaceIndexManifest.ValidateAsync(root, store, cancellationToken);
        if (!manifest.Ready)
            return state.FileCount == 0 ? "not_indexed" : "index_rebuilding";

        var pending = await store.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, cancellationToken);
        if (pending == "1")
        {
            var mode = await store.GetMetaAsync(WorkspaceIndexStore.IndexModeMetaKey, cancellationToken) ?? "unknown";
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

    // Reads the indexed file count from a warm store so a not-ready header still reports real progress. Returns
    // null when the store cannot be opened for read, so the caller falls back to the count-free header.
    private static async Task<string?> TryCountIndexedFilesAsync(
        string databasePath,
        string root,
        string indexState,
        IResidentWorkspaceProvider residentWorkspaces,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var store = new WorkspaceIndexStore(
                databasePath,
                busyTimeoutMilliseconds: IndexCoordinator.ReadBusyTimeoutMilliseconds);
            if (await store.OpenForReadAsync(cancellationToken) is not WorkspaceIndexReadOpenStatus.Ready)
                return null;

            var state = await store.GetStateAsync(cancellationToken);
            return await FormatAsync(store, root, indexState, state.FileCount, residentWorkspaces, cancellationToken);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    // R37: a progress suffix naming how long the in-flight cold build has been running, so a building header is
    // visible progress rather than a bare state. Empty when no build elapsed is known.
    private static string ElapsedProgressSuffix(string root, IWorkspaceIndexJobManager? jobs)
    {
        var elapsed = jobs?.GetStatus(root)?.Elapsed;
        return elapsed is null ? string.Empty : $"{Environment.NewLine}progress: building for ~{(int)elapsed.Value.TotalSeconds}s";
    }

    private static async Task<string> FormatAsync(
        WorkspaceIndexStore? store,
        string root,
        string indexState,
        int filesIndexed,
        IResidentWorkspaceProvider residentWorkspaces,
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
        IResidentWorkspaceProvider residentWorkspaces,
        CancellationToken cancellationToken)
    {
        var verification = DescribeVerification();
        var residentClause = DescribeResident(root, residentWorkspaces);
        if (store is null)
        {
            return $"availability: index mode not_indexed; full-text search unavailable; tier-1 build capture "
                + $"{verification.Tier1}; {verification.Grade}; workspace {residentClause}; not indexed "
                + "(run fuse_workspace action=index to build).";
        }

        var mode = await store.GetMetaAsync(WorkspaceIndexStore.IndexModeMetaKey, cancellationToken) ?? "unknown";
        var staleRaw = await store.GetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, cancellationToken);
        // Syntax rows stay readable while an explicit semantic job is active. Name that state so a client knows
        // that compiler facts are still being added instead of mistaking the syntax tier for the final word.
        var pendingRaw = await store.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, cancellationToken);
        var upgradeClause = pendingRaw == "1" ? " compiler analysis in progress;" : string.Empty;
        var freshness = int.TryParse(staleRaw, out var stale) && stale > 0
            ? $"{stale} known file(s) changed since index, results may lag the working tree"
            : "up to date";
        // R23 single source of truth: report the reconciled FTS availability (the stamp AND the chunk_fts table),
        // exactly as the status body does via WorkspaceIndexState.FtsAvailable. The store's FullTextSearchAvailable
        // property reflects only whether THIS store instance initialized FTS; on the fast-status path the state is
        // read without opening the store, so that property stays default-false and disagreed with the body.
        var ftsState = await store.GetStateAsync(cancellationToken);
        var ftsClause = AvailabilityHeaderHelpers.FormatFtsAvailabilityClause(ftsState.FtsAvailable);
        return $"availability: index mode {mode}; {ftsClause}; tier-1 build capture {verification.Tier1}; "
            + $"{verification.Grade}; workspace {residentClause};{upgradeClause} {freshness}.";
    }

    // Names the verification grade fuse_check can currently serve (T0, D11): oracle-grade when tier-1 build capture
    // is configured, otherwise the build-grade fallback (dotnet build scoped to the owning project). The verify verb
    // never shrugs where a project can be built; the grade names the latency to expect.
    private static (string Tier1, string Grade) DescribeVerification()
    {
        var available = new BuildCaptureClient().IsAvailable;
        return (
            available ? "configured" : "not configured",
            available
                ? "verify serves oracle-grade"
                : "verify serves build-grade (fuse_check runs a scoped dotnet build)");
    }

    // Names which truth answered (S1/D8): a live resident workspace (current as of its stamp) or the store.
    private static string DescribeResident(string root, IResidentWorkspaceProvider residentWorkspaces)
    {
        var resident = residentWorkspaces.DescribeResident(root);
        return resident is null
            ? "store-backed"
            : $"resident ({resident.ProjectCount} project(s), current as of {resident.AsOf})";
    }
}
