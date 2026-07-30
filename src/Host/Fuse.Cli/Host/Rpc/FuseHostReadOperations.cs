using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Scoping;
using Microsoft.Extensions.Logging;

namespace Fuse.Cli.Rpc;

// Focused read operations behind the thin JSON-RPC adapter. The host owns lifecycle validation and payload cleanup.
internal sealed class FuseHostReadOperations
{
    private readonly FuseHostService _host;

    internal FuseHostReadOperations(FuseHostService host)
    {
        _host = host;
    }

    internal async Task<GraphDto> GraphAsync(
        string root,
        string detail,
        string? scopeMode,
        string? seed,
        string? query,
        string? since,
        string? directory)
    {
        var expandDirectory = !string.IsNullOrWhiteSpace(directory);
        var directories = !expandDirectory && string.Equals(detail, "Directories", StringComparison.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
            return new GraphDto([], [], directories ? "Directories" : "Files");

        await using var store = await _host.OpenIndexedForHostAsync(root, _host.LifetimeToken);
        var files = await store.FindFilesByPathAsync(string.Empty, FuseHostService.ListLimit, _host.LifetimeToken);
        var tokenByPath = await store.GetFileTokenEstimatesAsync(_host.LifetimeToken);
        var edges = await store.GetFileDependencyEdgesAsync(_host.LifetimeToken);

        var typesByPath = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var symbol in await store.ListSymbolsAsync(FuseHostService.ListLimit, _host.LifetimeToken))
        {
            if (!typesByPath.TryGetValue(symbol.FilePath, out var names))
                typesByPath[symbol.FilePath] = names = [];
            if (names.Count < 25 && !names.Contains(symbol.Name))
                names.Add(symbol.Name);
        }

        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            degree[edge.FromPath] = degree.GetValueOrDefault(edge.FromPath) + 1;
            degree[edge.ToPath] = degree.GetValueOrDefault(edge.ToPath) + 1;
        }

