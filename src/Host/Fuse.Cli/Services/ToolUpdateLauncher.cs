using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Fuse.Cli.Services;

/// <summary>
///     The outcome of launching the detached tool updater.
/// </summary>
/// <param name="Launched">Whether the detached updater process started.</param>
/// <param name="DotnetArguments">The <c>dotnet</c> arguments the updater runs, for messaging or a manual fallback.</param>
/// <param name="LogPath">The path the updater writes its combined output to.</param>
/// <param name="Error">The launch failure message when <paramref name="Launched" /> is false; otherwise null.</param>
public sealed record ToolUpdateLaunch(bool Launched, string DotnetArguments, string LogPath, string? Error);

/// <summary>
///     Derives the install-scoped mutex name for a Fuse global-tool install path, so update can stop only the
///     processes that share this install rather than every <c>fuse</c> peer on the machine.
/// </summary>
public static class ToolInstallEndpoint
{
    /// <summary>
    ///     The named mutex for a Fuse install path. Long-lived hosts acquire this while they run so
    ///     <see cref="ToolUpdateLauncher" /> can stop install-lock holders without killing unrelated peers.
    /// </summary>
    /// <param name="installPath">The absolute fuse executable or managed-assembly path.</param>
    /// <returns>A stable mutex name such as <c>fuse-install-1a2b3c4d5e6f7a8b</c>.</returns>
    public static string MutexName(string installPath) => "fuse-install-" + InstallHash(installPath);

    /// <summary>
    ///     Resolves the current process install path: the apphost when published, otherwise the fuse managed dll.
    /// </summary>
    /// <returns>The normalized install path.</returns>
    public static string ResolveInstallPath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
            return NormalizeInstallPath(processPath);

        return NormalizeInstallPath(typeof(ToolUpdateLauncher).Assembly.Location);
    }

    /// <summary>
    ///     Normalizes an install path for stable comparison and hashing.
    /// </summary>
    /// <param name="installPath">The executable or assembly path.</param>
    /// <returns>The normalized absolute path.</returns>
    public static string NormalizeInstallPath(string installPath) =>
        Path.GetFullPath(Path.TrimEndingDirectorySeparator(installPath)).ToLowerInvariant();

    private static string InstallHash(string installPath)
    {
        var normalized = NormalizeInstallPath(installPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}

/// <summary>
///     Holds the install mutex for the running Fuse process. <c>fuse host</c> keeps one alive for its lifetime so
///     <see cref="ToolUpdateLauncher" /> can stop install-lock holders during <c>fuse update</c>.
/// </summary>
public sealed class ToolInstallLock : IDisposable
{
    private readonly Mutex? _mutex;
    private bool _released;

    private ToolInstallLock(Mutex? mutex) => _mutex = mutex;

    /// <summary>
    ///     Tries to acquire the install mutex for the current Fuse install path.
    /// </summary>
    /// <returns>A lock when acquisition succeeds; otherwise null.</returns>
    public static ToolInstallLock? TryAcquire()
    {
        var installPath = ToolInstallEndpoint.ResolveInstallPath();
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, ToolInstallEndpoint.MutexName(installPath));
        }
        catch (Exception)
        {
            return null;
        }

        var owns = false;
        try
        {
            owns = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owns = true;
        }
        catch (Exception)
        {
            mutex.Dispose();
            return null;
        }

        if (!owns)
        {
            mutex.Dispose();
            return null;
        }

        return new ToolInstallLock(mutex);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_released || _mutex is null)
            return;

        _released = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (Exception)
        {
            // Best effort; the process is shutting down.
        }

        _mutex.Dispose();
    }
}

/// <summary>
///     Launches the detached updater that upgrades the Fuse global tool once the current process exits, working
///     around the fact that a running .NET tool locks its own files on Windows. Shared by the explicit
///     <c>fuse update</c> command and the opt-in background auto-update.
/// </summary>
public sealed class ToolUpdateLauncher
{
    private readonly IFusePeerDiscovery _peerDiscovery;
    private readonly IDetachedUpdateProcessLauncher _processLauncher;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ToolUpdateLauncher" /> class.
    /// </summary>
    public ToolUpdateLauncher() : this(new FusePeerDiscovery(), new DetachedUpdateProcessLauncher())
    {
    }

