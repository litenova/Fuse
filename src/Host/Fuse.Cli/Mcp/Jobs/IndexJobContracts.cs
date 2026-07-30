using System.Text.Json.Serialization;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The amount of compiler analysis an index job performs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<IndexDepth>))]
public enum IndexDepth
{
    /// <summary>Extract declarations, routes, and searchable syntax without loading MSBuild.</summary>
    Syntax,

    /// <summary>Run syntax extraction and then load the selected compiler workspace.</summary>
    Semantic,
}

/// <summary>
///     The lifecycle state of one repository-owned index job.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<IndexJobState>))]
public enum IndexJobState
{
    /// <summary>The job has been accepted and is waiting for its worker.</summary>
    Queued,

    /// <summary>The job is running.</summary>
    Running,

    /// <summary>A cancellation request has been sent to the worker.</summary>
    Cancelling,

    /// <summary>The job reached its requested depth.</summary>
    Completed,

    /// <summary>The job stopped after a cancellation request.</summary>
    Cancelled,

    /// <summary>The job stopped because one of its stages failed.</summary>
    Failed,
}

/// <summary>
///     The observable stage of an index job.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<IndexPhase>))]
public enum IndexPhase
{
    /// <summary>Discover the repository inventory.</summary>
    Inventory,

    /// <summary>Read and parse source files.</summary>
    SyntaxExtraction,

    /// <summary>Persist syntax records and search documents.</summary>
    SyntaxPersistence,

    /// <summary>Resolve the requested compiler target.</summary>
    SemanticPreparation,

    /// <summary>Load compiler state and extract semantic facts.</summary>
    SemanticExtraction,

    /// <summary>Persist compiler-derived records.</summary>
    SemanticPersistence,

    /// <summary>Write completion metadata and maintain the database.</summary>
    Finalization,
}

/// <summary>
///     A request to build or refresh the derived index for one repository root.
/// </summary>
/// <param name="Root">The repository root to index.</param>
/// <param name="Depth">The requested syntax or semantic depth.</param>
/// <param name="Force">Whether the job discards the existing derived index first.</param>
/// <param name="CaptureBundlePath">An optional portable capture bundle directory.</param>
public sealed record IndexJobRequest(
    string Root,
    IndexDepth Depth,
    bool Force,
    string? CaptureBundlePath);

