namespace Fuse.Cli.Services;

/// <summary>
///     Keeps the index live (R39): on a debounced file-system change (from <see cref="DebouncedFileWatcher" />,
///     which observes workspace source changes), it requests a syntax refresh through the repository job manager.
///     The watcher excludes Git metadata because the inventory stage invokes Git commands that can update it. The
///     manager deduplicates source changes with explicit index requests, so the watcher never opens the SQLite
///     writer directly. Default-on when the daemon is active; opt out with <c>FUSE_WATCH=0</c>. A periodic safety
///     request catches events a watcher dropped.
/// </summary>
public sealed class LiveIndexWatcher : IDisposable
{
    /// <summary>The environment variable that opts out of the live watcher.</summary>
    public const string EnvVar = "FUSE_WATCH";

    private readonly Func<CancellationToken, Task> _reconcile;
    private readonly CancellationToken _cancellationToken;
    private readonly Timer? _safetyTimer;
    private int _running; // overlap guard: 0 idle, 1 reconciling.
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LiveIndexWatcher" /> class.
    /// </summary>
    /// <param name="reconcile">The refresh request action.</param>
    /// <param name="safetyInterval">The periodic safety-reconcile interval, or null to disable it.</param>
    /// <param name="cancellationToken">A token to stop the watcher.</param>
    public LiveIndexWatcher(Func<CancellationToken, Task> reconcile, TimeSpan? safetyInterval, CancellationToken cancellationToken)
    {
        _reconcile = reconcile;
        _cancellationToken = cancellationToken;
        if (safetyInterval is { } interval && interval > TimeSpan.Zero)
            _safetyTimer = new Timer(_ => _ = HandleChangeAsync(_cancellationToken), null, interval, interval);
    }

    /// <summary>Whether the live watcher is enabled (default on; <c>0</c>/<c>false</c>/<c>no</c>/<c>off</c> opts out).</summary>
    /// <returns><see langword="true" /> unless explicitly opted out.</returns>
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
    ///     Requests a refresh now, best-effort and overlap-guarded. A request already in flight makes this a no-op;
    ///     the repository job manager still coalesces any request from another caller. Wired to a watcher's change event.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the refresh request.</param>
    /// <returns>A task that completes when the refresh request finishes (or is skipped).</returns>
    public async Task HandleChangeAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            return;

        // Overlap guard: only one reconcile at a time. A change during a reconcile is caught by the next event or
        // the safety timer, so no update is lost and the writer is never contended by stacked reconciles.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        try
        {
            await _reconcile(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a refresh request failure (contention, transient IO) must not tear down the daemon.
            // The next explicit read or watcher event can start the repository job again.
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    ///     Attaches a live watcher to a debounced file watcher, so each settled change requests a refresh. Returns
    ///     null when the live watcher is disabled (<c>FUSE_WATCH=0</c>), leaving on-read reconcile as the only
    ///     freshness path.
    /// </summary>
    /// <param name="watcher">The debounced file watcher over the served root.</param>
    /// <param name="reconcile">The refresh request action.</param>
    /// <param name="safetyInterval">The periodic safety-refresh interval.</param>
    /// <param name="cancellationToken">A token to stop the watcher.</param>
    /// <returns>The attached watcher, or null when disabled.</returns>
    public static LiveIndexWatcher? Attach(
        DebouncedFileWatcher watcher,
        Func<CancellationToken, Task> reconcile,
        TimeSpan? safetyInterval,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled())
            return null;

        var live = new LiveIndexWatcher(reconcile, safetyInterval, cancellationToken);
        watcher.Changed += live.HandleChangeAsync;
        return live;
    }

    /// <summary>Stops the safety timer.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _safetyTimer?.Dispose();
    }
}
