using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Xunit;
using Xunit.Abstractions;

namespace Fuse.Semantics.Tests;

// R42 latency measurement (opt-in, FUSE_PERF_R42=1): times a cold (first) vs warm (second) fuse_refactor and
// fuse_workspace-doctor load over the Fuse repo's own Fuse.slnx, so the daemon-held warm-Solution before/after is
// recorded through the refactor/diagnose engine. Environment-dependent; not a canonical result file. Skipped in
// CI (the env flag is unset) so it never slows the gate.
public sealed class WarmSolutionLatencyMeasurement(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("FUSE_PERF_R42") == "1";

    private static string? RepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    [Fact]
    [Trait("Category", "RequiresSdk")]
    public async Task Measure_cold_vs_warm_refactor_and_doctor()
    {
        if (!Enabled)
            return;
        var root = RepoRoot();
        Assert.NotNull(root);
        var sln = Path.Combine(root!, "Fuse.slnx");

        // Refactor: cold (first open loads MSBuild) vs warm (held solution reused). A rename that abstains still
        // pays the load, which is the cost R42 attacks, so an abstention is a valid timing sample.
        using var cache = new WarmSolutionCache(cap: 3);
        var refactorer = new RenameRefactorer(cache);
        var coldRefactor = Stopwatch.StartNew();
        await refactorer.RenameAsync(sln, "WarmSolutionCache", "WarmSolutionCacheRenamed", CancellationToken.None);
        coldRefactor.Stop();
        var warmRefactor = Stopwatch.StartNew();
        await refactorer.RenameAsync(sln, "WarmSolutionCache", "WarmSolutionCacheRenamed", CancellationToken.None);
        warmRefactor.Stop();
        output.WriteLine($"refactor cold={coldRefactor.ElapsedMilliseconds}ms warm={warmRefactor.ElapsedMilliseconds}ms loads={cache.LoadCount}");

        // Doctor: cold (first DiagnoseLoad loads MSBuild) vs warm (shared cache reuse). Fresh Shared cache so the
        // first call is genuinely cold.
        WarmSolutionCache.Shared = new WarmSolutionCache(cap: 3);
        var indexer = CreateIndexer();
        var coldDoctor = Stopwatch.StartNew();
        await indexer.DiagnoseLoadAsync(root!, CancellationToken.None);
        coldDoctor.Stop();
        var warmDoctor = Stopwatch.StartNew();
        await indexer.DiagnoseLoadAsync(root!, CancellationToken.None);
        warmDoctor.Stop();
        output.WriteLine($"doctor cold={coldDoctor.ElapsedMilliseconds}ms warm={warmDoctor.ElapsedMilliseconds}ms loads={WarmSolutionCache.Shared.LoadCount}");

        // R44: the MSBuild toolchain warmup at startup does exactly this - discover the target and prime the cache
        // via OpenAsync - off the critical path. After it completes, the first refactor of the session hits the
        // warm cache instead of paying the cold load. Here the warmup elapsed is the cost moved to background start.
        var warmCache = new WarmSolutionCache(cap: 3);
        var warmup = Stopwatch.StartNew();
        await warmCache.OpenAsync(sln, CancellationToken.None);
        warmup.Stop();
        var firstAfterWarmup = Stopwatch.StartNew();
        await new RenameRefactorer(warmCache).RenameAsync(sln, "WarmSolutionCache", "WarmSolutionCacheRenamed", CancellationToken.None);
        firstAfterWarmup.Stop();
        output.WriteLine($"R44 warmup(background)={warmup.ElapsedMilliseconds}ms firstRefactorAfterWarmup={firstAfterWarmup.ElapsedMilliseconds}ms loads={warmCache.LoadCount}");

        var rss = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
        output.WriteLine($"process RSS after holding solutions: {rss} MB (cap {3})");
    }

    private static SemanticIndexer CreateIndexer()
    {
        var fileSystem = new PhysicalFileSystem();
        var pipeline = new FileCollectionPipeline(
            fileSystem,
            new GitIgnoreParser(fileSystem),
            [new GitIgnoreFilter(), new ExtensionFilter(), new ExcludedDirectoryFilter(), new EmptyFileFilter(), new BinaryFileFilter(fileSystem)]);
        return new SemanticIndexer(
            new DotNetWorkspaceDiscoverer(),
            new RoslynWorkspaceLoader(),
            new WorkspaceFileScanner(pipeline),
            new SemanticSymbolExtractor(),
            new SyntaxSymbolExtractor(),
            new SyntaxRouteExtractor(),
            new FileHashService(),
            SemanticAnalysisRunner.CreateDefault());
    }
}
