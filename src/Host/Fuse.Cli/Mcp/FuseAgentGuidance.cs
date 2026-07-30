namespace Fuse.Cli.Mcp;

/// <summary>
///     Canonical instructions for routing agent work through the Fuse MCP tools.
/// </summary>
internal static class FuseAgentGuidance
{
    /// <summary>
    ///     Gets the managed Markdown block written by <c>fuse mcp install</c> unless <c>--no-rules</c> is set.
    /// </summary>
    internal static string RuleBody { get; } = string.Join(
        "\n",
        "For repository code tasks:",
        "1. Call fuse_workspace with action=status before broad discovery.",
        "2. Call fuse_find before broad file search when the task names a symbol, route, service, request, or configuration key.",
        "3. Call fuse_find with kind=task for open-ended localization.",
        "4. Call fuse_impact before changing a public signature.",
        "5. Call fuse_check after a proposed single-file edit.",
        "6. Call fuse_review before handoff.",
        "If Fuse reports unavailable or insufficient signal, follow its fallback and continue with native repository tools. Do not repeat a refused query.");

    /// <summary>
    ///     Gets the MCP initialization instructions advertised to every connected client.
    /// </summary>
    internal static string ServerInstructions { get; } = string.Join(
        "\n",
        "Fuse provides local .NET codebase context, typed framework wiring, change impact, focused tests, and compiler-backed checks. Start repository work with fuse_workspace action=status. Use fuse_find before broad repository search, fuse_impact before a public signature change, fuse_check after a proposed single-file edit, and fuse_review before handoff.",
        "",
        RuleBody,
        "",
        "Tool constraints:",
        "- Every tool except `fuse_reduce` requires a Git repository identity. A nested path resolves to the nearest repository root. An unresolved folder is refused before Fuse starts a daemon or writes an index.",
        "- A syntax index remains readable while an explicit semantic job runs. Call `fuse_workspace` with `action=status` for the active job, or `action=cancel` to stop it.",
        "- `fuse_check` checks one complete proposed file without writing it. It reports oracle grade, build grade, or an abstention.",
        "- `fuse_refactor` returns a staged solution-wide diff and never writes the working tree.",
        "- `fuse_workspace action=apply` accepts complete content for one file. It does not apply a multi-file patch.",
        "- The default MCP server delegates compiler state and index writes to one shared `fuse host` daemon per repository. Set `FUSE_DAEMON=0` to serve in-process.",
        "- `fuse_reduce` compacts known files or raw content outside the indexed workspace loop.");
}
