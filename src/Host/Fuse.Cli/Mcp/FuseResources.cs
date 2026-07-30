using System.ComponentModel;
using System.Text;
using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Indexing;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP resource definitions for Fuse V3, exposed to AI agents through the Model Context Protocol server.
/// </summary>
/// <remarks>
///     Each method backs an MCP resource addressed by a <c>fuse://</c> URI template. The resources mirror the
///     read workflows of the equivalent <see cref="FuseTools" /> tools over the persistent semantic index: the
///     index is built on first use, no files are written, and errors are returned as descriptive strings rather
///     than thrown. Use the tools for full control; the resources are the fixed-default addressable form.
/// </remarks>
[McpServerResourceType]
public sealed class FuseResources
{
    /// <summary>
    ///     Reads a workspace map: indexed symbols, routes, and counts. The cheap first call.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="path">Relative path to the workspace directory.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The workspace map, or a descriptive error message when the directory is missing.</returns>
    [McpServerResource(
        UriTemplate = "fuse://map/{path}",
        Name = "Workspace Map",
        MimeType = "text/plain")]
    [Description("Returns a map of the indexed workspace (symbols, routes, counts). Mirrors fuse_workspace (action=map).")]
    public static async Task<string> ReadMapResourceAsync(
        SemanticIndexer indexer,
        [Description("Relative path to the workspace directory.")] string path,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        if (!TryResolveRoot(path, out var root, out var error))
            return error;
        await using var store = await OpenIndexedAsync(ResolveRuntime(runtime, indexer), indexer, root, cancellationToken);
        var renderer = new WorkspaceMapRenderer(store);
        return await renderer.RenderAsync(MapDetail.All, maxRows: 200, cancellationToken);
    }

    /// <summary>
    ///     Reads a session's accumulated claims ledger (U2): the graded, evidence-referenced claims Fuse emitted
    ///     across the session, re-graded stale where their evidence has since changed. The running evidence trail.
    /// </summary>
    /// <param name="indexer">The semantic indexer (opens the store; the ledger lives in the workspace store).</param>
    /// <param name="path">Relative path to the workspace directory.</param>
    /// <param name="session">The session id whose ledger to read.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The rendered claims ledger, or a note when the session has no accumulated claims.</returns>
    [McpServerResource(
        UriTemplate = "fuse://ledger/{path}/{session}",
        Name = "Session Claim Ledger",
        MimeType = "text/plain")]
    [Description("Returns a session's accumulated graded claims (U2): the evidence trail Fuse emitted across the session, each claim graded and evidence-referenced.")]
    public static async Task<string> ReadClaimLedgerResourceAsync(
        SemanticIndexer indexer,
        [Description("Relative path to the workspace directory.")] string path,
        [Description("The session id whose claim ledger to read.")] string session,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        if (!TryResolveRoot(path, out var root, out var error))
            return error;
        await using var store = await OpenIndexedAsync(ResolveRuntime(runtime, indexer), indexer, root, cancellationToken);
        var claims = await SessionClaimLedger.LoadAsync(store, session, cancellationToken);
        if (claims.Count == 0)
            return $"session '{session}': no accumulated claims yet. Claim-emitting tools (for example fuse_impact with a session) add to the ledger.";
        return $"session '{session}' claim ledger:\n" + ClaimLedger.Render(claims);
    }

