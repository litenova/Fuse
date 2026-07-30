using System.Text;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Fuse.Workspace;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Implements <c>fuse_find</c>, the find union: exact lookup, wiring resolution, signatures, neighbors, and
///     ranked task localization. No source bodies.
/// </summary>
internal static class FindToolOperations
{
    private const int DefaultExactMatchLimit = 50;

    /// <summary>Runs one find kind and maps a not-ready index to its availability header.</summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="changeSource">The git change source for task localization and review-aware lookup.</param>
    /// <param name="query">The name, path fragment, text, wiring identifier, or task to find.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="kind">The kind: symbol, path, text, all, service, request, route, config, signatures, neighbors, or task.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The matches for the requested kind.</returns>
    internal static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        string query,
        string path = ".",
        string kind = "all",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        IndexedStoreAccess.ExecuteReadMcpAsync(() => DispatchAsync(
            IndexedStoreAccess.ResolveRuntime(runtime, indexer), indexer, changeSource, query, path, kind, cancellationToken));

    /// <summary>
    ///     Localizes a task to ranked candidate files and symbols (no source bodies). Reached through
    ///     <c>fuse_find kind=task</c>.
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
    /// <param name="strict">When true, an insufficient request is refused and only a navigation map is returned.</param>
    /// <param name="expand">When true, candidates are enriched with their typed-graph neighbors.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>Ranked candidates with reasons and token costs, or a navigation map when not confident.</returns>
    internal static async Task<string> LocalizeAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        string path = ".",
        string? task = null,
        string? route = null,
        string? symbol = null,
        string? service = null,
        string? request = null,
        string? config = null,
        string? changedSince = null,
        int maxCandidates = 50,
        bool strict = false,
        bool expand = false,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        var toolRuntime = IndexedStoreAccess.ResolveRuntime(runtime, indexer);
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        await using var store = await IndexedStoreAccess.OpenIndexedAsync(toolRuntime, indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store, changeSource);
        var localization = new LocalizationRequest(
            root, Query: task, ChangedSince: changedSince, Route: route, Focus: symbol, Service: service,
            Request: request, ConfigSection: config, MaxCandidates: maxCandidates, Strict: strict, ExpandGraph: expand);
        return LocalizationFormatter.Format(await engine.LocalizeAsync(localization, cancellationToken));
    }

    /// <summary>
    ///     Deterministically resolves .NET wiring to its target(s). Reached through <c>fuse_find</c> with
    ///     <c>kind=service|request|route|config</c>.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="service">A service to resolve to its implementation.</param>
    /// <param name="request">A request or command to resolve to its handler.</param>
    /// <param name="route">A route to resolve to its action.</param>
    /// <param name="config">A config section to resolve to its options type.</param>
    /// <param name="symbol">A symbol to resolve to its declaration.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The resolved target(s) with paths and evidence.</returns>
    internal static async Task<string> ResolveAsync(
        SemanticIndexer indexer,
        string path = ".",
        string? service = null,
        string? request = null,
        string? route = null,
        string? config = null,
        string? symbol = null,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        var toolRuntime = IndexedStoreAccess.ResolveRuntime(runtime, indexer);
        await using var store = await IndexedStoreAccess.OpenIndexedAsync(toolRuntime, indexer, path, cancellationToken);
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

        var target = result.Target.ToString().ToLowerInvariant();
        var builder = new StringBuilder();
        builder.AppendLine($"resolve {target}: {result.Query}");
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
                $"{result.Query} resolves to {result.Matches.Count} {target} target(s)",
                $"graph: {target} wiring edges"),
        ]));

        return builder.ToString();
    }

    /// <summary>
    ///     Batch exact-signature lookup: for a set of symbol names, returns each declared signature, kind,
    ///     accessibility, and location in one call. Reached through <c>fuse_find kind=signatures</c>.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="names">The symbol names to look up (simple or fully qualified).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="limitPerName">The maximum matches to return per requested name.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The signatures grouped by requested name, with a note for any name that did not match.</returns>
    internal static async Task<string> SignaturesAsync(
        SemanticIndexer indexer,
        string[] names,
        string path = ".",
        int limitPerName = 5,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        if (names is null || names.Length == 0)
            return "Error: provide one or more symbol names in 'names'.";

        var toolRuntime = IndexedStoreAccess.ResolveRuntime(runtime, indexer);
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        var requestedNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var residentByName = requestedNames.ToDictionary(
            name => name,
            name => toolRuntime.ResidentWorkspaces.TryGetSignature(root, name, limitPerName, cancellationToken),
            StringComparer.Ordinal);

        // A resident compiler can answer metadata signatures without waiting for the syntax store. Start or join
        // the repository job through the selected access provider so a daemon-backed MCP server never starts a
        // competing local writer, then return the exact compiler answer immediately.
        if (requestedNames.Length > 0 && residentByName.Values.All(signatures => signatures is { Count: > 0 }))
            return await RenderResidentOnlyAsync(toolRuntime, indexer, root, requestedNames, residentByName, cancellationToken);

        await using var store = await IndexedStoreAccess.OpenIndexedAsync(toolRuntime, indexer, path, cancellationToken);
        var matches = await store.GetSignaturesByNamesAsync(names, limitPerName, cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine(await IndexAvailabilityReporter.OracleHeaderAsync(
            store, root, cancellationToken, residentWorkspaces: toolRuntime.ResidentWorkspaces));
        foreach (var requested in requestedNames)
        {
            builder.AppendLine($"# {requested}");

            // U1b: resident-first. When a live resident workspace serves the root it resolves a qualified name
            // (including a referenced package's API) from the compiler's real metadata, so a package signature is
            // answered from the compiler rather than the store (which never indexed the package). Fall through to
            // the store when no resident workspace serves the root, or it did not resolve this name.
            var residentSignatures = residentByName[requested];
            if (residentSignatures is { Count: > 0 })
            {
                AppendResidentSignatures(builder, residentSignatures);
                continue;
            }

            AppendIndexedSignatures(builder, matches, requested);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Iterative exploration primitives: the graph neighborhood of a file, the callers and implementers of a
    ///     symbol, or the structurally central files of an area. Reached through <c>fuse_find kind=neighbors</c>.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="file">A file whose graph neighborhood to return.</param>
    /// <param name="symbol">A symbol whose callers and implementers to return.</param>
    /// <param name="centralIn">An area whose central files to return; empty means the whole workspace.</param>
    /// <param name="limit">The maximum results to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The ranked exploration items with provenance and no bodies.</returns>
    internal static async Task<string> NeighborsAsync(
        SemanticIndexer indexer,
        string path = ".",
        string? file = null,
        string? symbol = null,
        string? centralIn = null,
        int limit = 20,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null)
    {
        var toolRuntime = IndexedStoreAccess.ResolveRuntime(runtime, indexer);
        await using var store = await IndexedStoreAccess.OpenIndexedAsync(toolRuntime, indexer, path, cancellationToken);
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
            builder.AppendLine($"  {item.Path}{FormatSymbolSuffix(item)}  [{item.Reason}]");

        return builder.ToString();
    }

    private static async Task<string> DispatchAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        IChangeSource changeSource,
        string query,
        string path,
        string kind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return FuseOperationalErrors.Format(FuseOperationalErrors.ValidationErrorPrefix, "provide a query to find.");

        // A wiring, signatures, neighbors, or task kind routes to the specialized engine logic, keyed by the query.
        switch (kind.Trim().ToLowerInvariant())
        {
            case "service":
                return await ResolveAsync(indexer, path, service: query, cancellationToken: cancellationToken, runtime: runtime);
            case "request":
                return await ResolveAsync(indexer, path, request: query, cancellationToken: cancellationToken, runtime: runtime);
            case "route":
                return await ResolveAsync(indexer, path, route: query, cancellationToken: cancellationToken, runtime: runtime);
            case "config":
                return await ResolveAsync(indexer, path, config: query, cancellationToken: cancellationToken, runtime: runtime);
            case "signatures":
                return await SignaturesAsync(indexer, [query], path, cancellationToken: cancellationToken, runtime: runtime);
            case "neighbors":
                return await NeighborsAsync(indexer, path, symbol: query, cancellationToken: cancellationToken, runtime: runtime);
            case "task":
                return await LocalizeTaskAsync(runtime, indexer, changeSource, query, path, cancellationToken);
            default:
                return await ExactLookupAsync(runtime, indexer, query, path, kind.Trim().ToLowerInvariant(), cancellationToken);
        }
    }

    private static async Task<string> LocalizeTaskAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        IChangeSource changeSource,
        string query,
        string path,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        await using (var store = await IndexedStoreAccess.OpenIndexedAsync(runtime, indexer, path, cancellationToken))
        {
            if (!store.FullTextSearchAvailable)
            {
                return AvailabilityHeaderHelpers.FormatTaskLocalizationFtsRefusal(
                    await IndexAvailabilityReporter.OracleHeaderAsync(
                        store, root, cancellationToken, residentWorkspaces: runtime.ResidentWorkspaces));
            }
        }

        return await LocalizeAsync(indexer, changeSource, path, task: query, cancellationToken: cancellationToken, runtime: runtime);
    }

    private static async Task<string> ExactLookupAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        string query,
        string path,
        string kind,
        CancellationToken cancellationToken)
    {
        // R30: the opt-in inline lexical fallback. When the index is not semantic-ready (a cold/building/rebuilding
        // store makes the open signal a deferral) and FUSE_LEXICAL_FALLBACK is on, serve a scoped, ranked raw-text
        // result graded lexical-fallback instead of the bare deferral signal - for fuse-only/CLI setups with no
        // native search to defer to. Default (flag off): the deferral signal is returned as usual.
        WorkspaceIndexStore store;
        try
        {
            store = await IndexedStoreAccess.OpenIndexedAsync(runtime, indexer, path, cancellationToken);
        }
        catch (IndexBlockedReadException) when (LexicalFallback.IsEnabled())
        {
            FuseMetrics.RecordDegraded(DegradedStateKind.LexicalFallback);
            return await LexicalFallback.SearchAsync(
                WorkspacePathResolver.ResolveRepositoryRoot(path), query, DefaultExactMatchLimit, cancellationToken);
        }

        await using (store)
        {
            var builder = new StringBuilder();

            if (kind is "all" or "symbol")
            {
                var symbols = await store.FindSymbolsByNameAsync(query, DefaultExactMatchLimit, cancellationToken);
                builder.AppendLine($"symbols ({symbols.Count}):");
                foreach (var symbol in symbols)
                    builder.AppendLine($"  {symbol.Kind} {symbol.FullyQualifiedName}  ({symbol.FilePath}:{symbol.StartLine})");
            }

            if (kind is "all" or "path")
            {
                var files = await store.FindFilesByPathAsync(query, DefaultExactMatchLimit, cancellationToken);
                builder.AppendLine($"paths ({files.Count}):");
                foreach (var file in files)
                    builder.AppendLine($"  {file.NormalizedPath}");
            }

            if (kind is "all" or "text")
            {
                var hits = await store.SearchAsync(new SearchQuery(query, DefaultExactMatchLimit), cancellationToken);
                builder.AppendLine($"text ({hits.Count}):");
                foreach (var hit in hits)
                    builder.AppendLine($"  {hit.Name ?? hit.Kind}  ({hit.FilePath}:{hit.StartLine})");
            }

            return builder.ToString();
        }
    }

    private static async Task<string> RenderResidentOnlyAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        string root,
        IReadOnlyList<string> requestedNames,
        IReadOnlyDictionary<string, IReadOnlyList<ResidentSignature>?> residentByName,
        CancellationToken cancellationToken)
    {
        var started = await runtime.IndexAccess.StartSyntaxAsync(indexer, root, cancellationToken);
        var builder = new StringBuilder();
        builder.AppendLine(await IndexAvailabilityReporter.BuildingSyntaxHeaderAsync(
            root, started.Snapshot.Counts.Files, runtime.ResidentWorkspaces, cancellationToken));
        foreach (var requested in requestedNames)
        {
            builder.AppendLine($"# {requested}");
            AppendResidentSignatures(builder, residentByName[requested]!);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendResidentSignatures(StringBuilder builder, IReadOnlyList<ResidentSignature> signatures)
    {
        foreach (var signature in signatures)
        {
            var container = string.IsNullOrEmpty(signature.Container) ? string.Empty : $" in {signature.Container}";
            builder.AppendLine($"  {signature.Signature}{container}");
            builder.AppendLine($"    [{signature.Kind}] resident (metadata: {signature.Assembly})");
        }
    }

    private static void AppendIndexedSignatures(
        StringBuilder builder,
        IReadOnlyList<SymbolSignature> matches,
        string requested)
    {
        var forName = matches
            .Where(match => string.Equals(match.Name, requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(match.FullyQualifiedName, requested, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (forName.Count == 0)
        {
            builder.AppendLine("  no match in the index (check the name, or run fuse_workspace action=index if the file is new).");
            return;
        }

        foreach (var match in forName)
        {
            var accessibility = string.IsNullOrEmpty(match.Accessibility) ? string.Empty : match.Accessibility + " ";
            var signature = string.IsNullOrEmpty(match.Signature)
                ? $"{match.Kind} {match.FullyQualifiedName} (no signature recorded; index is syntax-tier for this file)"
                : $"{accessibility}{match.Signature}";
            var container = string.IsNullOrEmpty(match.ContainingType) ? string.Empty : $" in {match.ContainingType}";
            builder.AppendLine($"  {signature}{container}");
            builder.AppendLine($"    {match.FilePath}:{match.StartLine} [{match.Kind}{(match.IsPublicApi ? ", public-api" : "")}]");
        }
    }

    private static string FormatSymbolSuffix(ExploredItem item) =>
        item.Symbol is null ? string.Empty : $"  {item.Symbol}";
}
