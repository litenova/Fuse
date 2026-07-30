using System.Text;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Implements <c>fuse_impact</c>: the blast radius for a symbol before an edit, and the NuGet package-upgrade
///     break set (F3).
/// </summary>
/// <remarks>
///     The enumeration comes from the persisted semantic graph (R5 reference edges plus the wiring edges). The
///     precise signature-change break set needs an oracle-grade load and is reported unavailable otherwise, per
///     the availability contract; it is never guessed.
/// </remarks>
internal static class ImpactToolOperations
{
    /// <summary>Runs the impact tool and maps a not-ready index to its availability header.</summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="symbol">The symbol whose blast radius to compute.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="limit">The maximum impacted items to return.</param>
    /// <param name="package">The optional NuGet package id for package-upgrade analysis.</param>
    /// <param name="fromVersion">The installed NuGet package version for package-upgrade analysis.</param>
    /// <param name="toVersion">The target NuGet package version for package-upgrade analysis.</param>
    /// <param name="session">The optional claim-ledger session id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The impacted files and symbols with the edge that connects them, plus an availability note.</returns>
    internal static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        string symbol = "",
        string path = ".",
        int limit = 50,
        string package = "",
        string fromVersion = "",
        string toVersion = "",
        string session = "",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        IndexedStoreAccess.ExecuteReadMcpAsync(() => ImpactCoreAsync(
            IndexedStoreAccess.ResolveRuntime(runtime, indexer),
            indexer, symbol, path, limit, package, fromVersion, toVersion, session, cancellationToken));