        var maxDegree = degree.Count == 0 ? 1 : degree.Values.Max();
        var roleByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(scopeMode))
        {
            var (_, plan) = await PlanScopeAsync(store, root, scopeMode!, seed, query, since, 0);
            foreach (var item in plan.Items)
                roleByPath[item.Path] = item.Role;
        }

        var fileNodes = files.Select(file => new GraphNodeDto(
            file.NormalizedPath,
            typesByPath.GetValueOrDefault(file.NormalizedPath, []),
            Math.Round(degree.GetValueOrDefault(file.NormalizedPath) / (double)maxDegree, 4),
            tokenByPath.GetValueOrDefault(file.NormalizedPath),
            roleByPath.GetValueOrDefault(file.NormalizedPath))).ToList();
        var fileEdges = edges
            .GroupBy(edge => (edge.FromPath, edge.ToPath))
            .Select(group => new GraphEdgeDto(group.Key.FromPath, group.Key.ToPath, group.Count(), group.First().Kind))
            .ToList();

        if (expandDirectory)
        {
            var prefix = directory!.Replace('\\', '/').TrimEnd('/') + "/";
            var subNodes = fileNodes.Where(node => node.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            var subPaths = new HashSet<string>(subNodes.Select(node => node.Path), StringComparer.OrdinalIgnoreCase);
            var subEdges = fileEdges.Where(edge => subPaths.Contains(edge.From) && subPaths.Contains(edge.To)).ToList();
            return new GraphDto(subNodes, subEdges, "Files");
        }

        return directories ? AggregateToDirectories(fileNodes, fileEdges) : new GraphDto(fileNodes, fileEdges, "Files");
    }

    internal async Task<ScopeResultDto> ScopeAsync(
        string root,
        string mode,
        string? seed,
        string? query,
        string? since,
        int maxTokens)
    {
        if (!Directory.Exists(root))
            return new ScopeResultDto((mode ?? "search").Trim().ToLowerInvariant(), [], 0, null);

        await using var store = await _host.OpenIndexedForHostAsync(root, _host.LifetimeToken);
        var (normalizedMode, plan) = await PlanScopeAsync(store, root, mode, seed, query, since, maxTokens);
        string? payloadPath = null;
        if (plan.Items.Count > 0)
        {
            var renderer = new SemanticContextRenderer(
                _host.ReductionPipeline,
                new SourceContentProvider(new PhysicalFileSystem()));
            var rendered = await renderer.RenderAsync(plan, root, _host.LifetimeToken);
            var content = SemanticContextEmitter.Emit(plan, rendered, ContextOutputFormat.Xml, root);
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Fuse",
                "host-payloads");
            Directory.CreateDirectory(directory);
            payloadPath = Path.Combine(
                directory,
                $"{HostEndpoint.PipeName(root)}-{normalizedMode}-{Guid.NewGuid():N}.fuse.xml");
            await File.WriteAllTextAsync(payloadPath, content, _host.LifetimeToken);
            RestrictPayloadPermissions(payloadPath);
            _host.TrackPayload(payloadPath);
        }

        var files = plan.Items
            .Select(item => new ScopeFileDto(item.Path, item.EstimatedTokens))
            .OrderByDescending(file => file.TokenCost)
            .ToList();
        _host.Logger.LogInformation(
            "Scope {Mode} on {Root}: {Files} files, {Tokens} tokens.",
            normalizedMode,
            root,
            files.Count,
            plan.EstimatedTokens);
        return new ScopeResultDto(normalizedMode, files, plan.EstimatedTokens, payloadPath);
    }

    internal async Task<ExplainResultDto> ExplainAsync(
        string root,
        string mode,
        string? seed,
        string? query,
        string? since)
    {
        if (!Directory.Exists(root))
            return new ExplainResultDto((mode ?? "search").Trim().ToLowerInvariant(), []);

        await using var store = await _host.OpenIndexedForHostAsync(root, _host.LifetimeToken);
        var (normalizedMode, plan) = await PlanScopeAsync(store, root, mode, seed, query, since, 0);
        var files = plan.Items.Select(item => new ExplainFileDto(item.Path, item.Role, item.Tier.ToString(), item.Score)).ToList();
        _host.Logger.LogInformation("Explain {Mode} on {Root}: {Files} planned files.", normalizedMode, root, files.Count);
        return new ExplainResultDto(normalizedMode, files);
    }

    internal async Task<DiagnosticsDto> DiagnosticsAsync(string root)
    {
        if (!Directory.Exists(root))
            return new DiagnosticsDto([], [], [], []);

        await using var store = await _host.OpenIndexedForHostAsync(root, _host.LifetimeToken);
        var files = await store.FindFilesByPathAsync(string.Empty, FuseHostService.ListLimit, _host.LifetimeToken);
        var secrets = new List<SecretDiagnosticDto>();
        var generated = new List<string>();
        foreach (var file in files)
        {
            string content;
            try
            {
                content = await File.ReadAllTextAsync(
                    Path.Combine(root, file.NormalizedPath.Replace('/', Path.DirectorySeparatorChar)),
                    _host.LifetimeToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (string.Equals(file.Extension, ".cs", StringComparison.OrdinalIgnoreCase)
                && _host.GeneratedCodeDetector.IsGenerated(content))
            {
                generated.Add(file.NormalizedPath);
            }

            var spans = _host.Redactor.FindSecretSpans(content);
            if (spans.Count == 0)
                continue;

            var lineStarts = ComputeLineStarts(content);
            foreach (var span in spans)
            {
                var (startLine, startColumn) = OffsetToLineColumn(lineStarts, span.Start);
                var (endLine, endColumn) = OffsetToLineColumn(lineStarts, span.Start + span.Length);
                secrets.Add(new SecretDiagnosticDto(
                    file.NormalizedPath,
                    span.Kind,
                    startLine,
                    startColumn,
                    endLine,
                    endColumn));
            }
        }

        var tokenByPath = await store.GetFileTokenEstimatesAsync(_host.LifetimeToken);
        var hotspots = tokenByPath
            .Select(pair => new HotspotDiagnosticDto(pair.Key, pair.Value))
            .OrderByDescending(hotspot => hotspot.TokenCost)
            .Take(20)
            .ToList();
        var connected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in await store.GetFileDependencyEdgesAsync(_host.LifetimeToken))
        {
            connected.Add(edge.FromPath);
            connected.Add(edge.ToPath);
        }

        var graphGaps = files
            .Select(file => file.NormalizedPath)
            .Where(path => !connected.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        _host.Logger.LogInformation(
            "Diagnostics on {Root}: {Secrets} secrets, {Hotspots} hotspots, {Gaps} gaps, {Generated} generated.",
            root,
            secrets.Count,
            hotspots.Count,
            graphGaps.Count,
            generated.Count);
        return new DiagnosticsDto(secrets, hotspots, graphGaps, generated);
    }

    private async Task<(string Mode, ContextPlan Plan)> PlanScopeAsync(
        WorkspaceIndexStore store,
        string root,
        string mode,
        string? seed,
        string? query,
        string? since,
        int maxTokens)
    {
        var engine = new SemanticRetrievalEngine(store, _host.ChangeSource);
        var normalized = (mode ?? "search").Trim().ToLowerInvariant();
        int? budget = maxTokens > 0 ? maxTokens : null;
        return normalized switch
        {
            "changes" => ("changes", await engine.ReviewAsync(
                new ReviewRequest(root, string.IsNullOrWhiteSpace(since) ? "HEAD" : since!, MaxTokens: budget),
                _host.LifetimeToken)),
            "focus" => ("focus", await engine.PlanContextAsync(
                new ContextRequest(
                    root,
                    string.IsNullOrWhiteSpace(seed)
                        ? []
                        : [new ContextSeed(LooksLikePath(seed) ? ContextSeedKind.File : ContextSeedKind.Symbol, seed)],
                    MaxTokens: budget),
                _host.LifetimeToken)),
            _ => await PlanSearchAsync(engine, root, query, budget),
        };
    }

    private async Task<(string Mode, ContextPlan Plan)> PlanSearchAsync(
        SemanticRetrievalEngine engine,
        string root,
        string? query,
        int? budget)
    {
        var located = await engine.LocalizeAsync(new LocalizationRequest(root, Query: query), _host.LifetimeToken);
        var seeds = located.Candidates
            .Where(candidate => !string.IsNullOrEmpty(candidate.Path))
            .Select(candidate => new ContextSeed(ContextSeedKind.File, candidate.Path))
            .ToList();
        return ("search", await engine.PlanContextAsync(
            new ContextRequest(root, seeds, MaxTokens: budget),
            _host.LifetimeToken));
    }

    private static GraphDto AggregateToDirectories(
        IReadOnlyList<GraphNodeDto> fileNodes,
        IReadOnlyList<GraphEdgeDto> fileEdges)
    {
        static string DirectoryOf(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash <= 0 ? "." : path[..slash];
        }

        var byDirectory = new Dictionary<string, (double Centrality, int Tokens, int Files)>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in fileNodes)
        {
            var directory = DirectoryOf(node.Path);
            var aggregate = byDirectory.GetValueOrDefault(directory);
            byDirectory[directory] = (
                aggregate.Centrality + node.Centrality,
                aggregate.Tokens + node.TokenCost,
                aggregate.Files + 1);
        }

        var nodes = byDirectory
            .Select(pair => new GraphNodeDto(
                pair.Key,
                [$"{pair.Value.Files} files"],
                Math.Round(pair.Value.Centrality, 4),
                pair.Value.Tokens,
                null))
            .ToList();
        var edges = fileEdges
            .Select(edge => (From: DirectoryOf(edge.From), To: DirectoryOf(edge.To)))
            .Where(edge => !string.Equals(edge.From, edge.To, StringComparison.OrdinalIgnoreCase))
            .GroupBy(edge => (edge.From, edge.To))
            .Select(group => new GraphEdgeDto(group.Key.From, group.Key.To, group.Count(), "reference"))
            .ToList();
        return new GraphDto(nodes, edges, "Directories");
    }

    private static bool LooksLikePath(string value) =>
        value.Contains('/', StringComparison.Ordinal)
        || value.Contains('\\', StringComparison.Ordinal)
        || Path.HasExtension(value);

    private static int[] ComputeLineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\n')
                starts.Add(index + 1);
        }

        return [.. starts];
    }

    private static (int Line, int Column) OffsetToLineColumn(int[] lineStarts, int offset)
    {
        var line = Array.BinarySearch(lineStarts, offset);
        if (line < 0)
            line = ~line - 1;
        line = Math.Clamp(line, 0, lineStarts.Length - 1);
        return (line, offset - lineStarts[line]);
    }

    private static void RestrictPayloadPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
