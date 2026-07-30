using System.ComponentModel;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The <c>fuse_check</c> MCP tool: typecheck a proposed single-file edit at the best available grade.
/// </summary>
[McpServerToolType]
internal sealed class FuseCheckHandler
{
    /// <summary>Typechecks a proposed single-file edit without writing it.</summary>
    /// <param name="indexer">The semantic indexer (opens the store for repair-packet context).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="file">The repository-relative path of the file being changed.</param>
    /// <param name="content">The proposed full new content of that file.</param>
    /// <param name="session">The optional resident-diagnostics session id.</param>
    /// <param name="full">Whether resident diagnostics should return the full set instead of a delta.</param>
    /// <param name="markGreen">Whether to reset a resident-diagnostics session baseline.</param>
    /// <param name="analyzers">Whether the resident check should include configured analyzers.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The diagnostics for the changed document, a clean verdict, or an explicit abstention.</returns>
    [McpServerTool(Name = "fuse_check", ReadOnly = true)]
    [Description("Speculatively typecheck a proposed single-file edit: the compiler errors and warnings it would produce, without writing the file. Verification never shrugs (D11): oracle-grade (sub-second, no build) when the repo is captured at tier-1; otherwise build-grade, running dotnet build scoped to the owning project (tens of seconds) and parsing the same diagnostics; abstains only when even the toolchain cannot run, naming the reason. Every answer is stamped with its grade. Delta mode (S2): pass a session id with no content to get the diagnostics your on-disk edits introduced or resolved since the session baseline (needs a resident workspace; does not run a build); full:true returns the whole current set; markGreen:true resets the baseline to now. Analyzer parity (S4): when a resident workspace serves the root, analyzers:true (the default) also runs the repo's configured analyzers and nullable warnings at their editorconfig severities, so a green check matches CI.")]
    public static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The repo-relative path of the file being changed.")] string file = "",
        [Description("The proposed full new content of that file.")] string content = "",
        [Description("Delta mode: a session id. With no content, returns the diagnostics introduced or resolved since the session baseline (needs a resident workspace; does not run a build).")] string session = "",
        [Description("Delta mode: return the whole current diagnostic set instead of the delta since the baseline.")] bool full = false,
        [Description("Delta mode: reset the session baseline to the current diagnostics (mark green), so later deltas are measured from here.")] bool markGreen = false,
        [Description("Also run the repo's configured analyzers and nullable warnings at their editorconfig severities (CI parity), when a resident workspace serves the root. Default on.")] bool analyzers = true,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        CheckToolOperations.ExecuteAsync(
            indexer, path, file, content, session, full, markGreen, analyzers, cancellationToken, runtime);
}
