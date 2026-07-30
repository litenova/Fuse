using System.ComponentModel;
using Fuse.Cli.Services;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The <c>fuse_workspace</c> MCP tool: workspace status and lifecycle, plus the one explicit tree-write path.
/// </summary>
[McpServerToolType]
internal sealed class FuseWorkspaceHandler
{
    /// <summary>Runs one workspace action.</summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="jobs">The daemon-aware lifecycle client for index start, status, and cancellation.</param>
    /// <param name="action">The action: status, index, cancel, map, doctor, or apply.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="detail">For the map action: the detail to include (symbols, routes, all).</param>
    /// <param name="maxRows">For the map action: the maximum rows per section.</param>
    /// <param name="file">For the apply action: the repository-relative file to write.</param>
    /// <param name="content">For the apply action: the complete replacement content.</param>
    /// <param name="write">For the apply action: whether to write instead of returning a dry-run report.</param>
    /// <param name="expectedHash">For the apply action: the SHA-256 hash of the content the edit was derived from.</param>
    /// <param name="refresh">For the doctor action: whether to force a live MSBuild diagnosis.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The action's result, or a descriptive error.</returns>
    [McpServerTool(Name = "fuse_workspace", ReadOnly = false)]
    [Description("Workspace status and lifecycle (the loop's first stop). action=status (default): index mode, verification grade, freshness, and active job. action=index: start or join the syntax index job. action=cancel: stop the active index job. action=map: symbols, routes, and counts. action=doctor: daemon, configuration, storage, compiler-target, and job diagnostics. action=apply: write a proposed single-file edit (file + content) to the working tree; it is a dry run unless write=true and refuses paths outside the workspace root.")]
    public static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        IndexJobClient jobs,
        [Description("The action: status, index, cancel, map, doctor, or apply.")] string action = "status",
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("For the map action: detail to include (symbols, routes, all).")] string detail = "all",
        [Description("For the map action: maximum rows per section.")] int maxRows = 200,
        [Description("For the apply action: the repo-relative file to write.")] string file = "",
        [Description("For the apply action: the full new content to write to that file.")] string content = "",
        [Description("For the apply action: actually write (otherwise a dry run reports the change without writing).")] bool write = false,
        [Description("For the apply action: the SHA-256 (hex) of the file content this edit was derived from. When set, apply refuses if the file changed since (a concurrent edit), rather than clobbering it.")] string expectedHash = "",
        [Description("For the doctor action: force a live MSBuild load diagnosis instead of reporting the diagnosis stamped in the warm index (R43). Default false reports from the index in sub-second time when it is present.")] bool refresh = false,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        WorkspaceToolOperations.ExecuteAsync(
            indexer, jobs, action, path, detail, maxRows, file, content, write, expectedHash, refresh,
            cancellationToken, runtime);
}
