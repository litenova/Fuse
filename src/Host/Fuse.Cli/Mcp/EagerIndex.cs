using Fuse.Cli.Services;
using Fuse.Collection.FileSystem;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Starts syntax-only warm requests through the host-owned repository job manager. Eager work is visible to
///     status and cancellable through the same lifecycle as an explicit <c>fuse index</c> request.
/// </summary>
public sealed class EagerIndex
{
    /// <summary>The environment variable that opts out of eager warm-on-start indexing.</summary>
    public const string EnvVar = "FUSE_EAGER_INDEX";

    private readonly IWorkspaceIndexJobManager _jobs;

    /// <summary>Initializes the eager index service.</summary>
    /// <param name="jobs">The host-owned manager that owns repository index jobs.</param>
    public EagerIndex(IWorkspaceIndexJobManager jobs) => _jobs = jobs;

    /// <summary>Whether eager warm-on-start indexing is enabled.</summary>
    /// <returns>True unless the environment explicitly opts out.</returns>
    public static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnvVar);
        return value is null
               || !(value.Equals("0", StringComparison.Ordinal)
                    || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Starts a syntax job for a served repository when eager warming is enabled. The returned task only
    ///     accepts the job; the job itself continues under the manager's host lifetime.
    /// </summary>
    /// <param name="root">The workspace root to warm.</param>
    /// <param name="cancellationToken">Cancels only the request acceptance.</param>
    /// <returns>The start request task, or null when eager warming is disabled or the root has no Git identity.</returns>
    public Task? Start(string root, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled() || !WorkspaceIdentityResolver.TryResolveRepositoryRoot(root, out var repositoryRoot))
            return null;

        return StartAsync(repositoryRoot, cancellationToken);
    }

    /// <summary>
    ///     Starts or joins a syntax job and waits until it reaches a terminal state. Used by explicit warm callers
    ///     that need the same completion guarantee as <c>fuse index</c>.
    /// </summary>
    /// <param name="root">The workspace root to warm.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait, not the shared job.</param>
    /// <returns>The completed job snapshot.</returns>
    /// <exception cref="InvalidOperationException">The root has no Git identity or the job fails.</exception>
    public async Task<IndexJobSnapshot> WarmAsync(string root, CancellationToken cancellationToken)
    {
        if (!WorkspaceIdentityResolver.TryResolveRepositoryRoot(root, out var repositoryRoot))
            throw new InvalidOperationException(
                $"Cannot warm '{Path.GetFullPath(root)}' because it is not inside a Git repository.");

        await StartAsync(repositoryRoot, cancellationToken);
        var snapshot = await _jobs.WaitForCompletionAsync(repositoryRoot, cancellationToken)
            ?? throw new InvalidOperationException("index job disappeared before completion");
        if (snapshot.State == IndexJobState.Failed)
            throw new InvalidOperationException(snapshot.ErrorMessage ?? "index_failed");
        if (snapshot.State == IndexJobState.Cancelled)
            throw new OperationCanceledException("index job was cancelled", cancellationToken);
        return snapshot;
    }

    private async Task StartAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        WarmServiceState.Record(repositoryRoot);
        var started = await _jobs.StartOrJoinAsync(
            new IndexJobRequest(repositoryRoot, IndexDepth.Syntax, Force: false, CaptureBundlePath: null),
            cancellationToken);
        if (started.Conflict)
            throw new InvalidOperationException(started.Snapshot.ErrorMessage ?? "index_job_conflict");
    }
}
