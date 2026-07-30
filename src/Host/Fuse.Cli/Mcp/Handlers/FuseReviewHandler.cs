using System.ComponentModel;
using Fuse.Context;
using Fuse.Reduction;
using Fuse.Retrieval;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The <c>fuse_review</c> MCP tool: diff-first change impact and the packed context for handoff.
/// </summary>
[McpServerToolType]
internal sealed class FuseReviewHandler
{
    /// <summary>Reviews the semantic impact of a change and emits the packed context.</summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render bodies.</param>
    /// <param name="changeSource">The change source for resolving the git base ref.</param>
    /// <param name="sessionStore">The session store used to elide unchanged files.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="changedSince">The git base ref to diff against.</param>
    /// <param name="maxTokens">The token budget, or zero for none.</param>
    /// <param name="includeTests">Whether to include related test files.</param>
    /// <param name="format">The output format: xml, markdown, or json.</param>
    /// <param name="sessionId">Session id; files already sent unchanged in the session are elided.</param>
    /// <param name="handoff">Whether to render a handoff packet instead of review context.</param>
    /// <param name="checkSession">The check session that gates a handoff packet.</param>
    /// <param name="maxChangedFiles">The maximum changed files before a partial review response.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The review preamble plus the emitted context payload.</returns>
    [McpServerTool(Name = "fuse_review", ReadOnly = true)]
    [Description("Review the semantic impact of a change since a git base ref: changed files, the blast radius (callers, DI consumers, route/request handlers, options consumers, tests), and the packed context. The flagship tool for PR/change work.")]
    public static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        IChangeSource changeSource,
        ContextSessionStore sessionStore,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The git base ref to diff against (branch, commit, or HEAD~N).")] string changedSince = "HEAD",
        [Description("Token budget; changed files are always kept.")] int maxTokens = 0,
        [Description("Include related test files.")] bool includeTests = true,
        [Description("Output format: xml (default), markdown, or json.")] string format = "xml",
        [Description("Session id; files already sent unchanged in this session are elided.")] string? sessionId = null,
        [Description("Produce a paste-ready PR handoff packet instead of the review context; refuses while the check session has unresolved introduced errors (U2).")] bool handoff = false,
        [Description("For handoff: the fuse_check session id to gate on (refuses while it has unresolved introduced errors).")] string checkSession = "",
        [Description("Maximum changed files before review returns a bounded partial (changed-file list only). 0 uses FUSE_REVIEW_MAX_CHANGED_FILES or the default of 150.")] int maxChangedFiles = 0,
        CancellationToken cancellationToken = default) =>
        ReviewToolOperations.ExecuteAsync(
            indexer, reductionPipeline, changeSource, sessionStore, path, changedSince, maxTokens, includeTests,
            format, sessionId, handoff, checkSession, maxChangedFiles, cancellationToken);
}
