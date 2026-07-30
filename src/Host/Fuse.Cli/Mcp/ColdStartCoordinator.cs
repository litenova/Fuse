using System.Collections.Concurrent;
using System.Diagnostics;
using Fuse.Collection.FileSystem;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Bounds the first cold read (R27): the syntax-first index build runs in the background, deduplicated per
///     workspace root, and a read waits only up to a deadline. If the build completes within the deadline the read
///     serves; otherwise the read returns a bounded <c>building_syntax</c> header while the build continues, so a
///     cold repo never blocks a read for the full build (tens of seconds on a large repo). A second read in the
///     same session joins the running build (or finds it warm), never restarting it.
/// </summary>
internal sealed class ColdStartCoordinator
{
    /// <summary>The default deadline a cold read waits for the background syntax build before returning a header.</summary>
    internal const int DefaultDeadlineMilliseconds = 2500;

    /// <summary>The environment variable that overrides <see cref="DefaultDeadlineMilliseconds" />.</summary>
    internal const string DeadlineEnvVar = "FUSE_COLD_READ_DEADLINE_MS";

    private readonly ConcurrentDictionary<string, Task> _builds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _buildStartTimestamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves the cold-read deadline in milliseconds from the environment or the default.</summary>
    /// <returns>The deadline (always positive).</returns>
    internal static int DeadlineMilliseconds() =>
        int.TryParse(Environment.GetEnvironmentVariable(DeadlineEnvVar), out var ms) && ms > 0
            ? ms
            : DefaultDeadlineMilliseconds;

    /// <summary>
    ///     Runs (or joins) the per-root background build and waits up to the deadline. The build itself runs
    ///     detached (its own cancellation), so a caller whose wait is cancelled or times out does not cancel the
    ///     shared build.
    /// </summary>
    /// <param name="root">The absolute workspace root (dedup key).</param>
    /// <param name="build">The build to run in the background; invoked at most once per concurrent set of callers.</param>
    /// <param name="deadlineMilliseconds">How long to wait before returning <see langword="false" />.</param>
    /// <param name="waitToken">Cancels the wait (not the build).</param>
    /// <returns><see langword="true" /> when the build completed within the deadline; <see langword="false" /> when it is still running.</returns>
    public async Task<bool> BuildWithDeadlineAsync(
        string root,
        Func<CancellationToken, Task> build,
        int deadlineMilliseconds,
        CancellationToken waitToken)
    {
        var task = StartBuild(root, build);
        var finished = await Task.WhenAny(task, Task.Delay(deadlineMilliseconds, waitToken));
        if (finished == task)
        {
            await task; // observe faults / completion
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Starts (or joins) the per-root background build without waiting (R38 eager warm-on-start). The build is
    ///     deduplicated per root, so an eager start at daemon spawn and a later read share one build.
    /// </summary>
    /// <param name="root">The absolute workspace root (dedup key).</param>
    /// <param name="build">The build to run in the background; invoked at most once per concurrent set of callers.</param>
    /// <returns>The running (or already-running) build task.</returns>
    public Task StartBuild(string root, Func<CancellationToken, Task> build)
    {
        var key = RootKey(root);
        return _builds.GetOrAdd(key, _ => RunAndCleanupAsync(key, build));
    }

    /// <summary>Whether a background build is currently in flight for the root (test and diagnostic seam).</summary>
    /// <param name="root">The workspace root.</param>
    /// <returns><see langword="true" /> when a build is running.</returns>
    internal bool HasInFlightBuild(string root) =>
        _builds.ContainsKey(RootKey(root));

    /// <summary>
    ///     How long the in-flight cold build for a root has been running (R37 progress), or <see langword="null" />
    ///     when no build is in flight.
    /// </summary>
    /// <param name="root">The workspace root.</param>
    /// <returns>The elapsed build time, or null.</returns>
    public TimeSpan? ElapsedFor(string root)
    {
        var key = RootKey(root);
        return _buildStartTimestamps.TryGetValue(key, out var start)
            ? Stopwatch.GetElapsedTime(start)
            : null;
    }

    private async Task RunAndCleanupAsync(string key, Func<CancellationToken, Task> build)
    {
        _buildStartTimestamps[key] = Stopwatch.GetTimestamp();
        try
        {
            await build(CancellationToken.None);
        }
        finally
        {
            _builds.TryRemove(key, out _);
            _buildStartTimestamps.TryRemove(key, out _);
        }
    }

    private static string RootKey(string root) =>
        WorkspaceIdentityResolver.TryResolveRepositoryRoot(root, out var repositoryRoot)
            ? WorkspaceIdentityResolver.NormalizeKey(repositoryRoot)
            : WorkspaceIdentityResolver.NormalizeKey(root);
}

/// <summary>
///     Raised when a cold read's background syntax build did not finish within the cold-read deadline (R27). MCP
///     read tools map this to a bounded <c>building_syntax</c> availability header rather than blocking; the build
///     continues and the next read serves the warming index.
/// </summary>
internal sealed class ColdStartInProgressException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ColdStartInProgressException" /> class.</summary>
    /// <param name="root">The workspace root whose index is building.</param>
    public ColdStartInProgressException(string root)
        : base($"cold index build in progress for {root}") => Root = root;

    /// <summary>The workspace root whose index is building.</summary>
    public string Root { get; }
}
