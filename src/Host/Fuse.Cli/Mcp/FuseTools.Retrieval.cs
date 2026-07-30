using System.ComponentModel;
using System.Text;
using Fuse.Cli.Rpc;
using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Indexing;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;
using Fuse.Scoping;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The retrieval MCP tools (localize, resolve, context, review) over the persistent semantic index.
/// </summary>
public sealed partial class FuseTools
{
    /// <summary>
    ///     Localizes a task to ranked candidate files and symbols (no source bodies).
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="changeSource">The change source for resolving a git base ref.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="task">The free-text task or query.</param>
    /// <param name="route">A route to resolve.</param>
    /// <param name="symbol">A symbol to focus on.</param>
    /// <param name="service">A service to resolve.</param>
    /// <param name="request">A request or command to resolve.</param>
    /// <param name="config">A config section to resolve.</param>
    /// <param name="changedSince">A git base ref whose changed files seed candidates.</param>
    /// <param name="maxCandidates">The maximum candidates to return.</param>
    /// <param name="strict">When true, an insufficient request is refused and only a navigation map is returned; off by default (best-effort).</param>
    /// <param name="expand">When true, the selected candidates are enriched with their typed-graph neighbors for discovery; off by default.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>Ranked candidates with reasons and token costs, or a navigation map when the request is not confident.</returns>
    // Folded into fuse_find (kind=task) in U1; kept as an internal helper the find union calls.
    public static async Task<string> FuseLocalizeAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The task or query to localize.")] string? task = null,
        [Description("A route to resolve, for example \"POST /api/orders/{id}\".")] string? route = null,
        [Description("A symbol to focus on.")] string? symbol = null,
        [Description("A service to resolve to its implementation.")] string? service = null,
        [Description("A request/command to resolve to its handler.")] string? request = null,
        [Description("A config section to resolve to its options type.")] string? config = null,
        [Description("A git base ref whose changed files seed the candidates.")] string? changedSince = null,
        [Description("Maximum candidates to return.")] int maxCandidates = 50,
        [Description("Strict signal-sufficiency: when an insufficient request has no clear anchor, refuse and return only a navigation map instead of a low-confidence guess. Off by default (best-effort).")] bool strict = false,
        [Description("Expand the selected candidates with their typed-graph neighbors (implementers, callers, config) for discovery. Off by default; widens recall but pressures precision.")] bool expand = false,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        var toolRuntime = ResolveRuntime(runtime, indexer);
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        await using var store = await OpenIndexedAsync(toolRuntime, indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store, changeSource);
        var requestModel = new LocalizationRequest(
            root, Query: task, ChangedSince: changedSince, Route: route, Focus: symbol, Service: service,
            Request: request, ConfigSection: config, MaxCandidates: maxCandidates, Strict: strict, ExpandGraph: expand);
        var result = await engine.LocalizeAsync(requestModel, cancellationToken);
        return LocalizationFormatter.Format(result);
    }

    /// <summary>
    ///     Batch exact-signature lookup: for a set of symbol names, returns each declared signature, kind,
    ///     accessibility, and location from the semantic index in one call. The compiler-shaped answer to the
    ///     agent's most common question ("what is the exact shape of this member"), replacing many grep-and-read
    ///     round-trips.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="names">The symbol names to look up (simple or fully qualified).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="limitPerName">The maximum matches to return per requested name.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The signatures grouped by requested name, with a note for any name that did not match.</returns>
    // Folded into fuse_find (kind=signatures) in U1; kept as an internal helper the find union calls.
    public static async Task<string> FuseSignaturesAsync(
        SemanticIndexer indexer,
        [Description("Symbol names to look up (simple name or fully qualified).")] string[] names,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Maximum matches to return per requested name.")] int limitPerName = 5,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        if (names is null || names.Length == 0)
            return "Error: provide one or more symbol names in 'names'.";

        var toolRuntime = ResolveRuntime(runtime, indexer);
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        await using var store = await OpenIndexedAsync(toolRuntime, indexer, path, cancellationToken);
        var matches = await store.GetSignaturesByNamesAsync(names, limitPerName, cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine(await OracleAvailabilityHeaderAsync(
            store, root, cancellationToken, residentWorkspaces: toolRuntime.ResidentWorkspaces));
        foreach (var requested in names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.Ordinal))
        {
            builder.AppendLine($"# {requested}");

            // U1b: resident-first. When a live resident workspace serves the root it resolves a qualified name
            // (including a referenced package's API) from the compiler's real metadata, so a package signature is
            // answered from the compiler rather than the store (which never indexed the package). Fall through to
            // the store when no resident workspace serves the root, or it did not resolve this name.
            var residentSignatures = toolRuntime.ResidentWorkspaces.TryGetSignature(root, requested, limitPerName, cancellationToken);
            if (residentSignatures is { Count: > 0 })
            {
                foreach (var s in residentSignatures)
                {
                    var residentContainer = string.IsNullOrEmpty(s.Container) ? "" : $" in {s.Container}";
                    builder.AppendLine($"  {s.Signature}{residentContainer}");
                    builder.AppendLine($"    [{s.Kind}] resident (metadata: {s.Assembly})");
                }

                continue;
            }

            var forName = matches
                .Where(m => string.Equals(m.Name, requested, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(m.FullyQualifiedName, requested, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (forName.Count == 0)
            {
                builder.AppendLine("  no match in the index (check the name, or run fuse_workspace action=index if the file is new).");
                continue;
            }

            foreach (var m in forName)
            {
                var accessibility = string.IsNullOrEmpty(m.Accessibility) ? "" : m.Accessibility + " ";
                var signature = string.IsNullOrEmpty(m.Signature)
                    ? $"{m.Kind} {m.FullyQualifiedName} (no signature recorded; index is syntax-tier for this file)"
                    : $"{accessibility}{m.Signature}";
                var container = string.IsNullOrEmpty(m.ContainingType) ? "" : $" in {m.ContainingType}";
                builder.AppendLine($"  {signature}{container}");
                builder.AppendLine($"    {m.FilePath}:{m.StartLine} [{m.Kind}{(m.IsPublicApi ? ", public-api" : "")}]");
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Iterative exploration primitives: the graph neighborhood of a file, the callers and implementers of a
    ///     symbol, or the structurally central files of an area. Ranked, bounded, and body-free, for chaining.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="file">A file whose graph neighborhood to return.</param>
    /// <param name="symbol">A symbol whose callers and implementers to return.</param>
    /// <param name="centralIn">An area (folder prefix, or empty for the whole workspace) whose central files to return.</param>
    /// <param name="limit">The maximum results to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The ranked exploration items with provenance and no bodies.</returns>
    // Folded into fuse_find (kind=neighbors) in U1; kept as an internal helper the find union calls.
    public static async Task<string> FuseNeighborsAsync(
        SemanticIndexer indexer,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("A file whose graph neighborhood to return.")] string? file = null,
        [Description("A symbol whose callers and implementers to return.")] string? symbol = null,
        [Description("An area (folder prefix, or empty for the whole workspace) whose central files to return.")] string? centralIn = null,
        [Description("Maximum results to return.")] int limit = 20,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        var toolRuntime = ResolveRuntime(runtime, indexer);
        await using var store = await OpenIndexedAsync(toolRuntime, indexer, path, cancellationToken);
        var explorer = new GraphNeighborhoodExplorer(store);

        string mode;
        IReadOnlyList<ExploredItem> items;
        if (!string.IsNullOrWhiteSpace(file))
        {
            mode = $"neighborhood of {file}";
            items = await explorer.NeighborhoodAsync(file, limit, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(symbol))
        {
            mode = $"callers and implementers of {symbol}";
            items = await explorer.CallersAndImplementersAsync(symbol, limit, cancellationToken);
        }
        else if (centralIn is not null)
        {
            mode = centralIn.Length == 0 ? "central files (workspace)" : $"central files in {centralIn}";
            items = await explorer.CentralFilesAsync(centralIn, limit, cancellationToken);
        }
        else
        {
            return "Error: specify one of file, symbol, or centralIn.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"neighbors ({mode}): {items.Count}");
        foreach (var item in items)
        {
            var symbolPart = item.Symbol is null ? string.Empty : $"  {item.Symbol}";
            builder.AppendLine($"  {item.Path}{symbolPart}  [{item.Reason}]");
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Blast radius for a symbol before an edit: the callers, implementers, consumers, and referencing types a
    ///     change to it would touch, enumerated from the persisted semantic graph (R5's reference edges plus the
    ///     wiring edges). The precise signature-change break set requires an oracle-grade load and is reported as
    ///     unavailable otherwise, per the availability contract.
    /// </summary>
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
    [McpServerTool(Name = "fuse_impact", ReadOnly = true)]
    [Description("Blast radius for a symbol before you edit it: the callers, implementers, consumers, and referencing types a change would touch, from the persisted semantic graph. No bodies. The exact signature-change break set (which call sites would no longer bind) needs an oracle-grade (tier-1) load and is reported unavailable otherwise, rather than guessed. Package-upgrade mode (F3): pass package + fromVersion + toVersion to get the public-API break set between two cached NuGet package versions (removed/changed public members), so a bump's risk is knowable before the lockfile changes; it abstains when a version is not in the local cache and names its blind spots.")]
    public static Task<string> FuseImpactAsync(
        SemanticIndexer indexer,
        [Description("The symbol (simple or qualified name) whose blast radius to compute.")] string symbol = "",
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Maximum impacted items to return.")] int limit = 50,
        [Description("Package-upgrade mode: the NuGet package id whose bump to analyze.")] string package = "",
        [Description("Package-upgrade mode: the currently referenced version.")] string fromVersion = "",
        [Description("Package-upgrade mode: the target (upgrade) version.")] string toVersion = "",
        [Description("Optional session id: when set, this call's graded claims are appended to the session's claim ledger (U2).")] string session = "",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        FuseOperationalErrors.ExecuteMcpAsync(() => FuseImpactCoreAsync(
            ResolveRuntime(runtime, indexer), indexer, symbol, path, limit, package, fromVersion, toVersion, session, cancellationToken));

    private static async Task<string> FuseImpactCoreAsync(
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
            return RenderPackageUpgrade(Fuse.Retrieval.PackageUpgradeOracle.AnalyzeCachedVersions(package, fromVersion, toVersion));
        }

        if (string.IsNullOrWhiteSpace(symbol))
            return "Error: provide a symbol name (or package + fromVersion + toVersion for a package-upgrade analysis).";

        await using var store = await OpenIndexedAsync(runtime, indexer, root, cancellationToken);
        var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
        var explorer = new GraphNeighborhoodExplorer(store);
        var impact = await explorer.CallersAndImplementersAsync(symbol, limit, cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine(await OracleAvailabilityHeaderAsync(
            store, root, cancellationToken, residentWorkspaces: runtime.ResidentWorkspaces));
        builder.AppendLine($"impact of {symbol}: {impact.Count} impacted (index mode {mode})");
        foreach (var item in impact)
        {
            var symbolPart = item.Symbol is null ? string.Empty : $"  {item.Symbol}";
            builder.AppendLine($"  {item.Path}{symbolPart}  [{item.Reason}]");
        }

        if (impact.Count == 0 && mode == "syntax")
            builder.AppendLine("  (no edges: syntax mode has no semantic graph; run fuse_workspace action=index on a semantically loadable checkout)");

        builder.AppendLine();
        builder.AppendLine(await BuildImpactApiSurfaceLineAsync(store, symbol, mode, cancellationToken));

        // M1 covering-test selection (down-payment): the tests that reach the symbol through R5's DI-resolved
        // tests edges, called out distinctly from the blast radius so an agent can run just this subset. Best-
        // effort and bounded by R5 edge completeness, so it is labeled a lower bound, never "all the tests".
        var covering = await explorer.CoveringTestsAsync(symbol, limit, cancellationToken);
        builder.AppendLine();
        builder.AppendLine($"covering tests: {covering.Count} (a lower bound from R5 tests edges; run with your own --filter)");
        foreach (var t in covering)
            builder.AppendLine($"  {t.Path}  {t.Symbol}");

        // Availability contract: the exact signature-change break set is an oracle-grade answer (a bind-check
        // against a resident compilation). No tier-1 load exists yet, so it is reported unavailable, not guessed.
        builder.AppendLine();
        builder.AppendLine(
            "signature-change break set: unavailable (needs an oracle-grade tier-1 load; the enumeration above is " +
            "the graph-grade blast radius from the persisted reference and wiring edges).");

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

    /// <summary>
    ///     Runs the covering tests for a symbol (T1): the tests that reach it through the persisted <c>tests</c>
    ///     edges, run at build grade (<c>dotnet test</c> scoped by filter to just those test types), with the whole
    ///     suite never run. Selection-only when no <c>tests</c> edge reaches the symbol; the grade is stamped.
    /// </summary>
    /// <param name="indexer">The semantic indexer (opens the store for covering selection).</param>
    /// <param name="symbol">The symbol whose covering tests to run.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="limit">The maximum covering test types to run.</param>
    /// <param name="candidates">
    ///     Candidate racing (F2): a JSON array of single-file edits to race, each <c>{id?, file, content}</c>. When
    ///     non-empty, races the candidates' speculative typechecks in parallel instead of running covering tests.
    /// </param>
    /// <param name="maxCandidates">The bound on the number of candidates a race accepts (default four).</param>
    /// <param name="analyzers">Whether a race also runs the repo's configured analyzers against each overlay.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The per-test verdicts plus the grade, or the selection-only floor when nothing covers the symbol.</returns>
    [McpServerTool(Name = "fuse_test", ReadOnly = true)]
    [Description("Run the covering tests for a symbol: the tests that reach it through the persisted tests edges, run at build grade (dotnet test scoped by filter to just those test types, the whole suite never run), with per-test verdicts. Selection-only when no tests edge reaches the symbol. Build-grade runs the real build; the emit fast path is future work. Candidate racing (F2): pass candidates (a JSON array of {id?, file, content} single-file edits, bounded k) to speculatively typecheck all of them over the live resident compilation and get per-candidate diagnostics plus a winner by strict dominance (a lone clean candidate beats any with errors; ties reported); each candidate reuses the shared held compilation (only its own changed file rebinds), racing needs a resident workspace (FUSE_RESIDENT=1) and never applies a candidate.")]
    public static Task<string> FuseTestAsync(
        SemanticIndexer indexer,
        [Description("The symbol whose covering tests to run.")] string symbol = "",
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Maximum covering test types to run.")] int limit = 20,
        [Description("Candidate racing (F2): a JSON array of single-file edits to race, each {id?, file, content}. When set, races them through the speculative typecheck instead of running the covering tests.")] string candidates = "",
        [Description("Candidate racing: the maximum number of candidates accepted (the bound on k; default 4).")] int maxCandidates = 4,
        [Description("Candidate racing: also run the repo's configured analyzers against each candidate overlay (CI parity). Default on.")] bool analyzers = true,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        ExecuteReadMcpAsync(() => FuseTestCoreAsync(
            ResolveRuntime(runtime, indexer), indexer, symbol, path, limit, candidates, maxCandidates, analyzers, cancellationToken));

    private static async Task<string> FuseTestCoreAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        string symbol,
        string path,
        int limit,
        string candidates,
        int maxCandidates,
        bool analyzers,
        CancellationToken cancellationToken)
    {
        // Candidate racing (F2): when candidates are supplied, race their speculative typechecks in parallel over
        // the shared resident compilation and return per-candidate diagnostics plus a strict-dominance winner. This
        // is a distinct verb from the symbol-covering-test run below (no symbol needed).
        if (!string.IsNullOrWhiteSpace(candidates))
            return await FuseTestRaceAsync(runtime, WorkspacePathResolver.ResolveRepositoryRoot(path), candidates, maxCandidates, analyzers, cancellationToken);

        if (string.IsNullOrWhiteSpace(symbol))
            return "Error: provide a symbol name whose covering tests to run (or candidates to race).";

        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        // Covering selection is indexed-tier (R5 tests edges). Validate or warm the complete repository index
        // before selecting tests, using the same bounded cold-start and contention behavior as other indexed reads.
        await using var store = await OpenIndexedAsync(runtime, indexer, root, cancellationToken);
        var covering = await new GraphNeighborhoodExplorer(store).CoveringTestsAsync(symbol, limit, cancellationToken);
        if (covering.Count == 0)
            return $"covering tests for {symbol}: none (no tests edge reaches it, so there is nothing to run). This is the selection-only floor; a test reached only by reflection has no edge and is not selected.";

        var discovery = await new Fuse.Semantics.DotNetWorkspaceDiscoverer().DiscoverAsync(root, cancellationToken);
        var target = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (target is null)
            return $"covering tests for {symbol}: {covering.Count} test type(s) selected, but no solution or project was found to run them (selection-only).";

        var filter = Fuse.Workspace.TestFilterBuilder.BuildContains(covering.Select(c => c.Symbol));
        var scratch = Path.Combine(Path.GetTempPath(), "fuse-test", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await Fuse.Workspace.BuildGradeTestRunner.RunAsync(
                target, filter, scratch, TimeSpan.FromMinutes(10), cancellationToken);

            var builder = new StringBuilder();
            builder.AppendLine("verification grade: build (ran dotnet test scoped to the covering tests; the emit fast path is future work)");
            if (result.TimedOut)
            {
                builder.AppendLine($"covering tests for {symbol}: timed out and the test host was killed. Narrow the change or raise the budget.");
                return builder.ToString().TrimEnd();
            }

            if (result.Diagnostics is not null)
            {
                builder.AppendLine($"covering tests for {symbol}: {result.Diagnostics}");
                return builder.ToString().TrimEnd();
            }

            var passed = result.Verdicts.Count(v => v.Outcome == "passed");
            var failed = result.Verdicts.Count(v => v.Outcome == "failed");
            var notRun = result.Verdicts.Count(v => v.Outcome == "not-run");
            builder.AppendLine($"covering tests for {symbol}: {covering.Count} test type(s), {result.Verdicts.Count} test(s) run - {passed} passed, {failed} failed, {notRun} not-run");
            foreach (var verdict in result.Verdicts.OrderBy(v => v.Outcome == "failed" ? 0 : 1).ThenBy(v => v.Name, StringComparer.Ordinal))
                builder.AppendLine($"  {verdict.Outcome} {verdict.Name}");

            // Covering types that produced no verdict are reported not-runnable by name, never counted green.
            var notRunnable = Fuse.Workspace.CoveringRunAnalysis.NotRunnableTypes(
                covering.Select(c => c.Symbol).ToList(), result.Verdicts);
            if (notRunnable.Count > 0)
            {
                builder.AppendLine($"not-runnable ({notRunnable.Count}; selected but produced no result - a collection error or no runnable test):");
                foreach (var type in notRunnable)
                    builder.AppendLine($"  {type}");
            }

            // The graded claims block (U2): a test verdict is compiler/test-grade truth (the real dotnet test ran),
            // so this claim is verified - the strongest grade, distinct from the graph-grade impact claims.
            builder.AppendLine();
            builder.AppendLine(ClaimLedger.Render(
            [
                Claim.FromCompiler(
                    $"{passed} of {result.Verdicts.Count} covering test(s) passed ({failed} failed) for {symbol}",
                    "test: build-grade dotnet test run over the covering set"),
            ]));

            return builder.ToString().TrimEnd();
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    // Candidate racing (F2): parse the candidates argument, verify their speculative typechecks over the live
    // resident compilation (each candidate forks the immutable base and rebinds only its own changed tree, so k
    // candidates cost far less than k full verifies), and render per-candidate diagnostics plus a winner by strict
    // dominance. Candidates are evaluated one fork at a time (F2's Fallback: measured, concurrent Roslyn binding
    // over shared-base forks serializes, so parallelism buys no wall-clock and only multiplies fork memory).
    // Racing is the fork-cheap typecheck primitive; per-candidate test execution needs the emit path (T1's
    // descoped follow-up), so a race reports diagnostics, not test verdicts. Abstains when no resident workspace
    // serves the root (a build per candidate would not be a race), naming the requirement.
    private static async Task<string> FuseTestRaceAsync(
        FuseMcpRuntime runtime,
        string root,
        string candidatesJson,
        int maxCandidates,
        bool analyzers,
        CancellationToken cancellationToken)
    {
        RaceCandidateInput[]? parsed;
        try
        {
            parsed = System.Text.Json.JsonSerializer.Deserialize(
                candidatesJson, Fuse.Cli.Serialization.FuseCliJsonContext.Default.RaceCandidateInputArray);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return $"Error: candidates must be a JSON array of {{id?, file, content}} objects ({ex.Message}).";
        }

        if (parsed is null || parsed.Length == 0)
            return "Error: candidates is empty. Provide a JSON array of at least two {id?, file, content} edits to race.";
        if (parsed.Length < 2)
            return "Error: racing needs at least two candidates (one candidate is a single fuse_check, not a race).";

        var bound = Math.Max(2, maxCandidates);
        if (parsed.Length > bound)
            return $"Error: {parsed.Length} candidates exceed the bound k={bound} (racing is bounded to protect memory under k forks; raise maxCandidates deliberately).";

        // Build the candidate list, filling a blank id with the 1-based position so every verdict is attributable.
        var candidates = new List<Fuse.Workspace.RaceCandidate>(parsed.Length);
        for (var i = 0; i < parsed.Length; i++)
        {
            var input = parsed[i];
            if (string.IsNullOrWhiteSpace(input.File) || input.Content is null)
                return $"Error: candidate {i + 1} needs a file and content.";
            var (candidateResolved, _, candidateError) = WorkspacePathResolver.ResolveWorkspacePath(root, input.File, "race");
            if (!candidateResolved)
                return candidateError!;
            var id = string.IsNullOrWhiteSpace(input.Id) ? $"#{i + 1}" : input.Id!;
            candidates.Add(new Fuse.Workspace.RaceCandidate(id, input.File, input.Content));
        }

        // The per-candidate check is the resident overlay typecheck: the fork-cheap primitive racing depends on. A
        // null result means no held compilation covers the file, surfaced as not-applicable. When no resident
        // workspace serves the root every candidate is not-applicable, so abstain rather than pretend a race ran.
        var report = await Fuse.Workspace.CandidateRacer.RaceAsync(
            (candidate, ct) => runtime.ResidentWorkspaces.TryCheckOverlayAsync(root, candidate.File, candidate.Content, analyzers, ct),
            candidates,
            cancellationToken);

        if (report.Verdicts.All(v => !v.Applicable))
        {
            return "cannot race (abstain): no resident workspace serves this root, so the speculative overlay could not "
                + "typecheck any candidate. Start the server with FUSE_RESIDENT=1 for candidate racing (a build per "
                + "candidate would not be a race).";
        }

        var builder = new StringBuilder();
        builder.AppendLine(
            $"verification grade: oracle (speculative typecheck over the resident compilation; k={candidates.Count} candidates verified over the shared held compilation, no build, no disk write)");
        builder.AppendLine($"race of {candidates.Count} candidate(s):");
        foreach (var verdict in report.Verdicts)
        {
            if (!verdict.Applicable)
            {
                builder.AppendLine($"  [{verdict.Id}] not-applicable (no held compilation covers {verdict.File})");
                continue;
            }

            var status = verdict.IsClean
                ? $"clean (0 errors{(verdict.WarningCount > 0 ? $", {verdict.WarningCount} warning(s)" : string.Empty)})"
                : $"{verdict.ErrorCount} error(s){(verdict.WarningCount > 0 ? $", {verdict.WarningCount} warning(s)" : string.Empty)}";
            builder.AppendLine($"  [{verdict.Id}] {status}  {verdict.File}");
            foreach (var d in verdict.Diagnostics.Where(d => d.Severity == "Error"))
                builder.AppendLine($"      {d.Severity} {d.Id} at line {d.Line}: {d.Message}");
        }

        builder.AppendLine();
        if (report.WinnerId is not null)
            builder.AppendLine($"winner: {report.WinnerId} (the only clean candidate; it strictly dominates the {report.Verdicts.Count(v => !v.IsClean)} with errors or not applicable).");
        else if (report.Tie)
            builder.AppendLine($"winner: none - {report.Clean.Count} candidates are equally clean (a tie; strict dominance cannot choose a green over a green). Pick by another axis (tests, diff size, style).");
        else
            builder.AppendLine("winner: none - no candidate is clean (every candidate introduced an error). Fix the errors above; the repair packets from fuse_check name the specific fixes.");

        // The graded claims block (U2): each candidate verdict rests on the resident compiler overlay, so the race
        // outcome is compiler-grade truth (verified), the strongest grade - the same overlay fuse_check uses.
        builder.AppendLine();
        builder.AppendLine(ClaimLedger.Render(
        [
            Claim.FromCompiler(
                report.WinnerId is not null
                    ? $"candidate {report.WinnerId} typechecks clean and the other {report.Verdicts.Count(v => !v.IsClean)} do not"
                    : $"{report.Clean.Count} of {report.Verdicts.Count} raced candidates typecheck clean",
                "race: speculative overlay typecheck per candidate over the resident compilation"),
        ]));

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Compiler-executed solution-wide rename (R7): renames a symbol and every reference through Roslyn and
    ///     returns the change as a staged diff, never touching the working tree. Answers only when the whole
    ///     solution loads (a partial rename is worse than none) and abstains otherwise.
    /// </summary>
    /// <param name="path">The workspace directory.</param>
    /// <param name="symbol">The simple name of the symbol to rename.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="operation">The requested compiler refactor operation.</param>
    /// <param name="containingType">The declaring type used to disambiguate a signature operation.</param>
    /// <param name="parameterType">The new parameter type for an add-parameter operation.</param>
    /// <param name="parameterName">The parameter name for a signature operation.</param>
    /// <param name="argument">The call-site argument added by an add-parameter operation.</param>
    /// <param name="newOrder">The comma-separated parameter order for a reorder operation.</param>
    /// <param name="diagnosticId">The diagnostic identifier for an apply-codefix operation.</param>
    /// <param name="file">The repository-relative file for an apply-codefix operation.</param>
    /// <param name="cancellationToken">A token to cancel the rename.</param>
    /// <param name="runtime">The host-owned compiler cache used for this refactor.</param>
    /// <returns>The staged per-file diffs, or an explicit abstention.</returns>
    [McpServerTool(Name = "fuse_refactor", ReadOnly = true)]
    [Description("Compiler-executed, verify-gated refactors returned as a staged diff (nothing is written to disk). operation=rename (default): rename a symbol and all its references through Roslyn (a same-named unrelated symbol is not touched). operation=add-parameter: add a trailing parameter to a method and its override/interface family, threading an explicit argument (the `argument` value) into every call site. operation=add-cancellation-token: add a CancellationToken parameter and thread an in-scope token into every call site that has one, listing token-less sites as manual follow-ups. operation=remove-parameter: remove a parameter (named by parameterName) and drop its argument at every call site, abstaining when the parameter is used in a body or a call site passes a non-trivial (possibly side-effecting) argument. operation=reorder-parameters: reorder parameters into `newOrder` (comma-separated names), abstaining if any call site uses positional arguments (only named-argument call sites are safe to reorder). operation=extract-interface: generate an interface from a class's public instance methods and properties (name it with newName, else I<Class>) and make the class implement it. operation=move-type: move a top-level type (symbol) to its own new file named after it, removing it from its current file. operation=apply-codefix: apply the repo's own analyzer code fix for `diagnosticId` in `file`, driving that diagnostic to zero (discovers the analyzers and [ExportCodeFixProvider] fixes from the project's analyzer references). The signature and type operations recompile the solution and return the diff ONLY when no new diagnostic is introduced; otherwise they abstain naming the offending sites (never a mostly-right diff). Rename and the signature ops answer only when the whole solution loads cleanly; abstain otherwise. Review and apply the staged diff with normal editing tools, then run the repository's required gates.")]
    public static Task<string> FuseRefactorAsync(
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The simple name of the symbol to rename, or the method name for a signature operation.")] string symbol = "",
        [Description("The new name (rename only).")] string newName = "",
        [Description("The operation: rename (default), add-parameter, add-cancellation-token, remove-parameter, reorder-parameters, extract-interface, move-type, or apply-codefix.")] string operation = "rename",
        [Description("The declaring type's simple name, to disambiguate a method shared across types (signature operations).")] string containingType = "",
        [Description("The new parameter's type, as written in source (add-parameter).")] string parameterType = "",
        [Description("The new parameter's name (add-parameter; defaults to cancellationToken for add-cancellation-token).")] string parameterName = "",
        [Description("The argument expression added at every call site (add-parameter; defaults to 'default').")] string argument = "default",
        [Description("The parameter names in the desired order, comma-separated (reorder-parameters).")] string newOrder = "",
        [Description("The diagnostic id to fix (apply-codefix), for example IDE0090 or a repo analyzer id.")] string diagnosticId = "",
        [Description("The repo-relative file to apply the code fix in (apply-codefix).")] string file = "",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        FuseOperationalErrors.ExecuteMcpAsync(() => FuseRefactorCoreAsync(
            path, symbol, newName, operation, containingType, parameterType, parameterName, argument, newOrder, diagnosticId, file, cancellationToken,
            runtime: runtime));

    internal static async Task<string> FuseRefactorCoreAsync(
        string path,
        string symbol,
        string newName,
        string operation,
        string containingType,
        string parameterType,
        string parameterName,
        string argument,
        string newOrder,
        string diagnosticId,
        string file,
        CancellationToken cancellationToken,
        bool routeToHost = true,
        FuseMcpRuntime? runtime = null)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        if (routeToHost && Commands.McpServeCommand.IsDaemonEnabled())
        {
            var request = new RefactorRequestDto(symbol, newName, operation, containingType, parameterType, parameterName,
                argument, newOrder, diagnosticId, file);
            var remote = await FuseHostClient.TryRefactorAsync(root, request, TimeSpan.FromSeconds(5), cancellationToken);
            if (remote is not null)
                return remote.Output;
        }
        var discovery = await new Fuse.Semantics.DotNetWorkspaceDiscoverer().DiscoverAsync(root, cancellationToken);
        var target = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (target is null)
            return "cannot refactor: no solution or project found. fuse_refactor abstains.";

        var containing = string.IsNullOrWhiteSpace(containingType) ? null : containingType;
        var warmSolutions = runtime?.WarmSolutions;
        switch (operation.Trim().ToLowerInvariant())
        {
            case "add-parameter":
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(parameterType) || string.IsNullOrWhiteSpace(parameterName))
                    return "Error: add-parameter needs the method (symbol), parameterType, and parameterName.";
                return RenderChangeSignature(
                    await new Fuse.Semantics.ChangeSignatureRefactorer(warmSolutions).AddParameterAsync(
                        target, symbol, containing, parameterType, parameterName, argument, cancellationToken),
                    $"add parameter '{parameterType} {parameterName}' to {symbol}");

            case "add-cancellation-token":
                if (string.IsNullOrWhiteSpace(symbol))
                    return "Error: add-cancellation-token needs the method (symbol).";
                var tokenName = string.IsNullOrWhiteSpace(parameterName) ? "cancellationToken" : parameterName;
                return RenderChangeSignature(
                    await new Fuse.Semantics.ChangeSignatureRefactorer(warmSolutions).ThreadCancellationTokenAsync(
                        target, symbol, containing, tokenName, cancellationToken),
                    $"thread a CancellationToken '{tokenName}' through {symbol}");

            case "remove-parameter":
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(parameterName))
                    return "Error: remove-parameter needs the method (symbol) and parameterName.";
                return RenderChangeSignature(
                    await new Fuse.Semantics.ChangeSignatureRefactorer(warmSolutions).RemoveParameterAsync(
                        target, symbol, containing, parameterName, cancellationToken),
                    $"remove parameter '{parameterName}' from {symbol}");

            case "reorder-parameters":
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(newOrder))
                    return "Error: reorder-parameters needs the method (symbol) and newOrder (comma-separated parameter names).";
                var order = newOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return RenderChangeSignature(
                    await new Fuse.Semantics.ChangeSignatureRefactorer(warmSolutions).ReorderParametersAsync(
                        target, symbol, containing, order, cancellationToken),
                    $"reorder parameters of {symbol}");

            case "extract-interface":
                if (string.IsNullOrWhiteSpace(symbol))
                    return "Error: extract-interface needs the class (symbol).";
                return RenderTypeRefactor(
                    await new Fuse.Semantics.TypeRefactorer(warmSolutions).ExtractInterfaceAsync(
                        target, symbol, string.IsNullOrWhiteSpace(newName) ? null : newName, cancellationToken),
                    $"extract interface from {symbol}");

            case "move-type":
                if (string.IsNullOrWhiteSpace(symbol))
                    return "Error: move-type needs the type (symbol).";
                return RenderTypeRefactor(
                    await new Fuse.Semantics.TypeRefactorer(warmSolutions).MoveTypeToOwnFileAsync(target, symbol, cancellationToken),
                    $"move {symbol} to its own file");

            case "apply-codefix":
                if (string.IsNullOrWhiteSpace(diagnosticId) || string.IsNullOrWhiteSpace(file))
                    return "Error: apply-codefix needs a diagnosticId and a file.";
                var (fixResolved, _, fixError) = WorkspacePathResolver.ResolveWorkspacePath(root, file, "refactor");
                if (!fixResolved)
                    return fixError!;
                var fixResult = await new Fuse.Semantics.CodeFixApplier(warmSolutions).ApplyCodeFixAsync(target, diagnosticId, file, cancellationToken);
                if (!fixResult.Changed)
                    return $"cannot apply the fix for {diagnosticId} in {file}: {fixResult.Reason}";
                var fixBuilder = new StringBuilder();
                fixBuilder.AppendLine($"staged apply-codefix {fixResult.DiagnosticId} in {fixResult.FilePath} ({fixResult.Applied} fix(es) applied, verified clean, not written to disk)");
                fixBuilder.AppendLine($"--- {fixResult.FilePath} (full new content)");
                fixBuilder.AppendLine(fixResult.NewText);
                fixBuilder.AppendLine();
                fixBuilder.AppendLine("This diff verified clean (the target diagnostic reached zero with no new compile error). Review it and re-check with fuse_check before applying.");
                return fixBuilder.ToString().TrimEnd();

            case "rename":
            case "":
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(newName))
                    return "Error: provide the symbol to rename and the new name.";
                var result = await new Fuse.Semantics.RenameRefactorer(warmSolutions).RenameAsync(target, symbol, newName, cancellationToken);
                if (!result.Renamed)
                    return $"cannot rename: {result.Reason}";

                var builder = new StringBuilder();
                builder.AppendLine($"staged rename: {result.OldName} -> {result.NewName} ({result.Diffs.Count} file(s) changed, not written to disk)");
                foreach (var d in result.Diffs)
                {
                    builder.AppendLine($"--- {d.FilePath}");
                    builder.AppendLine(d.UnifiedDiff);
                }

                builder.AppendLine();
                builder.AppendLine("Review this diff and re-check with fuse_check before applying; a rename crossing a boundary Roslyn does not see (a string, reflection) would surface as a diagnostic there.");
                return builder.ToString().TrimEnd();

            default:
                return $"Error: unknown operation '{operation}'. Use rename, add-parameter, or add-cancellation-token.";
        }
    }

    // Renders a package-upgrade analysis (F3): the breaking public-API changes between two versions, or an abstention.
    private static string RenderPackageUpgrade(Fuse.Retrieval.PackageUpgradeReport report)
    {
        if (!report.Available)
            return $"package upgrade {report.PackageId}: cannot analyze - {report.Reason}";

        var builder = new StringBuilder();
        var verdict = report.HasBreaking ? $"{report.BreakingChanges.Count} BREAKING public-API change(s)" : "no breaking public-API changes";
        builder.AppendLine($"package upgrade {report.PackageId}: {verdict}, {report.AdditiveChanges.Count} additive");
        foreach (var c in report.BreakingChanges)
            builder.AppendLine($"  BREAK [{c.Kind}] {c.Symbol}{(c.Before is null ? string.Empty : $"  (was: {c.Before})")}");
        if (report.BreakingChanges.Count == 0)
            builder.AppendLine("  (the target version keeps every public/protected member of the referenced version)");

        builder.AppendLine();
        builder.AppendLine(report.BlindSpots);
        return builder.ToString().TrimEnd();
    }

    // Renders a type-refactor outcome (extract-interface, move-type): the staged full-file content, or the abstention.
    private static string RenderTypeRefactor(Fuse.Semantics.TypeRefactorResult result, string what)
    {
        if (!result.Changed)
            return $"cannot {what}: {result.Reason}";

        var builder = new StringBuilder();
        builder.AppendLine($"staged {result.Summary} (verified clean; {result.Diffs.Count} file(s), not written to disk)");
        foreach (var d in result.Diffs)
        {
            builder.AppendLine($"--- {d.FilePath} (full new content)");
            builder.AppendLine(d.NewText);
        }

        builder.AppendLine();
        builder.AppendLine("This diff verified clean (no new compile diagnostic). Review it and re-check with fuse_check before applying.");
        return builder.ToString().TrimEnd();
    }

    // Renders a change-signature outcome: the staged diff plus any manual follow-up sites, or the abstention.
    private static string RenderChangeSignature(Fuse.Semantics.ChangeSignatureResult result, string what)
    {
        if (!result.Changed)
            return $"cannot {what}: {result.Reason}";

        var builder = new StringBuilder();
        builder.AppendLine($"staged {what}: {result.OldSignature} (added {result.Added}; {result.Diffs.Count} file(s) changed, not written to disk)");
        foreach (var d in result.Diffs)
        {
            builder.AppendLine($"--- {d.FilePath}");
            builder.AppendLine(d.UnifiedDiff);
        }

        if (result.ManualFollowUps.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"Manual follow-ups ({result.ManualFollowUps.Count} call site(s) had no in-scope value, so 'default' was passed; thread a real value):");
            foreach (var site in result.ManualFollowUps)
                builder.AppendLine($"  {site}");
        }

        builder.AppendLine();
        builder.AppendLine("This diff verified clean (no new compile diagnostic). Review it and re-check with fuse_check before applying.");
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Typechecks a proposed single-file edit and returns the compiler diagnostics the change would produce,
    ///     without writing the file (T0, Decision D11). Verification never shrugs: it answers at oracle-grade (a
    ///     speculative typecheck against the build-captured compilation, no build) when tier-1 is available, falls
    ///     back to build-grade (running <c>dotnet build</c> scoped to the owning project and parsing the same
    ///     diagnostic shape) otherwise, and abstains only when even the toolchain cannot run. Every answer is
    ///     stamped with its <see cref="Fuse.Indexing.CheckResult.Grade" />.
    /// </summary>
    /// <param name="indexer">The semantic indexer (opens the store for repair-packet context).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="file">The repo-relative path of the file being changed.</param>
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
    public static Task<string> FuseCheckAsync(
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
        FuseOperationalErrors.ExecuteMcpAsync(() => FuseCheckCoreAsync(
            ResolveRuntime(runtime, indexer), indexer, path, file, content, session, full, markGreen, analyzers, cancellationToken));

    private static async Task<string> FuseCheckCoreAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
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
            return await FuseCheckDeltaAsync(runtime, path, root, session, full, markGreen, cancellationToken);

        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrEmpty(content))
            return "Error: provide the changed file path and its proposed new content (or a session id for delta mode).";

        var (fileResolved, absoluteFile, fileError) = WorkspacePathResolver.ResolveWorkspacePath(root, file, "check");
        if (!fileResolved)
            return fileError!;
        file = WorkspacePathResolver.ToRepoRelative(root, absoluteFile!);

        var ownership = await new Fuse.Semantics.ProjectOwnershipResolver().ResolveAsync(
            root, file, cancellationToken);

        // R18: verification is compiler-tier and runs before any mandatory index open, so index contention cannot
        // block a build-grade answer when dotnet build could verify. Repair-packet enrichment is indexed-tier and
        // best-effort afterward.
        var (result, buildElapsedMs) = await RunFuseCheckVerificationAsync(
            runtime, root, ownership, file, content, analyzers, cancellationToken);

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
        foreach (var d in result.Diagnostics)
            builder.AppendLine($"  {d.Severity} {d.Id} at line {d.Line}: {d.Message}");

        AppendRepairPackets(builder, await BuildRepairPacketsAsync(runtime, root, result.Diagnostics, cancellationToken));

        return builder.ToString().TrimEnd();
    }

    // Delta mode (S2): compute the diagnostics introduced or resolved since a persisted session baseline. Current
    // whole-state diagnostics come from a live resident workspace (delta mode must not run a build); the baseline
    // is persisted so a restarted process resumes it. On session start (no baseline) the current set is recorded as
    // the baseline; markGreen resets it to current; full returns the whole current set.
    private static async Task<string> FuseCheckDeltaAsync(
        FuseMcpRuntime runtime,
        string path,
        string root,
        string session,
        bool full,
        bool markGreen,
        CancellationToken cancellationToken)
    {
        var current = runtime.ResidentWorkspaces.TryGetCurrentDiagnostics(root);
        if (current is null)
        {
            return "cannot compute delta (abstain): no resident workspace serves this root, and delta mode does not run a build. "
                + "Start the server with FUSE_RESIDENT=1 for delta mode, or pass file and content for a speculative single-file check.";
        }

        await using var store = await OpenStoreForSessionBaselineAsync(runtime, root, cancellationToken);
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
            foreach (var d in current)
                all.AppendLine($"  {d.Severity} {d.Id} {d.FilePath}:{d.Line}: {d.Message}");
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
            foreach (var d in delta.Introduced)
                builder.AppendLine($"  {d.Severity} {d.Id} {d.FilePath}:{d.Line}: {d.Message}");
        }

        if (delta.Resolved.Count > 0)
        {
            builder.AppendLine("resolved:");
            foreach (var d in delta.Resolved)
                builder.AppendLine($"  {d.Severity} {d.Id} {d.FilePath}:{d.Line}: {d.Message}");
        }

        AppendRepairPackets(
            builder,
            await BuildRepairPacketsAsync(runtime, root, delta.Introduced, cancellationToken, store));

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Deterministically resolves .NET wiring to its target(s): no source bodies.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="service">A service to resolve to its implementation.</param>
    /// <param name="request">A request/command to resolve to its handler.</param>
    /// <param name="route">A route to resolve to its action.</param>
    /// <param name="config">A config section to resolve to its options type.</param>
    /// <param name="symbol">A symbol to resolve to its declaration.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The resolved target(s) with paths and evidence.</returns>
    // Folded into fuse_find (kind=service/request/route/config) in U1; kept as an internal helper the find union calls.
    public static async Task<string> FuseResolveAsync(
        SemanticIndexer indexer,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("A service to resolve to its implementation.")] string? service = null,
        [Description("A request/command to resolve to its handler.")] string? request = null,
        [Description("A route to resolve, for example \"POST /api/orders/{id}\".")] string? route = null,
        [Description("A config section to resolve to its options type.")] string? config = null,
        [Description("A symbol name to resolve to its declaration.")] string? symbol = null,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        var toolRuntime = ResolveRuntime(runtime, indexer);
        await using var store = await OpenIndexedAsync(toolRuntime, indexer, path, cancellationToken);
        var resolver = new SemanticResolver(store);

        ResolveResult? result = null;
        if (!string.IsNullOrWhiteSpace(service))
            result = await resolver.ResolveServiceAsync(service, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(request))
            result = await resolver.ResolveRequestAsync(request, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(route))
            result = await resolver.ResolveRouteAsync(route, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(config))
            result = await resolver.ResolveConfigAsync(config, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(symbol))
            result = await resolver.ResolveSymbolAsync(symbol, cancellationToken);

        if (result is null)
            return "Error: specify one of service, request, route, config, or symbol.";

        var builder = new StringBuilder();
        builder.AppendLine($"resolve {result.Target.ToString().ToLowerInvariant()}: {result.Query}");
        if (result.Matches.Count == 0)
            builder.AppendLine("  no matches");
        foreach (var match in result.Matches)
        {
            var location = match.FilePath is null ? string.Empty : $"  ({match.FilePath}:{match.StartLine})";
            builder.AppendLine($"  [{match.Relation}] {match.Kind} {match.DisplayName}{location}");
            if (match.Signature is not null)
                builder.AppendLine($"      {match.Signature}");
        }

        // The graded claims block (U2): a wiring resolution rests on the persisted graph, so it caps at
        // partially_verified (real signal, not compiler-confirmed).
        builder.AppendLine();
        builder.AppendLine(ClaimLedger.Render(
        [
            Claim.FromGraph(
                $"{result.Query} resolves to {result.Matches.Count} {result.Target.ToString().ToLowerInvariant()} target(s)",
                $"graph: {result.Target.ToString().ToLowerInvariant()} wiring edges"),
        ]));

        return builder.ToString();
    }

    /// <summary>
    ///     Plans and emits context for a set of seeds, with source bodies at mixed render tiers.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render bodies.</param>
    /// <param name="sessionStore">The session store used to elide unchanged files.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="seeds">Symbol seeds.</param>
    /// <param name="files">File path seeds (for example paths from localize).</param>
    /// <param name="services">Service seeds to resolve and expand.</param>
    /// <param name="requests">Request/command seeds to resolve and expand.</param>
    /// <param name="configs">Config section seeds to resolve and expand.</param>
    /// <param name="routes">Route seeds.</param>
    /// <param name="depth">The graph expansion depth.</param>
    /// <param name="maxTokens">The token budget.</param>
    /// <param name="format">The output format: xml, markdown, or json.</param>
    /// <param name="sessionId">Session id; files already sent unchanged in the session are elided.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The emitted context payload.</returns>
    [McpServerTool(Name = "fuse_context", ReadOnly = true)]
    [Description("Plan and emit context (source bodies, mixed render tiers, manifest, provenance) for a set of seeds. Feed it the file paths from fuse_find (kind=task) or the names it resolves from wiring kinds. Pass a sessionId to elide files already sent in the session.")]
    public static Task<string> FuseContextAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        ContextSessionStore sessionStore,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Symbol seeds.")] string[]? seeds = null,
        [Description("File path seeds (for example the paths returned by fuse_find kind=task).")] string[]? files = null,
        [Description("Service seeds to resolve and expand.")] string[]? services = null,
        [Description("Request/command seeds to resolve and expand.")] string[]? requests = null,
        [Description("Config section seeds to resolve and expand.")] string[]? configs = null,
        [Description("Route seeds, for example \"POST /api/orders/{id}\".")] string[]? routes = null,
        [Description("Graph expansion depth.")] int depth = 2,
        [Description("Token budget; must-keep seeds are always included.")] int maxTokens = 0,
        [Description("Output format: xml (default), markdown, or json.")] string format = "xml",
        [Description("Session id; files already sent unchanged in this session are elided.")] string? sessionId = null,
        CancellationToken cancellationToken = default) =>
        FuseOperationalErrors.ExecuteMcpAsync(() => FuseContextCoreAsync(
            indexer, reductionPipeline, sessionStore, path, seeds, files, services, requests, configs, routes,
            depth, maxTokens, format, sessionId, cancellationToken));

    private static async Task<string> FuseContextCoreAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        ContextSessionStore sessionStore,
        string path,
        string[]? seeds,
        string[]? files,
        string[]? services,
        string[]? requests,
        string[]? configs,
        string[]? routes,
        int depth,
        int maxTokens,
        string format,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var seedList = BuildSeeds(seeds, files, services, requests, configs, routes);
        if (seedList.Count == 0)
            return "Error: provide at least one seed (symbol/file/service/request/config/route).";

        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        if (files is { Length: > 0 })
        {
            var fileError = WorkspacePathResolver.ValidateWorkspacePaths(root, files, "read");
            if (fileError is not null)
                return fileError;
        }

        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store);
        var plan = await engine.PlanContextAsync(
            new ContextRequest(root, seedList, depth, maxTokens > 0 ? maxTokens : null), cancellationToken);

        var renderer = new SemanticContextRenderer(reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, cancellationToken);
        var unchanged = string.IsNullOrWhiteSpace(sessionId) ? null : sessionStore.Reconcile(sessionId, rendered.Files);
        return SemanticContextEmitter.Emit(plan, rendered, ParseFormat(format), root, unchangedPaths: unchanged);
    }

    private static List<ContextSeed> BuildSeeds(string[]? seeds, string[]? files, string[]? services, string[]? requests, string[]? configs, string[]? routes) =>
        (seeds ?? []).Select(s => new ContextSeed(ContextSeedKind.Symbol, s))
            .Concat((files ?? []).Select(f => new ContextSeed(ContextSeedKind.File, f)))
            .Concat((services ?? []).Select(s => new ContextSeed(ContextSeedKind.Service, s)))
            .Concat((requests ?? []).Select(r => new ContextSeed(ContextSeedKind.Request, r)))
            .Concat((configs ?? []).Select(c => new ContextSeed(ContextSeedKind.Config, c)))
            .Concat((routes ?? []).Select(r => new ContextSeed(ContextSeedKind.Route, r)))
            .ToList();

    /// <summary>
    ///     Reviews the semantic impact of a change and emits the packed context.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render bodies.</param>
    /// <param name="changeSource">The change source for resolving the git base ref.</param>
    /// <param name="sessionStore">The session store used to elide unchanged files.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="changedSince">The git base ref to diff against.</param>
    /// <param name="maxTokens">The token budget.</param>
    /// <param name="includeTests">Whether to include related test files.</param>
    /// <param name="format">The output format: xml, markdown, or json.</param>
    /// <param name="sessionId">Session id; files already sent unchanged in the session are elided.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The review preamble plus the emitted context payload.</returns>
    [McpServerTool(Name = "fuse_review", ReadOnly = true)]
    [Description("Review the semantic impact of a change since a git base ref: changed files, the blast radius (callers, DI consumers, route/request handlers, options consumers, tests), and the packed context. The flagship tool for PR/change work.")]
    public static Task<string> FuseReviewAsync(
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
        FuseOperationalErrors.ExecuteMcpAsync(() => FuseReviewCoreAsync(
            indexer, reductionPipeline, changeSource, sessionStore, path, changedSince, maxTokens, includeTests,
            format, sessionId, handoff, checkSession, maxChangedFiles, cancellationToken));

    private static async Task<string> FuseReviewCoreAsync(
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

        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);

        // R26: bound a huge diff. Fetch the changed-file set first (a cheap git name-only diff); if it exceeds the
        // cap, return the changed-file list and a narrow-the-base-ref note rather than running blast-radius
        // resolution over hundreds of files unbounded. maxTokens bounds output; this bounds the graph work.
        var changedFiles = await changeSource.GetChangedFilesAsync(root, changedSince, cancellationToken);
        var cap = ReviewBounds.ResolveCap(maxChangedFiles);
        if (ReviewBounds.ShouldBound(changedFiles.Count, cap))
        {
            var header = await OracleAvailabilityHeaderAsync(store, root, cancellationToken);
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
        var claimsSection = BuildReviewClaimsSection(plan, changedSince, apiDeltaSection);
        return SemanticContextEmitter.Emit(plan, rendered, ParseFormat(format), root, changedSince, unchanged, apiDeltaSection, claimsSection);
    }

    // The U2 graded-claims block for a review: the changed-file set is git-truth (Verified), and the presence of a
    // public-API surface delta is a graph-grade inference (PartiallyVerified, the grade cap). Returns null when
    // there is nothing to claim, so the emitter omits the block.
    private static string? BuildReviewClaimsSection(ContextPlan plan, string changedSince, string? apiDeltaSection)
    {
        var changedCount = plan.Items.Count(i => i.Role == "changed");
        var claims = new List<Claim>();
        if (changedCount > 0)
            claims.Add(Claim.FromCompiler(
                $"{changedCount} changed file(s) are seeded as must-keep",
                $"git diff {changedSince}"));
        if (!string.IsNullOrWhiteSpace(apiDeltaSection))
            claims.Add(Claim.FromGraph(
                "the change alters the public API surface (see the api-delta section)",
                "graph: public-API delta (T2)"));
        if (claims.Count == 0)
            return null;
        return ClaimLedger.Render(claims);
    }

    // The T2 public-API delta section for a review: added, removed, and changed public/protected members between
    // the base ref and the working tree. Best-effort - a git or read failure returns null so the review payload is
    // unaffected (the delta is an added section, not the review itself). The base side is read from the git base
    // ref; the current side from the working tree.
    private static async Task<string?> BuildApiDeltaSectionAsync(
        IChangeSource changeSource, string root, string changedSince, CancellationToken cancellationToken)
    {
        try
        {
            var changed = await changeSource.GetChangedFilesAsync(root, changedSince, cancellationToken);
            var delta = await ChangedApiSurfaceGatherer.GatherAsync(
                changeSource, root, changedSince, changed,
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

    // The U2 handoff packet: paste-ready PR body for a change, gated by the check session's red state. It refuses
    // (with the red summary) while the resident session has unresolved introduced errors - the gate-not-controller
    // stance in one behavior - and otherwise emits the changed files, the public API delta, the compiler-gate
    // status, and the named residual risk. The compiler gate is resident-only (like delta mode); with no resident
    // check session it is reported not-gated rather than assumed green.
    // Internal (not private): the CLI review command (U3 parity) calls this directly for `fuse review --handoff`.
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
                ResolveRuntime(runtime, indexer), indexer, changeSource, root, changedSince, checkSession, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"cannot build a handoff: {e.Message}. A handoff needs a readable git base ref (changedSince) and, for the red-gate, a resident check session.";
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
        var gateLine = "compiler status: not gated (no resident check session; run fuse_check --delta or re-check before merge).";
        var current = runtime.ResidentWorkspaces.TryGetCurrentDiagnostics(root);
        if (!string.IsNullOrWhiteSpace(checkSession) && current is not null)
        {
            await using var gateStore = await OpenIndexedAsync(runtime, indexer, root, cancellationToken);
            var baseline = await gateStore.GetCheckSessionBaselineAsync(checkSession, cancellationToken);
            if (baseline is not null)
            {
                var delta = DiagnosticDelta.Compute(baseline.Diagnostics, current);
                var introducedErrors = delta.Introduced.Where(d => d.Severity == "Error").ToList();
                if (introducedErrors.Count > 0)
                {
                    var red = new StringBuilder();
                    red.AppendLine($"handoff refused: the session has {introducedErrors.Count} unresolved introduced error(s). Resolve them before handoff (Fuse gates, it does not commit for you).");
                    foreach (var d in introducedErrors)
                        red.AppendLine($"  {d.Id} {d.FilePath}:{d.Line}: {d.Message}");
                    return red.ToString().TrimEnd();
                }

                gateLine = $"compiler status: green (no unresolved introduced errors in session '{checkSession}', resident-verified since {baseline.UpdatedUtc}).";
            }
            else
            {
                gateLine = $"compiler status: session '{checkSession}' has no baseline yet; establish one with fuse_check --delta, then re-check before merge.";
            }
        }

        IReadOnlyList<string> changed;
        try
        {
            changed = await changeSource.GetChangedFilesAsync(root, changedSince, cancellationToken);
        }
        catch (ChangeSourceException e)
        {
            return $"cannot build a handoff: {e.Message}. A handoff needs a git base ref (changedSince).";
        }

        var apiDelta = await BuildApiDeltaSectionAsync(changeSource, root, changedSince, cancellationToken);
        var packet = new StringBuilder();
        packet.AppendLine($"# Handoff: changes since {changedSince}");
        packet.AppendLine();
        packet.AppendLine(gateLine);
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

    // The T2 public-surface line for impact: whether the target symbol is on the public/protected API surface, so
    // an agent knows before editing whether a change is contract-relevant. Conservative (the T2 kill-risk): a
    // positive "public" is asserted only from IsPublicApi, which is reliable for types in any mode and for members
    // at the semantic tier; a member in syntax mode, where accessibility is unresolved, is reported undetermined
    // rather than guessed either way.
    private static async Task<string> BuildImpactApiSurfaceLineAsync(
        WorkspaceIndexStore store, string symbol, string mode, CancellationToken cancellationToken)
    {
        var matches = await store.FindSymbolsByNameAsync(symbol, 50, cancellationToken);
        var exact = matches
            .Where(m => string.Equals(m.Name, symbol, StringComparison.Ordinal)
                || string.Equals(m.FullyQualifiedName, symbol, StringComparison.Ordinal)
                || m.FullyQualifiedName.EndsWith("." + symbol, StringComparison.Ordinal))
            .ToList();

        if (exact.Count == 0)
            return $"public API surface: no indexed symbol named {symbol} to classify.";

        if (exact.Any(m => m.IsPublicApi))
        {
            return $"public API surface: {symbol} is on the public/protected surface (T2); removing it, reducing its "
                + "accessibility, or changing its signature is a breaking change, so the blast radius above is external-facing.";
        }

        // Not flagged public. For a member in syntax mode the flag is not reliable, so do not assert "internal".
        var allMembers = exact.All(m => !IsTypeKind(m.Kind));
        if (mode == "syntax" && allMembers)
        {
            return $"public API surface: undetermined for {symbol} in syntax mode (member accessibility is not "
                + "resolved); run fuse_workspace action=index on a semantically loadable checkout to classify.";
        }

        return $"public API surface: {symbol} is not on the public/protected surface; a change is internal to the assembly.";
    }

    private static bool IsTypeKind(string kind) =>
        kind is "class" or "interface" or "struct" or "record" or "enum" or "delegate" or "type";

    private static ContextOutputFormat ParseFormat(string format) => format.Trim().ToLowerInvariant() switch
    {
        "markdown" or "md" => ContextOutputFormat.Markdown,
        "json" => ContextOutputFormat.Json,
        _ => ContextOutputFormat.Xml,
    };

    private const string RepairPacketsOmittedNote =
        "repair packets: omitted (index unavailable for symbol enrichment; the verification verdict is unchanged).";

    // R18: warm read-only store open for optional enrichment or covering selection. Returns null when the index is
    // missing or contended; never triggers a syntax-first build or reconcile.
    private static async Task<WorkspaceIndexStore?> TryOpenStoreForEnrichmentAsync(
        FuseMcpRuntime runtime,
        string root,
        CancellationToken cancellationToken)
    {
        try
        {
            var databasePath = FuseStorePaths.ResolveDatabasePath(root);
            if (!File.Exists(databasePath))
                return null;

            return await runtime.IndexCoordinator.OpenForReadOnlyAsync(root, cancellationToken);
        }
        catch (Exception ex) when (IsStoreContention(ex))
        {
            return null;
        }
    }

    private static async Task<WorkspaceIndexStore?> OpenStoreForSessionBaselineAsync(
        FuseMcpRuntime runtime,
        string root,
        CancellationToken cancellationToken)
    {
        try
        {
            // The session baseline is a small daemon-owned write, so initialize the store when it does not exist
            // yet: delta mode with a resident workspace must not require a pre-built persistent index (this mirrors
            // the host RPC baseline path, FuseHostService.OpenStoreAsync). Genuine contention still abstains.
            var databasePath = FuseStorePaths.ResolveDatabasePath(root);
            if (!File.Exists(databasePath))
            {
                await runtime.IndexCoordinator.OpenForWriteAsync(
                    root, static (_, _) => Task.FromResult(0), cancellationToken);
            }

            return await runtime.IndexCoordinator.OpenForReadOnlyAsync(root, cancellationToken);
        }
        catch (Exception ex) when (IsStoreContention(ex))
        {
            return null;
        }
    }

    private static bool IsStoreContention(Exception exception) =>
        exception is IndexBusyException
            or SqliteException { SqliteErrorCode: 5 or 6 }
            or IOException { HResult: unchecked((int)0x80070020) };

    // The verification-grade ladder (T0, D11): oracle first, build-grade fallback, abstain only when neither runs.
    // Compiler-tier: no mandatory index open (R18).
    private static async Task<(Fuse.Indexing.CheckResult Result, long BuildElapsedMs)> RunFuseCheckVerificationAsync(
        FuseMcpRuntime runtime,
        string root,
        ProjectOwnership ownership,
        string file,
        string content,
        bool analyzers,
        CancellationToken cancellationToken)
    {
        Fuse.Indexing.CheckResult? oracle = null;
        var residentDiagnostics = await runtime.ResidentWorkspaces.TryCheckOverlayAsync(root, file, content, analyzers, cancellationToken);
        if (residentDiagnostics is not null)
            oracle = Fuse.Indexing.CheckResult.Ok(residentDiagnostics);

        var client = new Fuse.Semantics.BuildCaptureClient(processRunner: runtime.ProcessRunner);

        if (oracle is null && Commands.McpServeCommand.IsDaemonEnabled())
        {
            var remote = await FuseHostClient.TryCaptureCheckAsync(
                root, file, content, TimeSpan.FromSeconds(5), cancellationToken);
            if (remote is { Available: true })
            {
                oracle = CheckResult.Ok(remote.Diagnostics.Select(d => new CheckDiagnostic(
                    d.Id, d.Severity, d.Message, d.Path, d.Line)).ToList());
            }
        }

        if (oracle is null)
            oracle = await TryOracleFromCaptureBundleAsync(runtime, root, file, content, client, cancellationToken);

        if (oracle is null && client.IsAvailable && ownership.IsResolved)
            oracle = await TryOracleFromOwningProjectsAsync(client, ownership, file, content, cancellationToken);

        if (oracle is not null)
            return (oracle, 0);

        if (!ownership.IsResolved)
            return (CheckResult.Abstain("no project found to build (no oracle-grade capture and no buildable project)."), 0);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var buildResult = await new Fuse.Semantics.BuildGradeChecker(processRunner: runtime.ProcessRunner).CheckAsync(
            root, ownership, file, content, cancellationToken);
        return (buildResult, stopwatch.ElapsedMilliseconds);
    }

    private static async Task<CheckResult?> TryOracleFromOwningProjectsAsync(
        Fuse.Semantics.BuildCaptureClient client,
        ProjectOwnership ownership,
        string file,
        string content,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<CheckDiagnostic>();
        foreach (var project in ownership.ProjectPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = await client.CheckAsync(project, file, content, TimeSpan.FromMinutes(10), cancellationToken);
            if (!candidate.Verified)
                return null;
            diagnostics.AddRange(candidate.Diagnostics);
        }

        return CheckResult.Ok(diagnostics.Distinct().ToList());
    }

    internal static async Task<Fuse.Indexing.CheckResult?> TryOracleFromCaptureBundleAsync(
        FuseMcpRuntime runtime,
        string root,
        string file,
        string content,
        Fuse.Semantics.BuildCaptureClient client,
        CancellationToken cancellationToken)
    {
        if (!client.IsAvailable)
            return null;

        await using var store = await TryOpenStoreForEnrichmentAsync(runtime, root, cancellationToken);
        if (store is null)
            return null;

        var bundleDir = await store.GetMetaAsync(WorkspaceIndexStore.CaptureComplogPathMetaKey, cancellationToken);
        if (string.IsNullOrEmpty(bundleDir))
            return null;

        var logs = Directory.Exists(bundleDir)
            ? Fuse.Indexing.CaptureBundleIo.CompilerLogPaths(bundleDir)
            : File.Exists(bundleDir) ? [bundleDir] : [];
        foreach (var log in logs)
        {
            // R48: try the pooled, kept-alive worker first (rehydrate once, reuse across checks in a session); on a
            // cold/absent/failed pooled worker it returns null and we fall back to the spawn-per-call path, so the
            // verdict and honesty are unchanged and it is never worse than today.
            var pooled = await runtime.PooledCheckWorkers.TryCheckAsync(
                log, file, content, cancellationToken, ownerRoot: root);
            var candidate = pooled ?? await client.CheckFromComplogAsync(log, file, content, TimeSpan.FromMinutes(2), cancellationToken);
            if (candidate.Verified)
                return candidate;
        }

        return null;
    }

    private static async Task<(IReadOnlyList<RepairPacket> Packets, string? OmittedNote)> BuildRepairPacketsAsync(
        FuseMcpRuntime runtime,
        string root,
        IReadOnlyList<CheckDiagnostic> diagnostics,
        CancellationToken cancellationToken,
        WorkspaceIndexStore? store = null)
    {
        var ownedStore = store is null;
        store ??= await TryOpenStoreForEnrichmentAsync(runtime, root, cancellationToken);
        if (store is null)
            return ([], RepairPacketsOmittedNote);

        try
        {
            var packetBuilder = new RepairPacketBuilder(store);
            var packets = new List<RepairPacket>();
            foreach (var d in diagnostics.Where(d => d.Severity == "Error"))
            {
                var packet = await packetBuilder.BuildAsync(d, cancellationToken);
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
            foreach (var p in repair.Packets)
            {
                builder.AppendLine($"  [{p.DiagnosticId}] {p.Explanation}");
                if (p.TopRepair is { } repairAction)
                    builder.AppendLine($"    apply: replace '{repairAction.OldToken}' with '{repairAction.NewToken}'");
                foreach (var m in p.Members.Take(12))
                    builder.AppendLine($"    {(string.IsNullOrEmpty(m.Signature) ? m.Name : m.Signature)}");
            }
        }
        else if (repair.OmittedNote is not null)
        {
            builder.AppendLine();
            builder.AppendLine(repair.OmittedNote);
        }
    }
}
