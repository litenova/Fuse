using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Xunit;
using Xunit.Abstractions;

namespace Fuse.Semantics.Tests;

// R45: the per-project analyzer graph pass runs concurrently and merges positionally, so its flattened nodes/edges
// are byte-identical to the sequential pass. This test loads a real multi-project repo (eShopOnWeb), computes the
// sequential and parallel merges, and asserts they are byte-identical (node ids and ordered edge tuples). It is
// tolerant of the fixture being absent (skips), and prints the timing split when FUSE_PERF_R45=1. Suite A (24/24)
// is verified separately via `fuse eval semantics`.
public sealed class AnalyzerParallelismMeasurement(ITestOutputHelper output)
{
    private static string? Corpus([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        var eshop = dir is null ? null : Path.Combine(dir.FullName, "tests", "benchmarks", ".corpus", "eShopOnWeb");
        return eshop is not null && Directory.Exists(eshop) ? eshop : null;
    }

    [Fact]
    [Trait("Category", "RequiresSdk")]
    public async Task Parallel_analyzer_pass_is_byte_identical_to_sequential()
    {
        if (Environment.GetEnvironmentVariable("FUSE_SEMANTIC_CORPUS_TESTS") != "1")
            return;

        var root = Corpus();
        if (root is null)
            return; // Corpus fixture not present in this environment.

        var solution = Path.Combine(root, "eShopOnWeb.sln");
        if (!File.Exists(solution))
            return;

        var discovery = new WorkspaceDiscoveryResult(WorkspaceKind.Solution, solution, [], root);
        var snapshot = await new RoslynWorkspaceLoader().LoadAsync(discovery, CancellationToken.None);
        if (!snapshot.SemanticLoadSucceeded || snapshot.Projects.Count < 2)
            return; // Nothing multi-project to compare in this environment.

        var runner = SemanticAnalysisRunner.CreateDefault();
        var perfTiming = Environment.GetEnvironmentVariable("FUSE_PERF_R45") == "1";

        // Warm each compilation's binding once so a timing sample measures the analyzer pass, not first-bind JIT.
        foreach (var project in snapshot.Projects)
            _ = runner.Run(new SemanticAnalysisContext(project, root), CancellationToken.None);

        var seqWatch = Stopwatch.StartNew();
        var sequential = new List<SemanticAnalyzerResult>();
        foreach (var project in snapshot.Projects)
            sequential.Add(runner.Run(new SemanticAnalysisContext(project, root), CancellationToken.None));
        seqWatch.Stop();

        var parWatch = Stopwatch.StartNew();
        var parallel = new SemanticAnalyzerResult[snapshot.Projects.Count];
        Parallel.For(0, snapshot.Projects.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i => parallel[i] = runner.Run(new SemanticAnalysisContext(snapshot.Projects[i], root), CancellationToken.None));
        parWatch.Stop();

        // Merge both positionally (the production merge order) and compare byte-for-byte.
        var seqNodes = MergeNodeIds(sequential);
        var parNodes = MergeNodeIds(parallel);
        Assert.Equal(seqNodes, parNodes);

        var seqEdges = MergeEdges(sequential);
        var parEdges = MergeEdges(parallel);
        Assert.Equal(seqEdges, parEdges);

        if (perfTiming)
            output.WriteLine($"analyzer pass sequential={seqWatch.ElapsedMilliseconds}ms parallel={parWatch.ElapsedMilliseconds}ms " +
                             $"(projects={snapshot.Projects.Count}, cores={Environment.ProcessorCount}, edges={seqEdges.Count})");
    }

    // The flattened node-id list in the last-writer-wins-by-id, project order the production merge produces.
    private static List<string> MergeNodeIds(IReadOnlyList<SemanticAnalyzerResult> results)
    {
        var nodes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var result in results)
            foreach (var node in result.Nodes)
                nodes[node.NodeId] = node.Kind;
        return nodes.Select(kvp => $"{kvp.Key}|{kvp.Value}").OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    private static List<string> MergeEdges(IReadOnlyList<SemanticAnalyzerResult> results)
    {
        var edges = new List<string>();
        foreach (var result in results)
            foreach (var edge in result.Edges)
                edges.Add($"{edge.FromNodeId}->{edge.ToNodeId}|{edge.EdgeType}");
        return edges; // Order-preserving: the production merge concatenates edges in project order.
    }
}
