namespace Fuse.Cli.Rpc;

/// <summary>
///     The result of the <c>fuse/explain</c> method: the scoped result's context plan (which files a fusion would
///     include, their roles, tiers, and scores) computed without writing a payload.
/// </summary>
/// <param name="Mode">The scoping mode that produced the plan (<c>focus</c>, <c>search</c>, or <c>changes</c>).</param>
/// <param name="Files">The planned files, in plan order.</param>
public sealed record ExplainResultDto(string Mode, IReadOnlyList<ExplainFileDto> Files);

/// <summary>
///     One planned file in an explain result: why a file was included (role), at what fidelity (tier), and its
///     relevance score, so a caller can show the plan without emitting.
/// </summary>
/// <param name="Path">The normalized repository-relative file path.</param>
/// <param name="Role">The file's role (for example <c>Seed</c>, <c>Dependency</c>, <c>Changed</c>).</param>
/// <param name="Tier">The reduction tier the file was planned at (for example <c>Standard</c>, <c>Skeleton</c>).</param>
/// <param name="Score">The relevance score from scoping, or <c>0</c> when not scored.</param>
public sealed record ExplainFileDto(string Path, string Role, string Tier, double Score);
