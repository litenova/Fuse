using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Executes a repository index pass on behalf of the job manager.
/// </summary>
public interface IWorkspaceIndexJobExecutor
{
    /// <summary>Runs the requested index work and reports stage boundaries.</summary>
    /// <param name="jobId">The daemon-owned job identifier to persist with its state marker.</param>
    /// <param name="request">The normalized repository request.</param>
    /// <param name="progress">The manager-owned progress callback.</param>
    /// <param name="cancellationToken">The job lifetime cancellation token.</param>
    /// <returns>The final index result.</returns>
    Task<SemanticIndexResult> ExecuteAsync(
        string jobId,
        IndexJobRequest request,
        IProgress<IndexJobProgress> progress,
        CancellationToken cancellationToken);
}
