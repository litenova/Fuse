using Fuse.Cli.Mcp;

namespace Fuse.Cli.Rpc;

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

/// <summary>The indexed file count for one language.</summary>
/// <param name="Language">The language tag (for example <c>csharp</c>), or <c>unknown</c>.</param>
/// <param name="Count">The number of indexed files carrying the tag.</param>
public sealed record LanguageCountDto(string Language, int Count);

/// <summary>
///     The result of the <c>fuse/openIndexed</c> method (R19, G5 phase 2): whether the daemon prepared a readable
///     index for store-backed MCP tools, and the coarse state when it did not. A non-owner process delegates index
///     open, reconcile, syntax-first cold start, and an explicitly requested semantic job to the daemon over this
///     RPC, then opens the store read-only locally for queries.
/// </summary>
/// <param name="Status">A coarse outcome: <c>ready</c>, <c>index_rebuilding</c>, or <c>not_indexed</c>.</param>
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