    /// <summary>
    ///     Reads the workspace status (U3): the availability header (index mode, verification grade, freshness) and
    ///     the resolved workspace root and index mode. The addressable form of <c>fuse_workspace action=status</c>.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="path">Relative path to the workspace directory.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The rendered status, or a descriptive error message when the directory is missing.</returns>
    [McpServerResource(
        UriTemplate = "fuse://status/{path}",
        Name = "Workspace Status",
        MimeType = "text/plain")]
    [Description("Returns the workspace status (index mode, verification grade, freshness). Mirrors fuse_workspace (action=status).")]
    public static async Task<string> ReadStatusResourceAsync(
        SemanticIndexer indexer,
        [Description("Relative path to the workspace directory.")] string path,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        if (!TryResolveRoot(path, out var root, out var error))
            return error;
        var toolRuntime = ResolveRuntime(runtime, indexer);
        await using var store = await OpenIndexedAsync(toolRuntime, indexer, root, cancellationToken);
        var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
        var builder = new StringBuilder();
        builder.AppendLine(await FuseTools.OracleAvailabilityHeaderAsync(
            store, root, cancellationToken, residentWorkspaces: toolRuntime.ResidentWorkspaces));
        builder.AppendLine($"workspace: {root}");
        builder.AppendLine($"index mode: {mode}");
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Reads a session's diagnostics diff (U3): the compiler diagnostics the session's on-disk edits introduced
    ///     or resolved since its baseline, read from a live resident workspace. The addressable, read-only form of
    ///     <c>fuse_check --delta</c>; unlike the tool it never establishes a baseline (a resource read is idempotent).
    /// </summary>
    /// <param name="indexer">The semantic indexer (opens the store; the baseline lives in the workspace store).</param>
    /// <param name="path">Relative path to the workspace directory.</param>
    /// <param name="session">The session id whose diff to read.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The rendered diff, or a note when no resident workspace serves the root or the session has no baseline.</returns>
    [McpServerResource(
        UriTemplate = "fuse://diff/{path}/{session}",
        Name = "Session Diff",
        MimeType = "text/plain")]
    [Description("Returns the diagnostics a session's edits introduced or resolved since its baseline (read-only). Mirrors fuse_check --delta.")]
    public static async Task<string> ReadSessionDiffResourceAsync(
        SemanticIndexer indexer,
        [Description("Relative path to the workspace directory.")] string path,
        [Description("The session id whose diagnostics diff to read.")] string session,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        if (!TryResolveRoot(path, out var root, out var error))
            return error;

        var toolRuntime = ResolveRuntime(runtime, indexer);
        var current = toolRuntime.ResidentWorkspaces.TryGetCurrentDiagnostics(root, cancellationToken);
        if (current is null)
            return "no diff: no resident workspace serves this root (start the server with FUSE_RESIDENT=1); the diff never runs a build.";

        await using var store = await OpenIndexedAsync(toolRuntime, indexer, root, cancellationToken);
        var baseline = await store.GetCheckSessionBaselineAsync(session, cancellationToken);
        if (baseline is null)
            return $"session '{session}': no baseline recorded yet. Call fuse_check with this session to establish one, then read the diff.";

        var delta = DiagnosticDelta.Compute(baseline.Diagnostics, current);
        var builder = new StringBuilder();
        builder.AppendLine($"session '{session}' diff (since baseline {baseline.UpdatedUtc}): {delta.Introduced.Count} introduced, {delta.Resolved.Count} resolved.");
        if (delta.Introduced.Count == 0 && delta.Resolved.Count == 0)
        {
            builder.AppendLine("  (no change in diagnostics since the baseline)");
            return builder.ToString().TrimEnd();
        }

        if (delta.Introduced.Count > 0)
        {
            builder.AppendLine("introduced:");
            foreach (var d in delta.Introduced)
                builder.AppendLine($"  {d.Severity} {d.Id} {d.FilePath}:{d.Line}: {d.Message}");
        }

        if (delta.Resolved.Count > 0)
        {
            builder.AppendLine("resolved:");
            foreach (var d in delta.Resolved)
                builder.AppendLine($"  {d.Severity} {d.Id} {d.FilePath}:{d.Line}: {d.Message}");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Reads a session's current diagnostics (U3): the whole-state compiler diagnostics from a live resident
    ///     workspace, or the session's recorded baseline set when no resident workspace serves the root. Read-only.
    /// </summary>
    /// <param name="indexer">The semantic indexer (opens the store for the recorded baseline).</param>
    /// <param name="path">Relative path to the workspace directory.</param>
    /// <param name="session">The session id whose diagnostics to read.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The rendered diagnostics, or a note when neither a resident workspace nor a recorded baseline exists.</returns>
    [McpServerResource(
        UriTemplate = "fuse://diagnostics/{path}/{session}",
        Name = "Session Diagnostics",
        MimeType = "text/plain")]
    [Description("Returns a session's whole-state diagnostics (live resident set, or the recorded baseline). Read-only.")]
    public static async Task<string> ReadSessionDiagnosticsResourceAsync(
        SemanticIndexer indexer,
        [Description("Relative path to the workspace directory.")] string path,
        [Description("The session id whose diagnostics to read.")] string session,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        if (!TryResolveRoot(path, out var root, out var error))
            return error;

        var toolRuntime = ResolveRuntime(runtime, indexer);
        var current = toolRuntime.ResidentWorkspaces.TryGetCurrentDiagnostics(root, cancellationToken);
        var builder = new StringBuilder();
        if (current is not null)
        {
            builder.AppendLine($"session '{session}' diagnostics (live resident): {current.Count} diagnostic(s).");
            foreach (var d in current)
                builder.AppendLine($"  {d.Severity} {d.Id} {d.FilePath}:{d.Line}: {d.Message}");
            return builder.ToString().TrimEnd();
        }

        await using var store = await OpenIndexedAsync(toolRuntime, indexer, root, cancellationToken);
        var baseline = await store.GetCheckSessionBaselineAsync(session, cancellationToken);
        if (baseline is null)
            return $"session '{session}': no live resident workspace and no recorded baseline. Start the server with FUSE_RESIDENT=1, or call fuse_check with this session first.";

        builder.AppendLine($"session '{session}' diagnostics (recorded baseline as of {baseline.UpdatedUtc}): {baseline.Diagnostics.Count} diagnostic(s).");
        foreach (var d in baseline.Diagnostics)
            builder.AppendLine($"  {d.Severity} {d.Id} {d.FilePath}:{d.Line}: {d.Message}");
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Reads ranked candidate files and symbols for a task, with no source bodies. Mirrors fuse_find (kind=task).
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="changeSource">The change source (unused for a title-only query, passed for parity).</param>
    /// <param name="path">Relative path to the workspace directory.</param>
    /// <param name="query">The task or query to localize.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The ranked candidates, or a descriptive error message when the directory is missing.</returns>
    [McpServerResource(
        UriTemplate = "fuse://localize/{path}/{query}",
        Name = "Localized Candidates",
        MimeType = "text/plain")]
    [Description("Returns ranked candidate files and symbols for a task, no source bodies. Mirrors fuse_find (kind=task).")]
    public static async Task<string> ReadLocalizeResourceAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        [Description("Relative path to the workspace directory.")] string path,
        [Description("The task or query to localize.")] string query,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        if (!TryResolveRoot(path, out var root, out var error))
            return error;
        await using var store = await OpenIndexedAsync(ResolveRuntime(runtime, indexer), indexer, root, cancellationToken);
        var engine = new SemanticRetrievalEngine(store, changeSource);
        var result = await engine.LocalizeAsync(new LocalizationRequest(root, Query: query), cancellationToken);
        if (result.Candidates.Count == 0)
            return "No candidates found for the query.";
        return string.Join("\n", result.Candidates.Select(c =>
            $"{c.Score:F2}  {c.Path}  [{c.Kind}]  ~{c.EstimatedTokens} tok  {string.Join("; ", c.Reasons)}"));
    }

    /// <summary>
    ///     Reads planned and emitted context for a single named seed (symbol, service, request, or config).
    ///     Mirrors fuse_context.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render bodies.</param>
    /// <param name="path">Relative path to the workspace directory.</param>
    /// <param name="seed">A symbol, service, request, or config-section seed.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The emitted context payload, or a descriptive error message when the directory is missing.</returns>
    [McpServerResource(
        UriTemplate = "fuse://context/{path}/{seed}",
        Name = "Seeded Context",
        MimeType = "text/plain")]
    [Description("Returns planned and emitted context (source bodies, manifest, provenance) for a named seed. Mirrors fuse_context.")]
    public static async Task<string> ReadContextResourceAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        [Description("Relative path to the workspace directory.")] string path,
        [Description("A symbol, service, request, or config-section seed.")] string seed,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        if (!TryResolveRoot(path, out var root, out var error))
            return error;
        await using var store = await OpenIndexedAsync(ResolveRuntime(runtime, indexer), indexer, root, cancellationToken);
        var engine = new SemanticRetrievalEngine(store);
        var plan = await engine.PlanContextAsync(
            new ContextRequest(root, [new ContextSeed(ContextSeedKind.Symbol, seed)]), cancellationToken);
        var renderer = new SemanticContextRenderer(reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, cancellationToken);
        return SemanticContextEmitter.Emit(plan, rendered, ContextOutputFormat.Xml, root);
    }

    /// <summary>
    ///     Reads the semantic impact of a change since a git base ref, with the packed context. Mirrors fuse_review.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render bodies.</param>
    /// <param name="changeSource">The change source for resolving the git base ref.</param>
    /// <param name="path">Relative path to the workspace directory.</param>
    /// <param name="since">Git ref (branch, commit, or <c>HEAD~N</c>) to diff against.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The review payload, or a descriptive error message when the directory is missing.</returns>
    [McpServerResource(
        UriTemplate = "fuse://review/{path}/{since}",
        Name = "Change Review Context",
        MimeType = "text/plain")]
    [Description("Returns the semantic impact of a change since a git ref (changed files, blast radius, packed context). Mirrors fuse_review.")]
    public static async Task<string> ReadReviewResourceAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        IChangeSource changeSource,
        [Description("Relative path to the workspace directory.")] string path,
        [Description("Git ref to diff against (branch, commit, HEAD~N).")] string since,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        if (!TryResolveRoot(path, out var root, out var error))
            return error;
        await using var store = await OpenIndexedAsync(ResolveRuntime(runtime, indexer), indexer, root, cancellationToken);
        var engine = new SemanticRetrievalEngine(store, changeSource);
        var plan = await engine.ReviewAsync(new ReviewRequest(root, since), cancellationToken);
        var renderer = new SemanticContextRenderer(reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, cancellationToken);
        return SemanticContextEmitter.Emit(plan, rendered, ContextOutputFormat.Xml, root, since);
    }

    private static Task<WorkspaceIndexStore> OpenIndexedAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        string path,
        CancellationToken cancellationToken) =>
        runtime.IndexAccess.OpenIndexedAsync(indexer, path, cancellationToken);

    private static FuseMcpRuntime ResolveRuntime(FuseMcpRuntime? runtime, SemanticIndexer indexer) =>
        runtime ?? FuseMcpRuntime.CreateIsolated(indexer);

    private static bool TryResolveRoot(string path, out string root, out string error)
    {
        try
        {
            root = WorkspacePathResolver.ResolveRepositoryRoot(path);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is WorkspaceIdentityException or DirectoryNotFoundException)
        {
            root = string.Empty;
            error = FuseOperationalErrors.FromException(ex);
            return false;
        }
    }
}
