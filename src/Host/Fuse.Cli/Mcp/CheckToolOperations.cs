using System.Diagnostics;
using System.Text;
using Fuse.Cli.Rpc;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Implements <c>fuse_check</c>: typecheck a proposed single-file edit without writing it, at the best
///     available verification grade (T0, Decision D11).
/// </summary>
/// <remarks>
///     Verification never shrugs. It answers at oracle grade (a speculative typecheck against the build-captured
///     compilation, no build) when tier-1 is available, falls back to build grade (running <c>dotnet build</c>
///     scoped to the owning project and parsing the same diagnostic shape) otherwise, and abstains only when even
///     the toolchain cannot run. Every answer is stamped with its grade.
/// </remarks>
internal static class CheckToolOperations
{
    private const string RepairPacketsOmittedNote =
        "repair packets: omitted (index unavailable for symbol enrichment; the verification verdict is unchanged).";

    private static readonly TimeSpan HostRoutingTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CapturedLogCheckBudget = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OwningProjectCheckBudget = TimeSpan.FromMinutes(10);

    /// <summary>Runs the check tool, mapping an unexpected failure to an operational error.</summary>
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
    internal static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        string path = ".",
        string file = "",
        string content = "",
        string session = "",
        bool full = false,
        bool markGreen = false,
        bool analyzers = true,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        FuseOperationalErrors.ExecuteMcpAsync(() => CheckCoreAsync(
            IndexedStoreAccess.ResolveRuntime(runtime, indexer),
            path, file, content, session, full, markGreen, analyzers, cancellationToken));

    /// <summary>
    ///     Answers oracle-grade from a capture bundle's compiler log(s) when one is stamped into the index.
    /// </summary>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <param name="root">The repository root.</param>
    /// <param name="file">The repository-relative path of the file being changed.</param>
    /// <param name="content">The proposed full new content of that file.</param>
    /// <param name="client">The build-capture client used for the spawn-per-call fallback.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The verified result, or null when no captured log can answer.</returns>
    internal static async Task<CheckResult?> TryOracleFromCaptureBundleAsync(
        FuseMcpRuntime runtime,
        string root,
        string file,
        string content,
        BuildCaptureClient client,
        CancellationToken cancellationToken)
    {
        if (!client.IsAvailable)
            return null;

        await using var store = await IndexedStoreAccess.TryOpenForEnrichmentAsync(runtime, root, cancellationToken);
        if (store is null)
            return null;

        var bundleDirectory = await store.GetMetaAsync(WorkspaceIndexStore.CaptureComplogPathMetaKey, cancellationToken);
        if (string.IsNullOrEmpty(bundleDirectory))
            return null;

        var logs = Directory.Exists(bundleDirectory)
            ? CaptureBundleIo.CompilerLogPaths(bundleDirectory)
            : File.Exists(bundleDirectory) ? [bundleDirectory] : [];
        foreach (var log in logs)
        {
            // R48: try the pooled, kept-alive worker first (rehydrate once, reuse across checks in a session); on a
            // cold/absent/failed pooled worker it returns null and we fall back to the spawn-per-call path, so the
            // verdict and honesty are unchanged and it is never worse than today.
            var pooled = await runtime.PooledCheckWorkers.TryCheckAsync(log, file, content, cancellationToken, ownerRoot: root);
            var candidate = pooled
                ?? await client.CheckFromComplogAsync(log, file, content, CapturedLogCheckBudget, cancellationToken);
            if (candidate.Verified)
                return candidate;
        }

        return null;
    }

    private static async Task<string> CheckCoreAsync(
        FuseMcpRuntime runtime,
        string path,
        string file,
        string content,
        string session,
        bool full,
        bool markGreen,
        bool analyzers,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);

