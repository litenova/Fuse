using Fuse.Cli.Mcp;

namespace Fuse.Cli.Rpc;

/// <summary>
///     The result of the <c>fuse/handshake</c> method: the host's package version, the wire protocol version
///     the UI client must match, and the session token required on every subsequent RPC call. A protocol
///     mismatch is surfaced to the user as a clear notification rather than failing later with an opaque
///     serialization error.
/// </summary>
/// <param name="HostVersion">The host package version (for example <c>3.0.0</c>).</param>
/// <param name="ProtocolVersion">The RPC protocol version; the client compares it to its own.</param>
/// <param name="SessionToken">The session token the client must pass on all RPC methods except handshake.</param>
public sealed record FuseHostHandshake(string HostVersion, int ProtocolVersion, string SessionToken);

/// <summary>
///     The result of the <c>fuse/stats</c> method: cheap process-level health for the status bar and index
///     panel. Engine statistics (token totals, cache hit rates, pattern summary) are added by the richer
///     <c>fuse/stats</c> projection in a later phase; this carries the host liveness fields that need no scoping
///     run.
/// </summary>
/// <param name="HostVersion">The host package version.</param>
/// <param name="ProcessId">The host operating-system process id.</param>
/// <param name="UptimeMs">Milliseconds since the host started serving.</param>
/// <param name="WorkingSetBytes">The host process working-set size in bytes, shown as host RSS in the UI.</param>
public sealed record FuseHostStats(string HostVersion, int ProcessId, long UptimeMs, long WorkingSetBytes);

/// <summary>
///     One node in the dependency graph projection (<c>fuse/graph</c>): a file with the data the webview needs to
///     size, color, and label it without recomputing anything.
/// </summary>
/// <param name="Path">The normalized repository-relative file path; the node identity.</param>
/// <param name="DeclaredTypes">The type names the file declares, for the node label and hover.</param>
/// <param name="Centrality">The normalized PageRank centrality in <c>[0, 1]</c>, driving node size.</param>
/// <param name="TokenCost">The estimated token cost of including the file, driving node color.</param>
/// <param name="Role">The file's role when a scope is active (seed, dependency, changed), or <c>null</c>.</param>
public sealed record GraphNodeDto(
    string Path,
    IReadOnlyList<string> DeclaredTypes,
    double Centrality,
    int TokenCost,
    string? Role);

/// <summary>
///     One directed edge in the dependency graph projection: a reference from one file to another.
/// </summary>
/// <param name="From">The referencing file path.</param>
/// <param name="To">The referenced file path.</param>
/// <param name="Weight">The edge weight (reference strength), for styling.</param>
/// <param name="Kind">The reference kind (for example <c>reference</c>), for edge styling.</param>
public sealed record GraphEdgeDto(string From, string To, double Weight, string Kind);

/// <summary>
///     The result of the <c>fuse/graph</c> method: the dependency graph at the requested level of detail.
/// </summary>
/// <param name="Nodes">The graph nodes.</param>
/// <param name="Edges">The graph edges.</param>
/// <param name="Detail">The level of detail the nodes are at (<c>Files</c> or <c>Directories</c>).</param>
public sealed record GraphDto(
    IReadOnlyList<GraphNodeDto> Nodes,
    IReadOnlyList<GraphEdgeDto> Edges,
    string Detail);

/// <summary>The indexed file count for one language, projected for the index panel.</summary>
/// <param name="Language">The language tag (for example <c>csharp</c>), or <c>unknown</c>.</param>
/// <param name="Count">The number of indexed files carrying the tag.</param>
public sealed record LanguageCountDto(string Language, int Count);

/// <summary>
///     A retained index summary for diagnostics. Index lifecycle callers use <c>fuse/indexStart</c>,
///     <c>fuse/indexStatus</c>, and <c>fuse/indexCancel</c>.
/// </summary>
/// <param name="IndexState">A coarse state label (<c>Warm</c>, <c>Indexing</c>, or <c>NotIndexed</c>).</param>
/// <param name="FileCount">The number of indexed files.</param>
/// <param name="ElapsedMs">Wall-clock milliseconds the index build took.</param>
/// <param name="Mode">The index tier: <c>semantic</c> (full typed graph), <c>partial</c>, or <c>syntax</c>.</param>
/// <param name="SymbolCount">The number of indexed symbols.</param>
/// <param name="RouteCount">The number of indexed routes.</param>
/// <param name="SchemaVersion">The on-disk index schema version.</param>
/// <param name="FullTextSearch">Whether full-text search (FTS5) is available.</param>
/// <param name="FuseVersion">The Fuse build that wrote the index.</param>
/// <param name="Languages">The indexed file count per language, most files first.</param>
public sealed record IndexResultDto(
    string IndexState,
    int FileCount,
    long ElapsedMs,
    string Mode,
    int SymbolCount,
    int RouteCount,
    int SchemaVersion,
    bool FullTextSearch,
    string FuseVersion,
    IReadOnlyList<LanguageCountDto> Languages);

