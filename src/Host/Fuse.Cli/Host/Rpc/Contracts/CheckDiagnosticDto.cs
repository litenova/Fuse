namespace Fuse.Cli.Rpc;

/// <summary>
///     One compiler diagnostic on the host RPC wire. Mirrors <see cref="Fuse.Indexing.CheckDiagnostic" /> as a
///     wire DTO so the host RPC contract does not leak the engine record.
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

/// <summary>The capture-bundle oracle result served by the root's pooled check-worker owner.</summary>
/// <param name="Available">Whether the host had a verified capture-backed verdict; false asks the caller to use its normal fallback.</param>
/// <param name="Reason">The capture oracle's abstention reason, when supplied.</param>
/// <param name="Diagnostics">The verified changed-file diagnostics when available.</param>
public sealed record CaptureCheckResultDto(bool Available, string? Reason, IReadOnlyList<CheckDiagnosticDto> Diagnostics);