        // Delta mode (S2): a session with no proposed content asks "what did my last on-disk edit change", diffed
        // against the persisted session baseline. The content path below is unchanged when content is supplied.
        if (string.IsNullOrEmpty(content) && !string.IsNullOrWhiteSpace(session))
            return await DeltaAsync(runtime, root, session, full, markGreen, cancellationToken);

        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrEmpty(content))
            return "Error: provide the changed file path and its proposed new content (or a session id for delta mode).";

        var (fileResolved, absoluteFile, fileError) = WorkspacePathResolver.ResolveWorkspacePath(root, file, "check");
        if (!fileResolved)
            return fileError!;
        file = WorkspacePathResolver.ToRepoRelative(root, absoluteFile!);

        var ownership = await new ProjectOwnershipResolver().ResolveAsync(root, file, cancellationToken);

        // R18: verification is compiler-tier and runs before any mandatory index open, so index contention cannot
        // block a build-grade answer when dotnet build could verify. Repair-packet enrichment is indexed-tier and
        // best-effort afterward.
        var (result, buildElapsedMs) = await VerifyAsync(runtime, root, ownership, file, content, analyzers, cancellationToken);

        if (!result.Verified)
            return $"cannot verify ({result.Grade}): {result.Reason}";

        var gradeLine = result.Grade == "oracle"
            ? "verification grade: oracle (speculative typecheck, no build, no disk write)"
            : $"verification grade: build (ran dotnet build scoped to the owning project, {buildElapsedMs / 1000.0:F1}s, no disk write)";

        if (result.IsClean)
            return $"{gradeLine}\nclean: no errors in the changed document {file}.";

        var builder = new StringBuilder();
        builder.AppendLine(gradeLine);
        builder.AppendLine($"diagnostics for {file}: {result.Diagnostics.Count}");
        foreach (var diagnostic in result.Diagnostics)
            builder.AppendLine($"  {diagnostic.Severity} {diagnostic.Id} at line {diagnostic.Line}: {diagnostic.Message}");

        AppendRepairPackets(builder, await BuildRepairPacketsAsync(runtime, root, result.Diagnostics, cancellationToken));
        return builder.ToString().TrimEnd();
    }

    // Delta mode (S2): compute the diagnostics introduced or resolved since a persisted session baseline. Current
    // whole-state diagnostics come from a live resident workspace (delta mode must not run a build); the baseline
    // is persisted so a restarted process resumes it. On session start (no baseline) the current set is recorded as
    // the baseline; markGreen resets it to current; full returns the whole current set.
    private static async Task<string> DeltaAsync(
        FuseMcpRuntime runtime,
        string root,
        string session,
        bool full,
        bool markGreen,
        CancellationToken cancellationToken)
    {
        var current = runtime.ResidentWorkspaces.TryGetCurrentDiagnostics(root, cancellationToken);
        if (current is null)
        {
            return "cannot compute delta (abstain): no resident workspace serves this root, and delta mode does not run a build. "
                + "Start the server with FUSE_RESIDENT=1 for delta mode, or pass file and content for a speculative single-file check.";
        }

        await using var store = await IndexedStoreAccess.TryOpenForSessionBaselineAsync(runtime, root, cancellationToken);
        if (store is null)
        {
            return "cannot compute delta (abstain): index unavailable for the session baseline (locked or not built). "
                + "Retry when the index is free, or pass file and content for a speculative single-file check.";
        }

        if (markGreen)
        {
            await store.SaveCheckSessionBaselineAsync(session, root, current, cancellationToken);
            return $"delta mode: session '{session}' baseline reset (marked green) to {current.Count} current diagnostic(s). Later deltas are measured from here.";
        }

        if (full)
        {
            var all = new StringBuilder();
            all.AppendLine($"delta mode (resident): full diagnostic set, {current.Count} diagnostic(s).");
            foreach (var diagnostic in current)
                all.AppendLine($"  {diagnostic.Severity} {diagnostic.Id} {diagnostic.FilePath}:{diagnostic.Line}: {diagnostic.Message}");
            return all.ToString().TrimEnd();
        }

        var baseline = await store.GetCheckSessionBaselineAsync(session, cancellationToken);
        if (baseline is null)
        {
            await store.SaveCheckSessionBaselineAsync(session, root, current, cancellationToken);
            return $"delta mode: session '{session}' established with a {current.Count}-diagnostic baseline. Edit, then call again with the same session to see what your change introduced or resolved.";
        }

        var delta = DiagnosticDelta.Compute(baseline.Diagnostics, current);
        var builder = new StringBuilder();
        builder.AppendLine($"delta mode (resident, since baseline {baseline.UpdatedUtc}): {delta.Introduced.Count} introduced, {delta.Resolved.Count} resolved.");

        if (delta.Introduced.Count == 0 && delta.Resolved.Count == 0)
        {
            builder.AppendLine("  (no change in diagnostics since the baseline)");
            return builder.ToString().TrimEnd();
        }

        if (delta.Introduced.Count > 0)
        {
            builder.AppendLine("introduced (attributed to your changes since the baseline):");
            foreach (var diagnostic in delta.Introduced)
                builder.AppendLine($"  {diagnostic.Severity} {diagnostic.Id} {diagnostic.FilePath}:{diagnostic.Line}: {diagnostic.Message}");
        }

        if (delta.Resolved.Count > 0)
        {
            builder.AppendLine("resolved:");
            foreach (var diagnostic in delta.Resolved)
                builder.AppendLine($"  {diagnostic.Severity} {diagnostic.Id} {diagnostic.FilePath}:{diagnostic.Line}: {diagnostic.Message}");
        }

        AppendRepairPackets(builder, await BuildRepairPacketsAsync(runtime, root, delta.Introduced, cancellationToken, store));
        return builder.ToString().TrimEnd();
    }

    // The verification-grade ladder (T0, D11): oracle first, build-grade fallback, abstain only when neither runs.
    // Compiler-tier: no mandatory index open (R18).
    private static async Task<(CheckResult Result, long BuildElapsedMs)> VerifyAsync(
        FuseMcpRuntime runtime,
        string root,
        ProjectOwnership ownership,
        string file,
        string content,
        bool analyzers,
        CancellationToken cancellationToken)
    {
        var client = new BuildCaptureClient(processRunner: runtime.ProcessRunner);
        var oracle = await TryOracleAsync(runtime, root, ownership, file, content, analyzers, client, cancellationToken);
        if (oracle is not null)
            return (oracle, 0);

        if (!ownership.IsResolved)
            return (CheckResult.Abstain("no project found to build (no oracle-grade capture and no buildable project)."), 0);

        var stopwatch = Stopwatch.StartNew();
        var buildResult = await new BuildGradeChecker(processRunner: runtime.ProcessRunner)
            .CheckAsync(root, ownership, file, content, cancellationToken);
        return (buildResult, stopwatch.ElapsedMilliseconds);
    }

    private static async Task<CheckResult?> TryOracleAsync(
        FuseMcpRuntime runtime,
        string root,
        ProjectOwnership ownership,
        string file,
        string content,
        bool analyzers,
        BuildCaptureClient client,
        CancellationToken cancellationToken)
    {
        var residentDiagnostics = await runtime.ResidentWorkspaces.TryCheckOverlayAsync(
            root, file, content, analyzers, cancellationToken);
        if (residentDiagnostics is not null)
            return CheckResult.Ok(residentDiagnostics);

        if (Commands.McpServeCommand.IsDaemonEnabled())
        {
            var remote = await FuseHostClient.TryCaptureCheckAsync(root, file, content, HostRoutingTimeout, cancellationToken);
            if (remote is { Available: true })
            {
                return CheckResult.Ok(remote.Diagnostics
                    .Select(diagnostic => new CheckDiagnostic(
                        diagnostic.Id, diagnostic.Severity, diagnostic.Message, diagnostic.Path, diagnostic.Line))
                    .ToList());
            }
        }

        var fromBundle = await TryOracleFromCaptureBundleAsync(runtime, root, file, content, client, cancellationToken);
        if (fromBundle is not null)
            return fromBundle;

        return client.IsAvailable && ownership.IsResolved
            ? await TryOracleFromOwningProjectsAsync(client, ownership, file, content, cancellationToken)
            : null;
    }

    private static async Task<CheckResult?> TryOracleFromOwningProjectsAsync(
        BuildCaptureClient client,
        ProjectOwnership ownership,
        string file,
        string content,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<CheckDiagnostic>();
        foreach (var project in ownership.ProjectPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = await client.CheckAsync(project, file, content, OwningProjectCheckBudget, cancellationToken);
            if (!candidate.Verified)
                return null;
            diagnostics.AddRange(candidate.Diagnostics);
        }

        return CheckResult.Ok(diagnostics.Distinct().ToList());
    }

    private static async Task<(IReadOnlyList<RepairPacket> Packets, string? OmittedNote)> BuildRepairPacketsAsync(
        FuseMcpRuntime runtime,
        string root,
        IReadOnlyList<CheckDiagnostic> diagnostics,
        CancellationToken cancellationToken,
        WorkspaceIndexStore? store = null)
    {
        var ownedStore = store is null;
        store ??= await IndexedStoreAccess.TryOpenForEnrichmentAsync(runtime, root, cancellationToken);
        if (store is null)
            return ([], RepairPacketsOmittedNote);

        try
        {
            var packetBuilder = new RepairPacketBuilder(store);
            var packets = new List<RepairPacket>();
            foreach (var diagnostic in diagnostics.Where(diagnostic => diagnostic.Severity == "Error"))
            {
                var packet = await packetBuilder.BuildAsync(diagnostic, cancellationToken);
                if (packet is not null)
                    packets.Add(packet);
            }

            return (packets, null);
        }
        finally
        {
            if (ownedStore)
                await store.DisposeAsync();
        }
    }

    private static void AppendRepairPackets(
        StringBuilder builder,
        (IReadOnlyList<RepairPacket> Packets, string? OmittedNote) repair)
    {
        if (repair.Packets.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("repair packets:");
            foreach (var packet in repair.Packets)
            {
                builder.AppendLine($"  [{packet.DiagnosticId}] {packet.Explanation}");
                if (packet.TopRepair is { } repairAction)
                    builder.AppendLine($"    apply: replace '{repairAction.OldToken}' with '{repairAction.NewToken}'");
                foreach (var member in packet.Members.Take(12))
                    builder.AppendLine($"    {(string.IsNullOrEmpty(member.Signature) ? member.Name : member.Signature)}");
            }
        }
        else if (repair.OmittedNote is not null)
        {
            builder.AppendLine();
            builder.AppendLine(repair.OmittedNote);
        }
    }
}