/// <summary>
///     One emitted file in a scope result: the path the scope included and its token cost, for the scope-result
///     tree view.
/// </summary>
/// <param name="Path">The normalized repository-relative file path.</param>
/// <param name="TokenCost">The token cost the file contributed to the emitted payload.</param>
public sealed record ScopeFileDto(string Path, int TokenCost);

/// <summary>
///     The result of the <c>fuse/scope</c> method: the files a scoped fusion included with their token costs, the
///     total token count, and a path to the emitted payload written to a temp file the extension opens read-only.
/// </summary>
/// <param name="Mode">The scoping mode that ran (<c>focus</c>, <c>search</c>, or <c>changes</c>).</param>
/// <param name="Files">The emitted files with their token costs, most expensive first.</param>
/// <param name="TotalTokens">The total tokens across the emitted payload.</param>
/// <param name="PayloadPath">The absolute path to the emitted payload file, or <c>null</c> when nothing emitted.</param>
public sealed record ScopeResultDto(
    string Mode,
    IReadOnlyList<ScopeFileDto> Files,
    long TotalTokens,
    string? PayloadPath);

/// <summary>
///     One secret diagnostic: a detected secret's kind and its zero-based line and character range in a file, so
///     the extension can underline the exact literal in the editor and the Problems panel. The value is shown
///     redacted in any emitted payload; this diagnostic only points at where it lives in the source.
/// </summary>
/// <param name="Path">The normalized repository-relative file path.</param>
/// <param name="Kind">The secret kind (for example <c>github-token</c>).</param>
/// <param name="StartLine">Zero-based start line.</param>
/// <param name="StartColumn">Zero-based start character.</param>
/// <param name="EndLine">Zero-based end line.</param>
/// <param name="EndColumn">Zero-based end character (exclusive).</param>
public sealed record SecretDiagnosticDto(
    string Path,
    string Kind,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

/// <summary>
///     One token hotspot: a file whose estimated token cost is high enough to flag as an informational
///     diagnostic, so the budget pressure is visible in the editor as well as the hotspots tree.
/// </summary>
/// <param name="Path">The normalized repository-relative file path.</param>
/// <param name="TokenCost">The estimated token cost of including the file.</param>
public sealed record HotspotDiagnosticDto(string Path, int TokenCost);

/// <summary>
///     The result of the <c>fuse/diagnostics</c> method: the context diagnostics for a repository root. Secrets
///     carry precise spans; hotspots flag budget-heavy files; graph gaps name files the dependency graph leaves
///     unconnected (no inbound or outbound type reference), which often indicates dead or reflection-only code.
/// </summary>
/// <param name="Secrets">The detected secrets with their precise editor ranges.</param>
/// <param name="Hotspots">The most token-expensive files, most expensive first.</param>
/// <param name="GraphGaps">Files with no inbound or outbound dependency edge.</param>
/// <param name="Generated">Files detected as generated code (EF Core migrations and model snapshots).</param>
public sealed record DiagnosticsDto(
    IReadOnlyList<SecretDiagnosticDto> Secrets,
    IReadOnlyList<HotspotDiagnosticDto> Hotspots,
    IReadOnlyList<string> GraphGaps,
    IReadOnlyList<string> Generated);

/// <summary>
///     One planned file in an explain result: why a file was included (role), at what fidelity (tier), and its
///     relevance score, so the extension's scope-result and explainer panels can show the plan without emitting.
/// </summary>
/// <param name="Path">The normalized repository-relative file path.</param>
/// <param name="Role">The file's role (for example <c>Seed</c>, <c>Dependency</c>, <c>Changed</c>).</param>
/// <param name="Tier">The reduction tier the file was planned at (for example <c>Standard</c>, <c>Skeleton</c>).</param>
/// <param name="Score">The relevance score from scoping, or <c>0</c> when not scored.</param>
public sealed record ExplainFileDto(string Path, string Role, string Tier, double Score);

/// <summary>
///     The result of the <c>fuse/explain</c> method: the scoped result's context plan (which files a fusion
///     would include, their roles, tiers, and scores) computed without writing a payload.
/// </summary>
/// <param name="Mode">The scoping mode that produced the plan (<c>focus</c>, <c>search</c>, or <c>changes</c>).</param>
/// <param name="Files">The planned files, in plan order.</param>
public sealed record ExplainResultDto(string Mode, IReadOnlyList<ExplainFileDto> Files);

/// <summary>
///     One compiler diagnostic in a <c>fuse/check</c> delta (S3): the shape the ambient-verification hook renders
///     after an edit. Mirrors <see cref="Fuse.Indexing.CheckDiagnostic" /> as a wire DTO so the host RPC contract
///     does not leak the engine record.
/// </summary>
/// <param name="Id">The diagnostic id (for example <c>CS1061</c>).</param>
/// <param name="Severity">The severity (<c>Error</c>, <c>Warning</c>, <c>Info</c>, <c>Hidden</c>).</param>
/// <param name="Message">The diagnostic message.</param>
/// <param name="Path">The file the diagnostic is in, when known.</param>
/// <param name="Line">The 1-based line, when known.</param>
public sealed record CheckDiagnosticDto(string Id, string Severity, string Message, string? Path, int Line);

/// <summary>
///     The result of the <c>fuse/check</c> method (S3): the diagnostics a session's edits introduced or resolved
///     since its baseline, computed from a live resident workspace with no build. When no resident workspace
///     serves the root, <see cref="Resident" /> is false and both lists are empty, so an ambient-verification hook
///     exits silently rather than blocking editing.
/// </summary>
/// <param name="Resident">Whether a live resident workspace served the diagnostics (false means none did).</param>
/// <param name="Introduced">Diagnostics present now but not at the session baseline (the edits introduced them).</param>
/// <param name="Resolved">Diagnostics present at the baseline but not now (the edits resolved them).</param>
public sealed record CheckDeltaDto(
    bool Resident,
    IReadOnlyList<CheckDiagnosticDto> Introduced,
    IReadOnlyList<CheckDiagnosticDto> Resolved);

/// <summary>
///     The result of the <c>fuse/checkOverlay</c> method (G5): the diagnostics a proposed single-file edit would
///     produce, typechecked against the daemon's live resident workspace with no build. This is the resident-grade
///     answer a non-owner process (for example an <c>mcp serve</c> that delegates to the shared daemon) proxies
///     over the pipe, so the warm compilation is a shared asset rather than duplicated per process.
/// </summary>
/// <param name="HasResident">Whether a live resident workspace served the check (false means the daemon has none).</param>
/// <param name="Diagnostics">The diagnostics for the changed document, when a resident workspace served it.</param>
public sealed record CheckOverlayResultDto(
    bool HasResident,
    IReadOnlyList<CheckDiagnosticDto> Diagnostics);

/// <summary>
///     The result of the <c>fuse/openIndexed</c> method (R19, G5 phase 2): whether the daemon prepared a readable
///     index for store-backed MCP tools and the coarse state when it did not. A non-owner process delegates index
///     open, reconcile, syntax-first cold start, and an explicitly requested semantic job to the daemon over this RPC, then
///     opens the store read-only locally for queries.
/// </summary>
/// <param name="Status">
///     A coarse outcome: <c>ready</c>, <c>index_rebuilding</c>, or <c>not_indexed</c>.
/// </param>
/// <param name="Detail">An actionable detail when <paramref name="Status" /> is not <c>ready</c>.</param>
/// <param name="FileCount">The indexed file count when <paramref name="Status" /> is <c>ready</c>; otherwise zero.</param>
/// <param name="Mode">The index tier when ready (<c>semantic</c>, <c>partial</c>, or <c>syntax</c>).</param>
/// <param name="Job">The active job when a build is in progress, including semantic work after syntax is readable.</param>
public sealed record OpenIndexedResultDto(
    string Status,
    string? Detail,
    int FileCount,
    string? Mode,
    IndexJobSnapshot? Job = null);

/// <summary>The rendered live semantic-load diagnosis produced by the root's compiler-state owner.</summary>
/// <param name="Report">The unchanged doctor report text.</param>
public sealed record DoctorResultDto(string Report);

/// <summary>The input for a staged refactor executed by the root's compiler-state owner.</summary>
public sealed record RefactorRequestDto(
    string Symbol, string NewName, string Operation, string ContainingType, string ParameterType,
    string ParameterName, string Argument, string NewOrder, string DiagnosticId, string File);

/// <summary>The rendered staged refactor result produced by the root's compiler-state owner.</summary>
/// <param name="Output">The unchanged staged diff or abstention text.</param>
public sealed record RefactorResultDto(string Output);

/// <summary>The capture-bundle oracle result served by the root's pooled check-worker owner.</summary>
/// <param name="Available">Whether the host had a verified capture-backed verdict; false asks the caller to use its normal fallback.</param>
/// <param name="Reason">The capture oracle's abstention reason, when supplied.</param>
/// <param name="Diagnostics">The verified changed-file diagnostics when available.</param>
public sealed record CaptureCheckResultDto(bool Available, string? Reason, IReadOnlyList<CheckDiagnosticDto> Diagnostics);

