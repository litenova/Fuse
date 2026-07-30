using System.Text;
using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Reduction;
using Fuse.Retrieval;
using Fuse.Scoping;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Implements <c>fuse_review</c>: diff-first change impact plus the packed context, and the U2 handoff packet.
/// </summary>
internal static class ReviewToolOperations
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
    internal static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        IChangeSource changeSource,
        ContextSessionStore sessionStore,
        string path = ".",
        string changedSince = "HEAD",
        int maxTokens = 0,
        bool includeTests = true,
        string format = "xml",
        string? sessionId = null,
        bool handoff = false,
        string checkSession = "",
        int maxChangedFiles = 0,
        CancellationToken cancellationToken = default) =>
        FuseOperationalErrors.ExecuteMcpAsync(() => ReviewCoreAsync(
            indexer, reductionPipeline, changeSource, sessionStore, path, changedSince, maxTokens, includeTests,
            format, sessionId, handoff, checkSession, maxChangedFiles, cancellationToken));

    /// <summary>
    ///     Builds the U2 handoff packet: a paste-ready PR body for a change, gated by the check session's red state.
    /// </summary>
    /// <param name="indexer">The semantic indexer (opens the store for the session baseline).</param>
    /// <param name="changeSource">The change source for resolving the git base ref.</param>
    /// <param name="root">The repository root.</param>
    /// <param name="changedSince">The git base ref to diff against.</param>
    /// <param name="checkSession">The check session that gates the packet.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The handoff packet, the red-gate refusal, or a graceful abstention.</returns>
    /// <remarks>
    ///     It refuses (with the red summary) while the resident session has unresolved introduced errors - the
    ///     gate-not-controller stance in one behavior - and otherwise emits the changed files, the public API delta,
    ///     the compiler-gate status, and the named residual risk. The compiler gate is resident-only (like delta
    ///     mode); with no resident check session it is reported not gated rather than assumed green.
    /// </remarks>
    internal static async Task<string> BuildHandoffAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        string root,
        string changedSince,
        string checkSession,
        CancellationToken cancellationToken,
        FuseMcpRuntime? runtime = null)
    {
        // A tool never crashes the server: any failure below (a git spawn error, an unreadable base ref) returns a
        // graceful abstention string, not an exception. Cancellation propagates.
        try
        {
            return await BuildHandoffCoreAsync(
                IndexedStoreAccess.ResolveRuntime(runtime, indexer),
                indexer, changeSource, root, changedSince, checkSession, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"cannot build a handoff: {ex.Message}. A handoff needs a readable git base ref (changedSince) and, "
                + "for the red-gate, a resident check session.";
        }
    }

    private static async Task<string> ReviewCoreAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        IChangeSource changeSource,
        ContextSessionStore sessionStore,
        string path,
        string changedSince,
        int maxTokens,
        bool includeTests,
        string format,
        string? sessionId,
        bool handoff,
        string checkSession,
        int maxChangedFiles,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);

        // U2 handoff: a paste-ready PR packet, gated by the check session's red state (gate, not controller).
        if (handoff)
            return await BuildHandoffAsync(indexer, changeSource, root, changedSince, checkSession, cancellationToken);

        await using var store = await IndexedStoreAccess.OpenIndexedAsync(indexer, path, cancellationToken);

        // R26: bound a huge diff. Fetch the changed-file set first (a cheap git name-only diff); if it exceeds the
        // cap, return the changed-file list and a narrow-the-base-ref note rather than running blast-radius
        // resolution over hundreds of files unbounded. maxTokens bounds output; this bounds the graph work.
        var changedFiles = await changeSource.GetChangedFilesAsync(root, changedSince, cancellationToken);
        var cap = ReviewBounds.ResolveCap(maxChangedFiles);
        if (ReviewBounds.ShouldBound(changedFiles.Count, cap))
        {
            var header = await IndexAvailabilityReporter.OracleHeaderAsync(store, root, cancellationToken);
            return ReviewBounds.FormatBoundedReview(header, changedSince, changedFiles, cap);
        }

        var engine = new SemanticRetrievalEngine(store, changeSource);
        var plan = await engine.ReviewAsync(
            new ReviewRequest(root, changedSince, MaxTokens: maxTokens > 0 ? maxTokens : null, IncludeTests: includeTests),
            cancellationToken);

        var renderer = new SemanticContextRenderer(reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, cancellationToken);
        var unchanged = string.IsNullOrWhiteSpace(sessionId) ? null : sessionStore.Reconcile(sessionId, rendered.Files);

        var apiDeltaSection = await BuildApiDeltaSectionAsync(changeSource, root, changedSince, cancellationToken);
        var claimsSection = BuildClaimsSection(plan, changedSince, apiDeltaSection);
        return SemanticContextEmitter.Emit(
            plan, rendered, ContextFormat.Parse(format), root, changedSince, unchanged, apiDeltaSection, claimsSection);
    }

    // The U2 graded-claims block for a review: the changed-file set is git-truth (Verified), and the presence of a
    // public-API surface delta is a graph-grade inference (PartiallyVerified, the grade cap). Returns null when
    // there is nothing to claim, so the emitter omits the block.
    private static string? BuildClaimsSection(ContextPlan plan, string changedSince, string? apiDeltaSection)
    {
        var changedCount = plan.Items.Count(item => item.Role == "changed");
        var claims = new List<Claim>();
        if (changedCount > 0)
        {
            claims.Add(Claim.FromCompiler(
                $"{changedCount} changed file(s) are seeded as must-keep",
                $"git diff {changedSince}"));
        }

        if (!string.IsNullOrWhiteSpace(apiDeltaSection))
        {
            claims.Add(Claim.FromGraph(
                "the change alters the public API surface (see the api-delta section)",
                "graph: public-API delta (T2)"));
        }

        return claims.Count == 0 ? null : ClaimLedger.Render(claims);
    }

    // The T2 public-API delta section for a review: added, removed, and changed public/protected members between
    // the base ref and the working tree. Best-effort - a git or read failure returns null so the review payload is
    // unaffected (the delta is an added section, not the review itself). The base side is read from the git base
    // ref; the current side from the working tree.
    private static async Task<string?> BuildApiDeltaSectionAsync(
        IChangeSource changeSource,
        string root,
        string changedSince,
        CancellationToken cancellationToken)
    {
        try
        {
            var changed = await changeSource.GetChangedFilesAsync(root, changedSince, cancellationToken);
            var delta = await ChangedApiSurfaceGatherer.GatherAsync(
                changeSource,
                root,
                changedSince,
                changed,
                (relativePath, _) =>
                {
                    var absolute = Path.Combine(root, relativePath);
                    return Task.FromResult(File.Exists(absolute) ? File.ReadAllText(absolute) : null);
                },
                cancellationToken);

            return delta.Changes.Count == 0 ? null : ApiDeltaReport.Render(delta);
        }
        catch (ChangeSourceException)
        {
            return null;
        }
    }

    private static async Task<string> BuildHandoffCoreAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        IChangeSource changeSource,
        string root,
        string changedSince,
        string checkSession,
        CancellationToken cancellationToken)
    {
        var gate = await BuildCompilerGateAsync(runtime, indexer, root, checkSession, cancellationToken);
        if (gate.Refusal is not null)
            return gate.Refusal;

        IReadOnlyList<string> changed;
        try
        {
            changed = await changeSource.GetChangedFilesAsync(root, changedSince, cancellationToken);
        }
        catch (ChangeSourceException ex)
        {
            return $"cannot build a handoff: {ex.Message}. A handoff needs a git base ref (changedSince).";
        }

        var apiDelta = await BuildApiDeltaSectionAsync(changeSource, root, changedSince, cancellationToken);
        var packet = new StringBuilder();
        packet.AppendLine($"# Handoff: changes since {changedSince}");
        packet.AppendLine();
        packet.AppendLine(gate.StatusLine);
        packet.AppendLine();
        packet.AppendLine($"## Changed files ({changed.Count})");
        foreach (var file in changed)
            packet.AppendLine($"- {file}");
        packet.AppendLine();
        packet.AppendLine("## Public API delta");
        packet.AppendLine(apiDelta ?? "No public or protected API change (internal-only change).");
        packet.AppendLine();
        packet.AppendLine("## Tests");
        packet.AppendLine("Run fuse_test on the changed symbols for covering-test verdicts (not included in this packet).");
        packet.AppendLine();
        packet.AppendLine("## Residual risk");
        packet.AppendLine("- Reflection, dynamic dispatch, and cross-boundary references by name (config strings, DI keys) are outside the compiler and graph view; verify them manually.");
        if (apiDelta is not null)
            packet.AppendLine("- The public API delta above may break external callers; confirm the change is intended.");
        return packet.ToString().TrimEnd();
    }

    private static async Task<(string StatusLine, string? Refusal)> BuildCompilerGateAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        string root,
        string checkSession,
        CancellationToken cancellationToken)
    {
        const string NotGated =
            "compiler status: not gated (no resident check session; run fuse_check --delta or re-check before merge).";

        var current = runtime.ResidentWorkspaces.TryGetCurrentDiagnostics(root, cancellationToken);
        if (string.IsNullOrWhiteSpace(checkSession) || current is null)
            return (NotGated, null);

        await using var store = await IndexedStoreAccess.OpenIndexedAsync(runtime, indexer, root, cancellationToken);
        var baseline = await store.GetCheckSessionBaselineAsync(checkSession, cancellationToken);
        if (baseline is null)
        {
            return ($"compiler status: session '{checkSession}' has no baseline yet; establish one with fuse_check "
                + "--delta, then re-check before merge.", null);
        }

        var delta = DiagnosticDelta.Compute(baseline.Diagnostics, current);
        var introducedErrors = delta.Introduced.Where(diagnostic => diagnostic.Severity == "Error").ToList();
        if (introducedErrors.Count == 0)
        {
            return ($"compiler status: green (no unresolved introduced errors in session '{checkSession}', "
                + $"resident-verified since {baseline.UpdatedUtc}).", null);
        }

        var refusal = new StringBuilder();
        refusal.AppendLine(
            $"handoff refused: the session has {introducedErrors.Count} unresolved introduced error(s). Resolve them "
            + "before handoff (Fuse gates, it does not commit for you).");
        foreach (var diagnostic in introducedErrors)
            refusal.AppendLine($"  {diagnostic.Id} {diagnostic.FilePath}:{diagnostic.Line}: {diagnostic.Message}");
        return (NotGated, refusal.ToString().TrimEnd());
    }
}
