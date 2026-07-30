using Fuse.Cli.Extensions;
using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Tests.Mcp;

/// <summary>
///     Regression coverage for persistent job lifecycle markers written around syntax and semantic stages.
/// </summary>
public sealed class SemanticIndexJobExecutorTests : IAsyncLifetime
{
    private readonly ServiceProvider _services = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-index-job-executor", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Widget.cs"),
            "namespace Sample; public sealed class Widget { public int Id => 1; }");
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ExecuteAsync_marks_an_unfinished_previous_job_and_keeps_one_start_time()
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var seed = new WorkspaceIndexStore(databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.SetMetaAsync("index_job_id", "previous-job", CancellationToken.None);
            await seed.SetMetaAsync("index_job_phase", "SyntaxPersistence", CancellationToken.None);
            await seed.SetMetaAsync("index_job_depth", "Syntax", CancellationToken.None);
            await seed.SetMetaAsync("index_job_started_at", "2026-01-02T03:04:05.0000000+00:00", CancellationToken.None);
            await seed.SetMetaAsync("index_job_state", "running", CancellationToken.None);
        }

        var progress = new RecordingProgress();
        var startedAt = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var executor = new SemanticIndexJobExecutor(
            new IndexCoordinator(),
            _services.GetRequiredService<SemanticIndexer>(),
            new AdvancingTimeProvider(startedAt));

        await executor.ExecuteAsync(
            "new-job",
            new IndexJobRequest(_root, IndexDepth.Syntax, Force: false, CaptureBundlePath: null),
            progress,
            CancellationToken.None);

        await using var store = new WorkspaceIndexStore(databasePath);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await store.OpenForReadAsync(CancellationToken.None));
        Assert.Equal("previous-job", await store.GetMetaAsync("index_interrupted_job_id", CancellationToken.None));
        Assert.Equal("SyntaxPersistence", await store.GetMetaAsync("index_interrupted_phase", CancellationToken.None));
        Assert.Equal("Syntax", await store.GetMetaAsync("index_interrupted_depth", CancellationToken.None));
        Assert.Equal("2026-01-02T03:04:05.0000000+00:00", await store.GetMetaAsync("index_interrupted_started_at", CancellationToken.None));
        Assert.Equal("interrupted", await store.GetMetaAsync("index_interrupted_state", CancellationToken.None));
        Assert.Equal("new-job", await store.GetMetaAsync("index_job_id", CancellationToken.None));
        Assert.Equal("completed", await store.GetMetaAsync("index_job_state", CancellationToken.None));
        Assert.Equal(startedAt.ToString("O"), await store.GetMetaAsync("index_job_started_at", CancellationToken.None));
        Assert.Contains(
            progress.Updates,
            update => update.Warning is not null && update.Warning.Contains("previous index job previous-job was interrupted", StringComparison.Ordinal));
    }

    private sealed class RecordingProgress : IProgress<IndexJobProgress>
    {
        public List<IndexJobProgress> Updates { get; } = [];

        public void Report(IndexJobProgress value) => Updates.Add(value);
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _next = initial;

        public override DateTimeOffset GetUtcNow()
        {
            var current = _next;
            _next = _next.AddMinutes(1);
            return current;
        }
    }
}
