namespace Fuse.Cli.Mcp;

/// <summary>
///     Owns one cancellable index job for each normalized repository root.
/// </summary>
public interface IWorkspaceIndexJobManager : IAsyncDisposable
{
    /// <summary>Starts a compatible job or joins the repository's active job.</summary>
    /// <param name="request">The requested index operation.</param>
    /// <param name="cancellationToken">Cancels only this caller's request wait.</param>
    /// <returns>The shared job snapshot and acceptance outcome.</returns>
    Task<IndexJobStartResult> StartOrJoinAsync(IndexJobRequest request, CancellationToken cancellationToken);

    /// <summary>Gets the current or most recently finished job for a repository root.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The job snapshot, or null when no job has been recorded in this process.</returns>
    IndexJobSnapshot? GetStatus(string root);

    /// <summary>Requests cancellation of the active repository job.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">Cancels only the cancellation request wait.</param>
    /// <returns>The job snapshot, or null when no active job exists.</returns>
    Task<IndexJobSnapshot?> CancelAsync(string root, CancellationToken cancellationToken);

    /// <summary>Waits for the repository's active or retained job to reach a terminal state.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait.</param>
    /// <returns>The terminal job snapshot, or null when no job exists.</returns>
    Task<IndexJobSnapshot?> WaitForCompletionAsync(string root, CancellationToken cancellationToken);

    /// <summary>
    ///     Waits until a source job has committed its syntax tier. A semantic job completes this wait before its
    ///     compiler stages finish, allowing read tools to use the syntax index while compiler analysis continues.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait.</param>
    /// <returns>
    ///     The current snapshot after syntax becomes readable, or the terminal snapshot when the job stops before
    ///     committing syntax. Returns null when no job exists.
    /// </returns>
    Task<IndexJobSnapshot?> WaitForSyntaxReadyAsync(string root, CancellationToken cancellationToken) =>
        WaitForCompletionAsync(root, cancellationToken);

    /// <summary>Cancels jobs during host shutdown and waits for their workers to stop.</summary>
    /// <param name="cancellationToken">Bounds the shutdown wait.</param>
    /// <returns>A task that completes after active workers stop or the wait is cancelled.</returns>
    Task ShutdownAsync(CancellationToken cancellationToken);
}