    private static async Task<string> ImpactCoreAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        string symbol,
        string path,
        int limit,
        string package,
        string fromVersion,
        string toVersion,
        string session,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);

        // Package-upgrade mode (F3) reads the NuGet cache rather than the workspace index, but it remains a
        // repository-scoped Fuse operation. Resolve identity first so fuse_reduce stays the only MCP utility that
        // accepts a folder outside Git.
        if (!string.IsNullOrWhiteSpace(package))
        {
            if (string.IsNullOrWhiteSpace(fromVersion) || string.IsNullOrWhiteSpace(toVersion))
                return "Error: package-upgrade mode needs package, fromVersion, and toVersion.";
            return RenderPackageUpgrade(PackageUpgradeOracle.AnalyzeCachedVersions(package, fromVersion, toVersion));
        }

        if (string.IsNullOrWhiteSpace(symbol))
            return "Error: provide a symbol name (or package + fromVersion + toVersion for a package-upgrade analysis).";

        await using var store = await IndexedStoreAccess.OpenIndexedAsync(runtime, indexer, root, cancellationToken);
        var mode = await store.GetMetaAsync(WorkspaceIndexStore.IndexModeMetaKey, cancellationToken) ?? "unknown";
        var explorer = new GraphNeighborhoodExplorer(store);
        var impact = await explorer.CallersAndImplementersAsync(symbol, limit, cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine(await IndexAvailabilityReporter.OracleHeaderAsync(
            store, root, cancellationToken, residentWorkspaces: runtime.ResidentWorkspaces));
        builder.AppendLine($"impact of {symbol}: {impact.Count} impacted (index mode {mode})");
        foreach (var item in impact)
            builder.AppendLine($"  {item.Path}{FormatSymbolSuffix(item)}  [{item.Reason}]");

        if (impact.Count == 0 && mode == "syntax")
            builder.AppendLine("  (no edges: syntax mode has no semantic graph; run fuse_workspace action=index on a semantically loadable checkout)");

        builder.AppendLine();
        builder.AppendLine(await BuildApiSurfaceLineAsync(store, symbol, mode, cancellationToken));

        // M1 covering-test selection (down-payment): the tests that reach the symbol through R5's DI-resolved
        // tests edges, called out distinctly from the blast radius so an agent can run just this subset. Best-
        // effort and bounded by R5 edge completeness, so it is labeled a lower bound, never "all the tests".
        var covering = await explorer.CoveringTestsAsync(symbol, limit, cancellationToken);
        builder.AppendLine();
        builder.AppendLine($"covering tests: {covering.Count} (a lower bound from R5 tests edges; run with your own --filter)");
        foreach (var test in covering)
            builder.AppendLine($"  {test.Path}  {test.Symbol}");

        // Availability contract: the exact signature-change break set is an oracle-grade answer (a bind-check
        // against a resident compilation). No tier-1 load exists yet, so it is reported unavailable, not guessed.
        builder.AppendLine();
        builder.AppendLine(
            "signature-change break set: unavailable (needs an oracle-grade tier-1 load; the enumeration above is "
            + "the graph-grade blast radius from the persisted reference and wiring edges).");

        // The graded claims block (U2): the impact answer's statements, each graded from the evidence behind it.
        // Both rest on the persisted graph, so they cap at partially_verified (not compiler-confirmed).
        var claims = new List<Claim>
        {
            Claim.FromGraph($"{impact.Count} caller(s)/implementer(s) reach {symbol}", $"graph: {mode} reference and wiring edges"),
            Claim.FromGraph($"{covering.Count} covering test(s) reach {symbol} (a lower bound)", "graph: R5 tests edges"),
        };
        builder.AppendLine();
        builder.AppendLine(ClaimLedger.Render(claims));

        // U2 ledger: when a session is given, accumulate this call's claims so the session-ledger resource can
        // report the running evidence trail across the task.
        if (!string.IsNullOrWhiteSpace(session))
            await SessionClaimLedger.AppendAsync(store, session, root, claims, cancellationToken);

        return builder.ToString();
    }

    // Renders a package-upgrade analysis (F3): the breaking public-API changes between two versions, or an abstention.
    private static string RenderPackageUpgrade(PackageUpgradeReport report)
    {
        if (!report.Available)
            return $"package upgrade {report.PackageId}: cannot analyze - {report.Reason}";

        var builder = new StringBuilder();
        var verdict = report.HasBreaking
            ? $"{report.BreakingChanges.Count} BREAKING public-API change(s)"
            : "no breaking public-API changes";
        builder.AppendLine($"package upgrade {report.PackageId}: {verdict}, {report.AdditiveChanges.Count} additive");
        foreach (var change in report.BreakingChanges)
            builder.AppendLine($"  BREAK [{change.Kind}] {change.Symbol}{(change.Before is null ? string.Empty : $"  (was: {change.Before})")}");
        if (report.BreakingChanges.Count == 0)
            builder.AppendLine("  (the target version keeps every public/protected member of the referenced version)");

        builder.AppendLine();
        builder.AppendLine(report.BlindSpots);
        return builder.ToString().TrimEnd();
    }

    // The T2 public-surface line for impact: whether the target symbol is on the public/protected API surface, so
    // an agent knows before editing whether a change is contract-relevant. Conservative (the T2 kill-risk): a
    // positive "public" is asserted only from IsPublicApi, which is reliable for types in any mode and for members
    // at the semantic tier; a member in syntax mode, where accessibility is unresolved, is reported undetermined
    // rather than guessed either way.
    private static async Task<string> BuildApiSurfaceLineAsync(
        WorkspaceIndexStore store,
        string symbol,
        string mode,
        CancellationToken cancellationToken)
    {
        var matches = await store.FindSymbolsByNameAsync(symbol, 50, cancellationToken);
        var exact = matches
            .Where(match => string.Equals(match.Name, symbol, StringComparison.Ordinal)
                || string.Equals(match.FullyQualifiedName, symbol, StringComparison.Ordinal)
                || match.FullyQualifiedName.EndsWith("." + symbol, StringComparison.Ordinal))
            .ToList();

        if (exact.Count == 0)
            return $"public API surface: no indexed symbol named {symbol} to classify.";

        if (exact.Any(match => match.IsPublicApi))
        {
            return $"public API surface: {symbol} is on the public/protected surface (T2); removing it, reducing its "
                + "accessibility, or changing its signature is a breaking change, so the blast radius above is external-facing.";
        }

        // Not flagged public. For a member in syntax mode the flag is not reliable, so do not assert "internal".
        if (mode == "syntax" && exact.All(match => !IsTypeKind(match.Kind)))
        {
            return $"public API surface: undetermined for {symbol} in syntax mode (member accessibility is not "
                + "resolved); run fuse_workspace action=index on a semantically loadable checkout to classify.";
        }

        return $"public API surface: {symbol} is not on the public/protected surface; a change is internal to the assembly.";
    }

    private static bool IsTypeKind(string kind) =>
        kind is "class" or "interface" or "struct" or "record" or "enum" or "delegate" or "type";

    private static string FormatSymbolSuffix(ExploredItem item) =>
        item.Symbol is null ? string.Empty : $"  {item.Symbol}";
}