/// <summary>
///     Counts collected by the most recent completed stage of an index job.
/// </summary>
/// <param name="Files">The indexed file count.</param>
/// <param name="Projects">The indexed project count.</param>
/// <param name="Symbols">The indexed symbol count.</param>
/// <param name="Chunks">The indexed source chunk count.</param>
/// <param name="Routes">The indexed route count.</param>
public sealed record IndexCountSnapshot(int Files, int Projects, int Symbols, int Chunks, int Routes)
{
    /// <summary>An empty count snapshot before any stage completes.</summary>
    public static IndexCountSnapshot Empty { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Creates a snapshot from one index pass result.</summary>
    /// <param name="result">The completed index pass.</param>
    /// <returns>The projected counts.</returns>
    public static IndexCountSnapshot From(SemanticIndexResult result) =>
        new(result.FileCount, result.ProjectCount, result.SymbolCount, result.ChunkCount, result.RouteCount);
}

/// <summary>
///     The disk space occupied by the repository's derived index files.
/// </summary>
/// <param name="DatabaseBytes">The main SQLite database size.</param>
/// <param name="WalBytes">The SQLite write-ahead-log size.</param>
/// <param name="SharedMemoryBytes">The SQLite shared-memory sidecar size.</param>
/// <param name="TotalFuseBytes">The combined size of files in the repository's <c>.fuse</c> directory.</param>
public sealed record IndexStorageSnapshot(long DatabaseBytes, long WalBytes, long SharedMemoryBytes, long TotalFuseBytes)
{
    /// <summary>An empty storage snapshot before a database has been created.</summary>
    public static IndexStorageSnapshot Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>
///     A read-only summary of the persisted index when no in-memory job owns the latest counts.
/// </summary>
/// <param name="State">The on-disk store state.</param>
/// <param name="IndexMode">The completed index depth or semantic mode.</param>
/// <param name="Freshness">The manifest validation outcome.</param>
/// <param name="Files">The number of indexed files.</param>
/// <param name="Symbols">The number of indexed symbols.</param>
/// <param name="Chunks">The number of indexed search chunks.</param>
/// <param name="Routes">The number of indexed routes.</param>
/// <param name="CompletedAt">The last completed inventory timestamp, when recorded.</param>
/// <param name="LastFailure">The latest persisted job failure, when one exists.</param>
public sealed record IndexStoreStatus(
    string State,
    string? IndexMode,
    string Freshness,
    int Files,
    int Symbols,
    int Chunks,
    int Routes,
    string? CompletedAt,
    string? LastFailure)
{
    /// <summary>Describes a repository with no readable derived index.</summary>
    public static IndexStoreStatus NotIndexed { get; } = new(
        "not_indexed", null, "not_indexed", 0, 0, 0, 0, null, null);
}

/// <summary>
///     An immutable view of an index job for CLI, MCP, and host RPC consumers.
/// </summary>
/// <param name="JobId">The job identifier shared by all joiners.</param>
/// <param name="Root">The normalized repository root.</param>
/// <param name="State">The current lifecycle state.</param>
/// <param name="Phase">The current indexing phase.</param>
/// <param name="PhaseNumber">The one-based number of the current phase.</param>
/// <param name="PhaseCount">The number of phases required for the requested depth.</param>
/// <param name="CompletedUnits">Completed work units in the current phase.</param>
/// <param name="TotalUnits">Total work units in the current phase, when known.</param>
/// <param name="PhasePercent">The current phase percent, when the total is known.</param>
/// <param name="EstimatedRemaining">The estimated time remaining after enough units have completed.</param>
/// <param name="CurrentItem">The current file, project, or operation, when known.</param>
/// <param name="StartedAt">When the job was accepted.</param>
/// <param name="Elapsed">Elapsed wall-clock time.</param>
/// <param name="Counts">The last known indexed record counts.</param>
/// <param name="Storage">The last measured derived-index storage usage.</param>
/// <param name="Warnings">Bounded non-fatal diagnostics from the job.</param>
/// <param name="ErrorCode">A stable error code when the job failed.</param>
/// <param name="ErrorMessage">A direct recovery message when the job failed.</param>
public sealed record IndexJobSnapshot(
    string JobId,
    string Root,
    IndexJobState State,
    IndexPhase Phase,
    int PhaseNumber,
    int PhaseCount,
    long CompletedUnits,
    long? TotalUnits,
    double? PhasePercent,
    TimeSpan? EstimatedRemaining,
    string? CurrentItem,
    DateTimeOffset StartedAt,
    TimeSpan Elapsed,
    IndexCountSnapshot Counts,
    IndexStorageSnapshot Storage,
    IReadOnlyList<string> Warnings,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
///     The result of accepting an index request.
/// </summary>
/// <param name="Snapshot">The active or retained job snapshot.</param>
/// <param name="Joined">Whether the request joined a job already running for the root.</param>
/// <param name="Conflict">Whether the request is incompatible with the active job.</param>
public sealed record IndexJobStartResult(IndexJobSnapshot Snapshot, bool Joined, bool Conflict);

/// <summary>
///     A mutable-stage update sent from an index executor to its job manager.
/// </summary>
/// <param name="Phase">The stage now in progress.</param>
/// <param name="CompletedUnits">Completed units in the stage.</param>
/// <param name="TotalUnits">Total stage units when known.</param>
/// <param name="CurrentItem">The current item or operation.</param>
/// <param name="Warning">An optional bounded warning to append to the snapshot.</param>
public sealed record IndexJobProgress(
    IndexPhase Phase,
    long CompletedUnits = 0,
    long? TotalUnits = null,
    string? CurrentItem = null,
    string? Warning = null);
