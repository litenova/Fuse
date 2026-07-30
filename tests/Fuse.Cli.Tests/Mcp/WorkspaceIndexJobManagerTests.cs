using Fuse.Cli.Mcp;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

public sealed class WorkspaceIndexJobManagerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-index-jobs", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Matching_requests_share_one_running_job()
    {
        var executor = new BlockingExecutor();
        await using var manager = new WorkspaceIndexJobManager(executor);

        var first = await manager.StartOrJoinAsync(Request(IndexDepth.Syntax), CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await manager.StartOrJoinAsync(Request(IndexDepth.Syntax), CancellationToken.None);

        Assert.False(first.Joined);
        Assert.True(second.Joined);
        Assert.False(second.Conflict);
        Assert.Equal(first.Snapshot.JobId, second.Snapshot.JobId);
        Assert.Single(executor.Requests);

        executor.Release();
        var completed = await manager.WaitForCompletionAsync(_root, CancellationToken.None);
        Assert.Equal(IndexJobState.Completed, completed!.State);
    }

    [Fact]
    public async Task Semantic_request_upgrades_active_syntax_job()
    {
        var executor = new BlockingExecutor();
        await using var manager = new WorkspaceIndexJobManager(executor);

        await manager.StartOrJoinAsync(Request(IndexDepth.Syntax), CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var joined = await manager.StartOrJoinAsync(Request(IndexDepth.Semantic), CancellationToken.None);

        Assert.True(joined.Joined);
        Assert.False(joined.Conflict);

        executor.Release();
        var completed = await manager.WaitForCompletionAsync(_root, CancellationToken.None);

        Assert.Equal(IndexJobState.Completed, completed!.State);
        Assert.Equal([IndexDepth.Syntax, IndexDepth.Semantic], executor.Requests.Select(request => request.Depth));
    }

    [Fact]
    public async Task Semantic_request_starts_with_syntax_before_compiler_analysis()
    {
        var executor = new BlockingExecutor();
        await using var manager = new WorkspaceIndexJobManager(executor);

        await manager.StartOrJoinAsync(Request(IndexDepth.Semantic), CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        executor.Release();

        var completed = await manager.WaitForCompletionAsync(_root, CancellationToken.None);

        Assert.Equal(IndexJobState.Completed, completed!.State);
        Assert.Equal([IndexDepth.Syntax, IndexDepth.Semantic], executor.Requests.Select(request => request.Depth));
    }

    [Fact]
    public async Task Force_request_conflicts_with_active_job()
    {
        var executor = new BlockingExecutor();
        await using var manager = new WorkspaceIndexJobManager(executor);

        await manager.StartOrJoinAsync(Request(IndexDepth.Syntax), CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var force = await manager.StartOrJoinAsync(Request(IndexDepth.Syntax) with { Force = true }, CancellationToken.None);

        Assert.True(force.Conflict);
        Assert.False(force.Joined);
        Assert.Equal("index_job_conflict", force.Snapshot.ErrorCode);
        Assert.Contains(force.Snapshot.JobId, force.Snapshot.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("fuse index cancel", force.Snapshot.ErrorMessage, StringComparison.Ordinal);

        executor.Release();
    }

    [Fact]
    public async Task Different_capture_bundle_conflicts_with_active_job()
    {
        var executor = new BlockingExecutor();
        await using var manager = new WorkspaceIndexJobManager(executor);

        await manager.StartOrJoinAsync(Request(IndexDepth.Semantic) with { CaptureBundlePath = Path.Combine(_root, "capture-a") }, CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var differentCapture = await manager.StartOrJoinAsync(
            Request(IndexDepth.Semantic) with { CaptureBundlePath = Path.Combine(_root, "capture-b") },
            CancellationToken.None);

        Assert.True(differentCapture.Conflict);
        Assert.Equal("index_job_conflict", differentCapture.Snapshot.ErrorCode);

        executor.Release();
    }

    [Fact]
    public async Task Cancel_reaches_executor_and_sets_cancelled_terminal_state()
    {
        var executor = new BlockingExecutor();
        await using var manager = new WorkspaceIndexJobManager(executor);

        await manager.StartOrJoinAsync(Request(IndexDepth.Syntax), CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelling = await manager.CancelAsync(_root, CancellationToken.None);
        var terminal = await manager.WaitForCompletionAsync(_root, CancellationToken.None);

        Assert.True(cancelling!.State is IndexJobState.Cancelling or IndexJobState.Cancelled);
        Assert.Equal(IndexJobState.Cancelled, terminal!.State);
        Assert.True(executor.CancellationObserved.Task.IsCompleted);
    }

    private IndexJobRequest Request(IndexDepth depth) => new(_root, depth, Force: false, CaptureBundlePath: null);

    private sealed class BlockingExecutor : IWorkspaceIndexJobExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<IndexJobRequest> Requests { get; } = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SemanticIndexResult> ExecuteAsync(
            string jobId,
            IndexJobRequest request,
            IProgress<IndexJobProgress> progress,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            progress.Report(new IndexJobProgress(IndexPhase.SyntaxExtraction, 1, 2, "A.cs"));
            Started.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            progress.Report(new IndexJobProgress(IndexPhase.SyntaxPersistence, 2, 2, "B.cs"));
            return new SemanticIndexResult(
                request.Depth == IndexDepth.Semantic ? "semantic" : "syntax",
                2,
                request.Depth == IndexDepth.Semantic ? 1 : 0,
                3,
                2,
                0,
                []);
        }

        public void Release() => _release.TrySetResult();
    }
}