    /// <summary>
    ///     Initializes a test instance that supplies a fixed peer list instead of enumerating the OS process table.
    /// </summary>
    /// <param name="listPeersForTests">The peer list to return from the peer-discovery service.</param>
    internal ToolUpdateLauncher(Func<IReadOnlyList<FusePeerProcess>> listPeersForTests)
        : this(new DelegateFusePeerDiscovery(listPeersForTests), new DetachedUpdateProcessLauncher())
    {
    }

    internal ToolUpdateLauncher(IFusePeerDiscovery peerDiscovery, IDetachedUpdateProcessLauncher processLauncher)
    {
        _peerDiscovery = peerDiscovery;
        _processLauncher = processLauncher;
    }

    /// <summary>
    ///     Writes and starts the detached updater.
    /// </summary>
    /// <param name="version">The exact version to install, or null for the latest stable.</param>
    /// <param name="stopOtherHosts">
    ///     When true, a narrow set of Fuse peers is stopped before launching the updater (the explicit
    ///     <c>fuse update</c> path). When false, siblings are left running (the background auto-update path, which
    ///     must not disrupt other sessions); the update then succeeds only once those processes exit on their own.
    /// </param>
    /// <param name="forceKillPeers">
    ///     When true with <paramref name="stopOtherHosts" />, every other Fuse peer on the machine is stopped (the
    ///     pre-4.2 breadth). Prefer the default narrow stop on shared CI agents and multi-repo workflows.
    /// </param>
    /// <param name="onHostStopped">An optional callback invoked with a message for each host stopped.</param>
    /// <returns>The launch outcome, including the log path and the command for a manual fallback.</returns>
    public ToolUpdateLaunch Launch(
        string? version,
        bool stopOtherHosts,
        bool forceKillPeers = false,
        Action<string>? onHostStopped = null)
    {
        if (stopOtherHosts)
            StopFusePeers(forceKillPeers, onHostStopped);

        var arguments = ToolUpdatePlanner.BuildDotnetArguments(version);
        var isWindows = OperatingSystem.IsWindows();
        var workDirectory = Path.Combine(Path.GetTempPath(), "fuse-update");
        var logPath = Path.Combine(workDirectory, "update.log");
        try
        {
            Directory.CreateDirectory(workDirectory);
            var scriptPath = Path.Combine(workDirectory, isWindows ? "update.ps1" : "update.sh");
            File.WriteAllText(scriptPath, ToolUpdatePlanner.BuildUpdaterScript(isWindows, Environment.ProcessId, arguments, logPath));
            _processLauncher.Launch(scriptPath, isWindows);
        }
        catch (Exception ex)
        {
            return new ToolUpdateLaunch(false, arguments, logPath, ex.Message);
        }

        return new ToolUpdateLaunch(true, arguments, logPath, null);
    }

    private void StopFusePeers(bool forceKillPeers, Action<string>? onHostStopped)
    {
        var selfId = Environment.ProcessId;
        var installPath = ToolInstallEndpoint.ResolveInstallPath();
        var installMutexName = ToolInstallEndpoint.MutexName(installPath);
        IReadOnlyList<FusePeerProcess> peers;
        try
        {
            peers = _peerDiscovery.ListPeers(installPath);
        }
        catch (Exception)
        {
            // Process enumeration can fail under restricted environments; the update can still proceed.
            return;
        }

        foreach (var peer in FuseProcessStopSelector.SelectPeersToStop(peers, selfId, installPath, installMutexName, forceKillPeers))
        {
            try
            {
                using var process = Process.GetProcessById(peer.ProcessId);
                process.Kill(entireProcessTree: true);
                onHostStopped?.Invoke($"Stopped running Fuse host (pid {peer.ProcessId}).");
            }
            catch (Exception)
            {
                // A host that already exited or that we cannot stop is not fatal; the updater still tries.
            }
        }
    }

}
