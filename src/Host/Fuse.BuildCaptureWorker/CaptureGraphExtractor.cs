using Basic.CompilerLog.Util;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Microsoft.CodeAnalysis;

namespace Fuse.BuildCaptureWorker;

/// <summary>
///     Rehydrates compiler calls into captured projects and extracts their symbols, wiring graph, and covering
///     test edges without opening an MSBuild workspace.
/// </summary>
internal sealed class CaptureGraphExtractor
{
    private readonly Func<Compilation, string?, Compilation> _normalizeSigning;

    internal CaptureGraphExtractor(Func<Compilation, string?, Compilation> normalizeSigning) =>
        _normalizeSigning = normalizeSigning;

    internal List<RehydratedCaptureProject> RehydrateProjects(
        string binlogPath,
        string? workspaceRoot,
        CancellationToken cancellationToken)
    {
        using var reader = CompilerCallReaderUtil.Create(binlogPath);
        var symbolExtractor = new SemanticSymbolExtractor();
        var analyzers = SemanticAnalysisRunner.CreateDefault();
        var projects = new List<RehydratedCaptureProject>();
        foreach (var data in reader.ReadAllCompilationData())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = data.CompilerCall;
            if (call.IsCSharp != true)
                continue;

            var compilation = _normalizeSigning(data.GetCompilationAfterGenerators(cancellationToken), call.ProjectFilePath);
            var errorCount = compilation.GetDiagnostics(cancellationToken)
                .Count(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            var typeCount = CountTypes(compilation.Assembly.GlobalNamespace);
            var projectDirectory = Path.GetDirectoryName(call.ProjectFilePath) ?? Directory.GetCurrentDirectory();
            var normalizeRoot = string.IsNullOrEmpty(workspaceRoot) ? projectDirectory : workspaceRoot;
            var loaded = new LoadedProject(
                Name: Path.GetFileNameWithoutExtension(call.ProjectFilePath) ?? call.ProjectFileName ?? "project",
                FilePath: call.ProjectFilePath ?? string.Empty,
                AssemblyName: compilation.AssemblyName,
                Compilation: compilation);
            var symbols = symbolExtractor.Extract(loaded, normalizeRoot, cancellationToken);
            var graph = analyzers.Run(new SemanticAnalysisContext(loaded, normalizeRoot), cancellationToken);

            projects.Add(new RehydratedCaptureProject(new CapturedProject(
                Name: loaded.Name,
                FilePath: loaded.FilePath,
                AssemblyName: compilation.AssemblyName,
                ErrorCount: errorCount,
                TypeCount: typeCount,
                SymbolCount: symbols.Count,
                NodeCount: graph.Nodes.Count,
                EdgeCount: graph.Edges.Count,
                Symbols: symbols,
                Nodes: graph.Nodes,
                Edges: graph.Edges,
                Routes: graph.Routes,
                DiRegistrations: graph.DiRegistrations,
                OptionsBindings: graph.OptionsBindings,
                TargetFramework: call.TargetFramework),
                loaded));
        }

        return projects;
    }

    // Test edges are added after every project graph exists because tests commonly reference types in another
    // project. Target frameworks stay separate so the persistent union records their availability accurately.
    internal IReadOnlyList<CapturedProject> AddCoveringTestEdges(
        IReadOnlyList<RehydratedCaptureProject> captures,
        string? workspaceRoot,
        CancellationToken cancellationToken)
    {
        var projected = captures.Select(capture => capture.Project).ToList();
        foreach (var targetGroup in captures
            .Select((capture, index) => (Capture: capture, Index: index))
            .GroupBy(item => item.Capture.Project.TargetFramework ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var members = targetGroup.ToList();
            var nodesById = new Dictionary<string, int>(StringComparer.Ordinal);
            var existingNodeIds = new HashSet<string>(StringComparer.Ordinal);
            var allEdges = new List<SemanticEdgeRecord>();
            foreach (var member in members)
            {
                foreach (var node in projected[member.Index].Nodes ?? [])
                {
                    existingNodeIds.Add(node.NodeId);
                    nodesById.TryAdd(node.NodeId, member.Index);
                }

                allEdges.AddRange(projected[member.Index].Edges ?? []);
            }

            var diResolvesTo = allEdges
                .Where(edge => edge.EdgeType == "di_resolves_to")
                .GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group.Select(edge => edge.ToNodeId).Distinct(StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal);
            var root = !string.IsNullOrEmpty(workspaceRoot)
                ? workspaceRoot
                : Path.GetDirectoryName(members[0].Capture.Project.FilePath) ?? Directory.GetCurrentDirectory();
            var (testNodes, testEdges) = new TestEdgeExtractor().Extract(
                members.Select(member => member.Capture.Loaded).ToList(),
                existingNodeIds,
                diResolvesTo,
                root,
                cancellationToken);

            var nodesToAdd = new Dictionary<int, List<NodeRecord>>();
            foreach (var node in testNodes)
            {
                if (nodesById.TryGetValue(node.NodeId, out var owner))
                    (nodesToAdd.TryGetValue(owner, out var list) ? list : nodesToAdd[owner] = []).Add(node);
            }

            var edgesToAdd = new Dictionary<int, List<SemanticEdgeRecord>>();
            foreach (var edge in testEdges)
            {
                if (nodesById.TryGetValue(edge.FromNodeId, out var owner))
                    (edgesToAdd.TryGetValue(owner, out var list) ? list : edgesToAdd[owner] = []).Add(edge);
            }

            foreach (var member in members)
            {
                var hasNodes = nodesToAdd.TryGetValue(member.Index, out var memberNodes);
                var hasEdges = edgesToAdd.TryGetValue(member.Index, out var memberEdges);
                if (!hasNodes && !hasEdges)
                    continue;

                var nodes = (projected[member.Index].Nodes ?? [])
                    .Concat(memberNodes ?? [])
                    .GroupBy(node => node.NodeId, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();
                var edges = (projected[member.Index].Edges ?? [])
                    .Concat(memberEdges ?? [])
                    .GroupBy(
                        edge => (edge.FromNodeId, edge.ToNodeId, edge.EdgeType, edge.EvidenceFilePath),
                        EqualityComparer<(string, string, string, string?)>.Default)
                    .Select(group => group.First())
                    .ToList();
                projected[member.Index] = projected[member.Index] with
                {
                    Nodes = nodes,
                    NodeCount = nodes.Count,
                    Edges = edges,
                    EdgeCount = edges.Count,
                };
            }
        }

        return projected;
    }

    private static int CountTypes(INamespaceSymbol @namespace)
    {
        var count = @namespace.GetTypeMembers().Length;
        foreach (var child in @namespace.GetNamespaceMembers())
            count += CountTypes(child);
        return count;
    }
}

internal sealed record RehydratedCaptureProject(CapturedProject Project, LoadedProject Loaded);
