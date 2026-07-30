using Fuse.Indexing;
using Fuse.Semantics.Analyzers;

namespace Fuse.Semantics;

/// <summary>
///     Extracts compiler-backed symbols, graph facts, and project ownership records for semantic indexing.
/// </summary>
internal sealed class SemanticGraphExtractor
{
    private readonly SemanticSymbolExtractor _symbols;
    private readonly SemanticAnalysisRunner _analysisRunner;
    private readonly FileHashService _hashService;

    internal SemanticGraphExtractor(
        SemanticSymbolExtractor symbols,
        SemanticAnalysisRunner analysisRunner,
        FileHashService hashService)
    {
        _symbols = symbols;
        _analysisRunner = analysisRunner;
        _hashService = hashService;
    }

    internal IReadOnlyList<SymbolRecord> ExtractSymbols(
        LoadedProject project,
        string root,
        CancellationToken cancellationToken)
        => _symbols.Extract(project, root, cancellationToken);

    internal SemanticAnalyzerResult AnalyzeProject(
        LoadedProject project,
        string root,
        CancellationToken cancellationToken)
        => _analysisRunner.Run(new SemanticAnalysisContext(project, root), cancellationToken);

    internal SemanticAnalyzerResult AnalyzeWorkspace(
        string root,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var edges = new List<SemanticEdgeRecord>();
        var routes = new List<RouteRecord>();
        var registrations = new List<DiRegistrationRecord>();
        var bindings = new List<OptionsBindingRecord>();
        var diagnostics = new List<DiagnosticRecord>();

        var projects = snapshot.Projects;
        var perProject = new SemanticAnalyzerResult[projects.Count];
        Parallel.For(
            0,
            projects.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            index => perProject[index] = AnalyzeProject(projects[index], root, cancellationToken));

        foreach (var result in perProject)
        {
            foreach (var node in result.Nodes)
                nodes[node.NodeId] = node;
            edges.AddRange(result.Edges);
            routes.AddRange(result.Routes);
            registrations.AddRange(result.DiRegistrations);
            bindings.AddRange(result.OptionsBindings);
            diagnostics.AddRange(result.Diagnostics);
        }

        var existingNodeIds = new HashSet<string>(nodes.Keys, StringComparer.Ordinal);
        var diResolvesTo = edges
            .Where(edge => edge.EdgeType == "di_resolves_to")
            .GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(edge => edge.ToNodeId).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        var (testNodes, testEdges) = new TestEdgeExtractor()
            .Extract(snapshot.Projects, existingNodeIds, diResolvesTo, root, cancellationToken);
        foreach (var node in testNodes)
            nodes[node.NodeId] = node;
        edges.AddRange(testEdges);

        return new SemanticAnalyzerResult(nodes.Values.ToList(), edges, routes, registrations, bindings, diagnostics);
    }

    internal List<ProjectRecord> BuildProjectRecords(
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var records = new List<ProjectRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(project.FilePath))
                continue;

            records.Add(new ProjectRecord(
                Path: project.FilePath,
                Name: project.Name,
                ProjectHash: ComputeProjectHash(project.FilePath),
                AssemblyName: project.AssemblyName));
        }

        return records;
    }

    internal List<ProjectRecord> BuildCaptureProjectRecords(
        IReadOnlyList<CapturedProject> capturedProjects,
        CancellationToken cancellationToken)
    {
        var records = new List<ProjectRecord>(capturedProjects.Count);
        foreach (var project in capturedProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.Add(new ProjectRecord(
                Path: project.FilePath,
                Name: project.Name,
                ProjectHash: ComputeProjectHash(project.FilePath),
                AssemblyName: project.AssemblyName,
                TargetFramework: project.TargetFramework));
        }

        return records;
    }

    internal static Dictionary<string, string> BuildFileProjectMap(
        string root,
        RoslynWorkspaceSnapshot snapshot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in snapshot.Projects)
        {
            foreach (var tree in project.Compilation.SyntaxTrees)
            {
                if (string.IsNullOrEmpty(tree.FilePath))
                    continue;

                var normalized = Path.GetRelativePath(root, tree.FilePath).Replace(Path.DirectorySeparatorChar, '/');
                map.TryAdd(normalized, project.FilePath);
            }
        }

        return map;
    }

    private string ComputeProjectHash(string projectFilePath) =>
        File.Exists(projectFilePath)
            ? _hashService.ComputeHash(File.ReadAllBytes(projectFilePath))
            : "0";
}
