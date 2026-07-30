namespace Fuse.Cli.Rpc;

/// <summary>
///     The result of the <c>fuse/scope</c> method: the files a scoped fusion included with their token costs, the
///     total token count, and a path to the emitted payload written to a temp file the caller opens read-only.
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

/// <summary>One emitted file in a scope result: the path the scope included and its token cost.</summary>
/// <param name="Path">The normalized repository-relative file path.</param>
/// <param name="TokenCost">The token cost the file contributed to the emitted payload.</param>
public sealed record ScopeFileDto(string Path, int TokenCost);
