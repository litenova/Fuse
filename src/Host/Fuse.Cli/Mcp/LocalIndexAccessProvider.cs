using Fuse.Indexing;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The in-process index access path. Cold reads, refreshes, and explicit index requests all enter the
///     repository-owned job manager before opening the resulting store. Used by daemon-less CLI,
///     <c>FUSE_DAEMON=0</c> serve, and as the fallback when no daemon answers.
/// </summary>
public sealed class LocalIndexAccessProvider : IIndexAccessProvider
{
    private readonly IndexCoordinator _coordinator;
    private readonly IWorkspaceIndexJobManager _jobs;
    private readonly TimeSpan? _coldReadDeadline;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LocalIndexAccessProvider" /> class.
    /// </summary>
    /// <param name="coordinator">The process-owned store coordinator used only to open the committed store.</param>
    /// <param name="jobs">The process-owned repository job manager.</param>
    /// <param name="coldReadDeadline">
    ///     An optional local-read deadline. Production uses <see cref="ColdReadDeadline" />; tests can supply a
    ///     longer bounded wait when they need to assert the completed fallback rather than the normal cold-read
    ///     deferral contract.
    /// </param>
    public LocalIndexAccessProvider(
        IndexCoordinator coordinator,
        IWorkspaceIndexJobManager jobs,
        TimeSpan? coldReadDeadline = null)
    {
        if (coldReadDeadline is { } deadline && deadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(coldReadDeadline), "The cold-read deadline must be positive.");

        _coordinator = coordinator;
        _jobs = jobs;
        _coldReadDeadline = coldReadDeadline;
    }

    /// <inheritdoc />
    public async Task<WorkspaceIndexStore> OpenIndexedAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        var started = await _jobs.StartOrJoinAsync(
            new IndexJobRequest(root, IndexDepth.Syntax, Force: false, CaptureBundlePath: null),
            cancellationToken);
        if (started.Conflict)
            throw new IndexBusyException();

        var syntaxReady = _jobs.WaitForSyntaxReadyAsync(root, cancellationToken);
        var deadline = Task.Delay(
            _coldReadDeadline ?? TimeSpan.FromMilliseconds(ColdReadDeadline.DeadlineMilliseconds()),
            cancellationToken);
        if (await Task.WhenAny(syntaxReady, deadline) != syntaxReady)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ColdStartInProgressException(root);
        }

        var snapshot = await syntaxReady;
        if (snapshot is null)
            throw new InvalidOperationException("index job disappeared before syntax became readable");
        if (snapshot.State == IndexJobState.Cancelled)
            throw new OperationCanceledException("index job was cancelled", cancellationToken);
        if (snapshot.State == IndexJobState.Failed)
            throw new InvalidOperationException(snapshot.ErrorMessage ?? "index_failed");

        return await _coordinator.OpenForReadOnlyAsync(root, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SemanticIndexResult> IndexAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        var started = await _jobs.StartOrJoinAsync(
            new IndexJobRequest(root, IndexDepth.Syntax, Force: false, CaptureBundlePath: null),
            cancellationToken);
        if (started.Conflict)
            throw new IndexBusyException();

        var snapshot = await _jobs.WaitForCompletionAsync(root, cancellationToken)
            ?? throw new InvalidOperationException("index job disappeared before completion");
        if (snapshot.State == IndexJobState.Cancelled)
            throw new OperationCanceledException("index job was cancelled", cancellationToken);
        if (snapshot.State == IndexJobState.Failed)
            throw new InvalidOperationException(snapshot.ErrorMessage ?? "index_failed");

        return new SemanticIndexResult(
            "syntax",
            snapshot.Counts.Files,
            snapshot.Counts.Projects,
            snapshot.Counts.Symbols,
            snapshot.Counts.Chunks,
            snapshot.Counts.Routes,
            Diagnostics: []);
    }
}
