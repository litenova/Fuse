using System.Diagnostics;
using Fuse.Cli.Mcp;
using Fuse.Cli.Rpc;

namespace Fuse.Cli.Services;

/// <summary>
///     Routes CLI index lifecycle requests to the repository daemon by default and to the same job manager in the
///     current process only when <c>FUSE_DAEMON=0</c> is set.
/// </summary>
public sealed class IndexJobClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromMilliseconds(500);
    private readonly IWorkspaceIndexJobManager _localJobs;
    private readonly bool? _daemonEnabled;

    /// <summary>Initializes a new CLI index lifecycle client.</summary>
    /// <param name="localJobs">The in-process manager used only when the daemon is disabled.</param>
    /// <param name="daemonEnabled">
    ///     Overrides daemon selection for a caller that owns an in-process test host. Null reads
    ///     <c>FUSE_DAEMON</c>; false keeps every lifecycle operation in the supplied manager.
    /// </param>
    public IndexJobClient(IWorkspaceIndexJobManager localJobs, bool? daemonEnabled = null)
    {
        _localJobs = localJobs;
        _daemonEnabled = daemonEnabled;
    }

    /// <summary>Starts or joins a repository job.</summary>
    /// <param name="request">The requested index operation.</param>
    /// <param name="cancellationToken">Cancels only this command's connection or start request.</param>
    /// <returns>The accepted job and the backend selected for subsequent status calls.</returns>
    public async Task<IndexJobClientStartResult> StartAsync(IndexJobRequest request, CancellationToken cancellationToken)
    {
        if (DaemonDisabled())
        {
            var local = await _localJobs.StartOrJoinAsync(request, cancellationToken);
            return new IndexJobClientStartResult(local, UsesDaemon: false);
        }

        var root = request.Root;
        var stale = await RetireStaleDaemonAsync(root, cancellationToken);
        if (stale is not null && !stale.Value.Stopped)
        {
            throw new IndexJobClientException(
                "daemon_protocol_mismatch",
                $"Daemon protocol {stale.Value.OldProtocol} does not match required protocol "
                + $"{FuseHostService.ProtocolVersion}; daemon PID {stale.Value.ProcessId} did not stop. "
                + $"Run 'fuse host --directory \"{root}\"' after stopping that daemon.");
        }

        var supervisor = new DaemonSupervisor(
            token => FuseHostClient.IsServingAsync(root, TimeSpan.FromMilliseconds(500), token),
            () => DaemonProcessLauncher.Spawn(root, idleMinutes: 15));
        var daemon = await supervisor.EnsureRunningAsync(TimeSpan.FromSeconds(10), cancellationToken);
        if (daemon == DaemonSupervisor.Outcome.FailedToStart)
        {
            if (stale is not null)
            {
                throw new IndexJobClientException(
                    "daemon_protocol_mismatch",
                    $"Daemon protocol {stale.Value.OldProtocol} was replaced for protocol "
                    + $"{FuseHostService.ProtocolVersion}, but the matching daemon did not start; daemon PID "
                    + $"{stale.Value.ProcessId}. Run 'fuse host --directory \"{root}\"'.");
            }
            throw new IndexJobClientException(
                "daemon_unavailable",
                $"Could not start the Fuse daemon for {root}. Run 'fuse host --directory \"{root}\"' or set FUSE_DAEMON=0.");
        }

        var started = await FuseHostClient.TryIndexStartAsync(
            root,
            request.Depth,
            request.Force,
            request.CaptureBundlePath,
            ConnectTimeout,
            cancellationToken);
        if (started is null)
            throw new IndexJobClientException(
                "daemon_unavailable",
                $"Fuse daemon did not accept the index request for {root}. Run 'fuse host --directory \"{root}\"'.");
        return new IndexJobClientStartResult(started, UsesDaemon: true);
    }

    /// <summary>Reads a job snapshot from the backend selected by a prior start request.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="usesDaemon">Whether the start request used the daemon.</param>
    /// <param name="cancellationToken">Cancels only this status request.</param>
    /// <returns>The current job snapshot, or null when no job is retained.</returns>
    public Task<IndexJobSnapshot?> StatusAsync(string root, bool usesDaemon, CancellationToken cancellationToken) =>
        usesDaemon
            ? FuseHostClient.TryIndexStatusAsync(root, ConnectTimeout, cancellationToken)
            : Task.FromResult(_localJobs.GetStatus(root));

    /// <summary>Reads status without spawning a daemon.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">Cancels only this status request.</param>
    /// <returns>The retained daemon or local snapshot, or null when none exists.</returns>
    public async Task<IndexJobClientStatusResult> StatusAsync(string root, CancellationToken cancellationToken)
    {
        if (!DaemonDisabled())
        {
            var remote = await FuseHostClient.TryIndexStatusAsync(root, StatusTimeout, cancellationToken);
            if (remote is not null)
                return new IndexJobClientStatusResult(remote, UsesDaemon: true);
        }

        return new IndexJobClientStatusResult(_localJobs.GetStatus(root), UsesDaemon: false);
    }

    /// <summary>Requests cancellation without spawning a daemon.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="usesDaemon">Whether cancellation targets a daemon-owned job.</param>
    /// <param name="cancellationToken">Cancels only this cancellation request.</param>
    /// <returns>The updated snapshot, or null when no active job exists.</returns>
    public Task<IndexJobSnapshot?> CancelAsync(string root, bool usesDaemon, CancellationToken cancellationToken) =>
        usesDaemon
            ? FuseHostClient.TryIndexCancelAsync(root, ConnectTimeout, cancellationToken)
            : _localJobs.CancelAsync(root, cancellationToken);

    /// <summary>Requests cancellation from whichever backend currently retains a job.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">Cancels only this cancellation request.</param>
    /// <returns>The updated snapshot and backend used.</returns>
    public async Task<IndexJobClientStatusResult> CancelAsync(string root, CancellationToken cancellationToken)
    {
        if (!DaemonDisabled())
        {
            var remote = await FuseHostClient.TryIndexCancelAsync(root, ConnectTimeout, cancellationToken);
            if (remote is not null)
                return new IndexJobClientStatusResult(remote, UsesDaemon: true);
        }

        return new IndexJobClientStatusResult(
            await _localJobs.CancelAsync(root, cancellationToken),
            UsesDaemon: false);
    }

    private bool DaemonDisabled()
    {
        if (_daemonEnabled is not null)
            return !_daemonEnabled.Value;

        var value = Environment.GetEnvironmentVariable("FUSE_DAEMON");
        return value is not null && (value.Equals("0", StringComparison.Ordinal)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<StaleDaemonResult?> RetireStaleDaemonAsync(string root, CancellationToken cancellationToken)
    {
        var handshake = await FuseHostClient.TryHandshakeAsync(root, ConnectTimeout, cancellationToken);
        if (handshake is null || handshake.ProtocolVersion == FuseHostService.ProtocolVersion)
            return null;

        var stats = await FuseHostClient.TryStatsIgnoringProtocolAsync(root, ConnectTimeout, cancellationToken);
        var processId = stats?.ProcessId
            ?? DaemonRegistry.List()
                .FirstOrDefault(descriptor => SameRoot(descriptor.Root, root))
                ?.ProcessId
            ?? 0;

        await FuseHostClient.TryShutdownIgnoringProtocolAsync(root, ConnectTimeout, cancellationToken);
        var stopped = await WaitForDaemonStopAsync(root, processId, cancellationToken);
        return new StaleDaemonResult(handshake.ProtocolVersion, processId, stopped);
    }

    private static async Task<bool> WaitForDaemonStopAsync(string root, int processId, CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(5);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (await FuseHostClient.TryHandshakeAsync(root, TimeSpan.FromMilliseconds(250), cancellationToken) is null)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        if (processId <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }

        return await FuseHostClient.TryHandshakeAsync(root, TimeSpan.FromMilliseconds(250), cancellationToken) is null;
    }

    private static bool SameRoot(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private readonly record struct StaleDaemonResult(int OldProtocol, int ProcessId, bool Stopped);
}

/// <summary>The backend selected for a CLI index start request.</summary>
/// <param name="Result">The accepted job result.</param>
/// <param name="UsesDaemon">Whether later lifecycle calls must use the host RPC.</param>
public sealed record IndexJobClientStartResult(IndexJobStartResult Result, bool UsesDaemon);

/// <summary>A job snapshot and the backend that supplied it.</summary>
/// <param name="Snapshot">The active or retained job snapshot.</param>
/// <param name="UsesDaemon">Whether the snapshot came from the host RPC.</param>
public sealed record IndexJobClientStatusResult(IndexJobSnapshot? Snapshot, bool UsesDaemon);

/// <summary>Identifies a CLI lifecycle transport failure with a stable code and recovery command.</summary>
public sealed class IndexJobClientException : Exception
{
    /// <summary>Initializes a CLI lifecycle transport exception.</summary>
    /// <param name="code">The stable machine-readable error code.</param>
    /// <param name="message">The direct recovery message.</param>
    public IndexJobClientException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>The stable machine-readable error code.</summary>
    public string Code { get; }
}
