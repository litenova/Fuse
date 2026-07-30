using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// F2 candidate racing through the fuse_test MCP surface: parses the candidates JSON, races the overlay
// typechecks over the (stubbed) resident workspace, and renders per-candidate diagnostics plus a strict-
// dominance winner. Also covers the abstention when no resident workspace serves the root, and the input
// guards (bound on k, malformed JSON).
public sealed class FuseTestRaceTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();

    public void Dispose()
    {
        _provider.Dispose();
    }

    [Fact]
    public async Task Race_names_the_sole_clean_candidate_as_the_winner()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = NewWorkspace(out var root);
        try
        {
            // The stub is content-keyed: any candidate whose content contains BROKEN gets a CS error, else clean.
            var runtime = FuseMcpRuntime.CreateIsolated(indexer, new ContentKeyedProvider(root));

            const string candidates = """
                [
                  {"id":"tuple","file":"Widget.cs","content":"clean tuple approach"},
                  {"id":"linq","file":"Widget.cs","content":"BROKEN linq approach"},
                  {"id":"loop","file":"Widget.cs","content":"BROKEN loop approach"}
                ]
                """;

            var output = await TestToolOperations.ExecuteAsync(
                indexer, path: work, candidates: candidates, cancellationToken: CancellationToken.None, runtime: runtime);

            Assert.Contains("verification grade: oracle", output);
            Assert.Contains("race of 3 candidate(s):", output);
            Assert.Contains("[tuple] clean", output);
            Assert.Contains("[linq] 1 error(s)", output);
            Assert.Contains("winner: tuple", output);
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public async Task Race_reports_a_tie_when_two_candidates_are_clean()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = NewWorkspace(out var root);
        try
        {
            var runtime = FuseMcpRuntime.CreateIsolated(indexer, new ContentKeyedProvider(root));
            const string candidates = """
                [
                  {"id":"a","file":"Widget.cs","content":"clean a"},
                  {"id":"b","file":"Widget.cs","content":"clean b"}
                ]
                """;

            var output = await TestToolOperations.ExecuteAsync(
                indexer, path: work, candidates: candidates, cancellationToken: CancellationToken.None, runtime: runtime);

            Assert.Contains("winner: none", output);
            Assert.Contains("tie", output);
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public async Task Race_abstains_with_no_resident_workspace()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = NewWorkspace(out _);
        try
        {
            const string candidates = """
                [{"id":"a","file":"Widget.cs","content":"x"},{"id":"b","file":"Widget.cs","content":"y"}]
                """;

            var output = await TestToolOperations.ExecuteAsync(
                indexer, path: work, candidates: candidates, cancellationToken: CancellationToken.None);

            Assert.Contains("cannot race (abstain)", output);
            Assert.Contains("FUSE_RESIDENT=1", output);
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public async Task Race_rejects_more_candidates_than_the_bound()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = NewWorkspace(out _);
        try
        {
            var candidates = "[" + string.Join(",",
                Enumerable.Range(0, 5).Select(i => $"{{\"file\":\"W.cs\",\"content\":\"c{i}\"}}")) + "]";

            var output = await TestToolOperations.ExecuteAsync(
                indexer, path: work, candidates: candidates, maxCandidates: 4, cancellationToken: CancellationToken.None);

            Assert.Contains("exceed the bound", output);
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public async Task Race_rejects_a_single_candidate_and_malformed_json()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = NewWorkspace(out _);
        try
        {
            var one = await TestToolOperations.ExecuteAsync(
                indexer, path: work, candidates: """[{"file":"W.cs","content":"x"}]""", cancellationToken: CancellationToken.None);
            Assert.Contains("at least two candidates", one);

            var bad = await TestToolOperations.ExecuteAsync(
                indexer, path: work, candidates: "{not json", cancellationToken: CancellationToken.None);
            Assert.Contains("must be a JSON array", bad);
        }
        finally
        {
            Cleanup(work);
        }
    }

    private static string NewWorkspace(out string root)
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-race-mcp-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.Combine(work, ".git"));
        File.WriteAllText(Path.Combine(work, "Widget.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(work, "Widget.cs"),
            "namespace Sample; public sealed class Widget { public int Spin() => 42; }");
        root = Path.GetFullPath(work);
        return work;
    }

    private static void Cleanup(string work)
    {
        try { Directory.Delete(work, recursive: true); } catch (IOException) { }
    }

    // A resident-provider stub whose overlay verdict is keyed on the candidate content: content containing
    // "BROKEN" returns a compiler error, anything else returns clean. Returns null for a different root (so the
    // abstention path is exercised by swapping in the null provider, not this one).
    private sealed class ContentKeyedProvider(string root) : IResidentWorkspaceProvider
    {
        public ResidentStatus? DescribeResident(string queried) =>
            string.Equals(queried, root, StringComparison.OrdinalIgnoreCase) ? new ResidentStatus(1, "test") : null;

        public IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
            string queried, string relativeFilePath, string newContent, CancellationToken cancellationToken) => null;

        public Task<IReadOnlyList<CheckDiagnostic>?> TryCheckOverlayAsync(
            string queried, string relativeFilePath, string newContent, bool includeAnalyzers, CancellationToken cancellationToken)
        {
            if (!string.Equals(queried, root, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<IReadOnlyList<CheckDiagnostic>?>(null);

            IReadOnlyList<CheckDiagnostic> diagnostics = newContent.Contains("BROKEN", StringComparison.Ordinal)
                ? [new CheckDiagnostic("CS0103", "Error", "broken candidate", relativeFilePath, 1)]
                : [];
            return Task.FromResult<IReadOnlyList<CheckDiagnostic>?>(diagnostics);
        }
    }
}
