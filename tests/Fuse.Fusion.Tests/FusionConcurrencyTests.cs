using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

/// <summary>
///     Verifies that the orchestrator runs fusions concurrently with no process-wide gate: the per-run content
///     cache and BM25 index hold no cross-run state, so simultaneous runs stay isolated and correct.
/// </summary>
public sealed class FusionConcurrencyTests : IDisposable
{
    private readonly List<string> _dirs = [];
    private readonly ServiceProvider _serviceProvider;

    public FusionConcurrencyTests()
    {
        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_ConcurrentRunsAgainstDifferentDirectories_AreIsolated()
    {
        const int runs = 8;

        // Each directory carries a unique marker token so cross-run leakage would be detectable.
        var requests = new List<(FusionRequest Request, string Marker)>();
        for (var i = 0; i < runs; i++)
        {
            var marker = $"UniqueMarker{i}xyz";
            var dir = NewDirectory();
            WriteFile(dir, "Service.cs", $$"""
                public class Service{{i}}
                {
                    public string Token() => "{{marker}}";
                }
                """);
            requests.Add((BuildQueryRequest(dir, $"Service{i}"), marker));
        }

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        var results = await Task.WhenAll(requests.Select(r => orchestrator.FuseAsync(r.Request)));

        for (var i = 0; i < runs; i++)
        {
            var content = results[i].InMemoryContent;
            Assert.NotNull(content);
            // Each run must contain its own marker and none of the others'.
            Assert.Contains(requests[i].Marker, content);
            for (var j = 0; j < runs; j++)
            {
                if (j != i)
                    Assert.DoesNotContain(requests[j].Marker, content);
            }
        }
    }

    [Fact]
    public async Task FuseAsync_ConcurrentRunsAgainstSameDirectory_ProduceConsistentResults()
    {
        var dir = NewDirectory();
        WriteFile(dir, "Alpha.cs", """
            public class Alpha
            {
                public string Name => "alpha-token";
            }
            """);
        WriteFile(dir, "Beta.cs", """
            public class Beta
            {
                public int Value => 42;
            }
            """);

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        var tasks = Enumerable.Range(0, 12)
            .Select(_ => orchestrator.FuseAsync(BuildQueryRequest(dir, "Alpha")))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // The per-run index must not collide: every concurrent run yields the same isolated, correct output.
        var first = results[0].InMemoryContent;
        Assert.NotNull(first);
        Assert.Contains("Alpha.cs", first);
        foreach (var result in results)
            Assert.Equal(first, result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_ConcurrentAnalysisCachedRunsAgainstSameDirectory_ProduceConsistentResults()
    {
        var dir = NewDirectory();
        WriteFile(dir, "Alpha.cs", """
            public class Alpha
            {
                public string Name => "alpha-token";
            }
            """);
        WriteFile(dir, "Beta.cs", """
            public class Beta
            {
                public int Value => 42;
            }
            """);

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        var tasks = Enumerable.Range(0, 12)
            .Select(_ => orchestrator.FuseAsync(BuildQueryRequest(dir, "Alpha", useAnalysisCache: true)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var first = results[0].InMemoryContent;
        Assert.NotNull(first);
        Assert.Contains("Alpha.cs", first);
        foreach (var result in results)
            Assert.Equal(first, result.InMemoryContent);

        Assert.False(File.Exists(Path.Combine(dir, ".fuse", "fuse-cache.db")));
        Assert.False(File.Exists(Path.Combine(dir, ".fuse", "fuse.db")));
    }

    [Fact]
    public async Task FuseAsync_ConcurrentRunsWithDifferentGitIgnore_DoNotCrossContaminate()
    {
        // C3: GitIgnoreFilter patterns must be per-run. One directory gitignores Secret.cs; the other does not.
        // Interleaving many concurrent runs would, with a shared mutable pattern set, leak one repo's ignore
        // rules into the other. Each run must honor only its own .gitignore.
        var ignoreDir = NewDirectory();
        WriteFile(ignoreDir, ".gitignore", "Secret.cs\n");
        WriteFile(ignoreDir, "Secret.cs", "public class Secret { public string Widget() => \"s\"; }");
        WriteFile(ignoreDir, "Public.cs", "public class Public { public string Widget() => \"p\"; }");

        var keepDir = NewDirectory();
        WriteFile(keepDir, "Secret.cs", "public class Secret { public string Widget() => \"s\"; }");
        WriteFile(keepDir, "Public.cs", "public class Public { public string Widget() => \"p\"; }");

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        var tasks = new List<Task<(bool Ignored, FusionResult Result)>>();
        for (var i = 0; i < 24; i++)
        {
            var useIgnore = i % 2 == 0;
            var dir = useIgnore ? ignoreDir : keepDir;
            tasks.Add(Run(dir, useIgnore));
        }

        var results = await Task.WhenAll(tasks);

        foreach (var (ignored, result) in results)
        {
            Assert.NotNull(result.InMemoryContent);
            Assert.Contains("Public.cs", result.InMemoryContent);
            if (ignored)
                Assert.DoesNotContain("Secret.cs", result.InMemoryContent);
            else
                Assert.Contains("Secret.cs", result.InMemoryContent);
        }

        async Task<(bool, FusionResult)> Run(string dir, bool ignored)
        {
            var result = await orchestrator.FuseAsync(BuildQueryRequest(dir, "Widget"));
            return (ignored, result);
        }
    }

    [Fact]
    public async Task FuseAsync_ConcurrentRunsWithPatternSummary_ProduceConsistentResults()
    {
        // C3: pattern detectors accumulate mutable state, so a shared instance would corrupt the summary under
        // concurrency. A fresh detector batch per run must yield identical output across simultaneous runs.
        var dir = NewDirectory();
        WriteFile(dir, "UserController.cs", """
            public class UserController
            {
                private readonly ILogger _logger;
                public async Task Handle()
                {
                    _logger.LogInformation("handling");
                    await Task.CompletedTask;
                }
            }
            """);

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        var request = new FusionRequest(
            new CollectionOptions(dir, extensions: [".cs"]),
            new ReductionOptions(includePatternSummary: true),
            new EmissionOptions(),
            inMemory: true);

        var tasks = Enumerable.Range(0, 12).Select(_ => orchestrator.FuseAsync(request)).ToArray();
        var results = await Task.WhenAll(tasks);

        var first = results[0].InMemoryContent;
        Assert.NotNull(first);
        foreach (var result in results)
            Assert.Equal(first, result.InMemoryContent);
    }

    // Builds a no-scope in-memory fusion request over the whole repo. The classic query scoping mode was
    // removed (K2), so these concurrency tests exercise the orchestrator, host-memory analysis cache, and
    // reduction cache over an unscoped run rather than a query-scoped one; the label argument is retained only to keep
    // each concurrent request's marker file distinct.
    private static FusionRequest BuildQueryRequest(string dir, string label, bool useAnalysisCache = false)
    {
        _ = label;
        return new FusionRequest(
            new CollectionOptions(dir, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true,
            useReductionCache: useAnalysisCache ? false : true,
            useAnalysisCache: useAnalysisCache);
    }

    private string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-concurrency-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        _dirs.Add(dir);
        return dir;
    }

    private static void WriteFile(string dir, string name, string content) =>
        File.WriteAllText(Path.Combine(dir, name), content);

    public void Dispose()
    {
        _serviceProvider.Dispose();
        foreach (var dir in _dirs)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
