namespace Fuse.Cli.Rpc;

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
///     One secret diagnostic: a detected secret's kind and its zero-based line and character range in a file, so a
///     client can underline the exact literal. The value is shown redacted in any emitted payload; this diagnostic
///     only points at where it lives in the source.
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
///     diagnostic, so the budget pressure is visible alongside the hotspots list.
/// </summary>
/// <param name="Path">The normalized repository-relative file path.</param>
/// <param name="TokenCost">The estimated token cost of including the file.</param>
public sealed record HotspotDiagnosticDto(string Path, int TokenCost);
