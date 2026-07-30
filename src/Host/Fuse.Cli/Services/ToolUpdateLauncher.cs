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

/// <summary>
///     A running Fuse peer process visible to <see cref="ToolUpdateLauncher" />.
/// </summary>
/// <param name="ProcessId">The operating-system process id.</param>
/// <param name="ParentProcessId">The parent process id when known.</param>
/// <param name="CommandLine">The best-effort command line, used to recognize <c>host</c> peers.</param>
internal readonly record struct FusePeerProcess(int ProcessId, int? ParentProcessId, string CommandLine);

/// <summary>
///     Selects which Fuse peers <c>fuse update</c> stops before launching the detached updater.
/// </summary>
internal static class FuseProcessStopSelector
{
    /// <summary>
    ///     Returns the peers to stop: either every other Fuse peer (<paramref name="forceKillPeers" />) or only
    ///     descendants of the updating process and install-mutex holders from the same install.
    /// </summary>
    /// <param name="peers">All enumerated Fuse peers.</param>
    /// <param name="selfProcessId">The updating process id.</param>
    /// <param name="installPath">The normalized install path of the updating process.</param>
    /// <param name="installMutexName">The install mutex name for <paramref name="installPath" />.</param>
    /// <param name="forceKillPeers">When true, every peer except <paramref name="selfProcessId" /> is selected.</param>
    /// <returns>The peers to kill.</returns>
    internal static IReadOnlyList<FusePeerProcess> SelectPeersToStop(
        IReadOnlyList<FusePeerProcess> peers,
        int selfProcessId,
        string installPath,
        string installMutexName,
        bool forceKillPeers)
    {
        if (forceKillPeers)
            return peers.Where(peer => peer.ProcessId != selfProcessId).ToArray();

        var descendants = CollectDescendants(peers, selfProcessId);
        var installMutexHolderIds = CollectInstallMutexHolderProcessIds(peers, installPath, installMutexName);
        return peers
            .Where(peer => peer.ProcessId != selfProcessId)
            .Where(peer => descendants.Contains(peer.ProcessId) || installMutexHolderIds.Contains(peer.ProcessId))
            .ToArray();
    }

    private static HashSet<int> CollectDescendants(IReadOnlyList<FusePeerProcess> peers, int selfProcessId)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        foreach (var peer in peers)
        {
            if (peer.ParentProcessId is not int parentId)
                continue;

            if (!childrenByParent.TryGetValue(parentId, out var children))
            {
                children = [];
                childrenByParent[parentId] = children;
            }

            children.Add(peer.ProcessId);
        }

        var descendants = new HashSet<int>();
        var queue = new Queue<int>();
        if (childrenByParent.TryGetValue(selfProcessId, out var directChildren))
        {
            foreach (var child in directChildren)
                queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!descendants.Add(current))
                continue;

            if (!childrenByParent.TryGetValue(current, out var children))
                continue;

            foreach (var child in children)
                queue.Enqueue(child);
        }

        return descendants;
    }

    private static HashSet<int> CollectInstallMutexHolderProcessIds(
        IReadOnlyList<FusePeerProcess> peers,
        string installPath,
        string installMutexName)
    {
        if (!AnyProcessHoldsInstallMutex(installMutexName))
            return [];

        return peers
            .Where(peer => RunsHostSubcommand(peer.CommandLine))
            .Where(peer => SharesInstallPath(peer.CommandLine, installPath))
            .Select(peer => peer.ProcessId)
            .ToHashSet();
    }

    private static bool AnyProcessHoldsInstallMutex(string installMutexName)
    {
        try
        {
            using var mutex = Mutex.OpenExisting(installMutexName);
            try
            {
                if (mutex.WaitOne(TimeSpan.Zero))
                {
                    mutex.ReleaseMutex();
                    return false;
                }

                return true;
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool RunsHostSubcommand(string commandLine) =>
        commandLine.Contains(" host", StringComparison.OrdinalIgnoreCase)
        || commandLine.Contains(" host\"", StringComparison.OrdinalIgnoreCase)
        || commandLine.EndsWith(" host", StringComparison.OrdinalIgnoreCase);

    private static bool SharesInstallPath(string commandLine, string installPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        return commandLine.Contains(installPath, StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains(Path.GetFileName(installPath), StringComparison.OrdinalIgnoreCase);
    }

}

/// <summary>
///     Enumerates running Fuse peer processes for update termination.
/// </summary>
internal interface IFusePeerDiscovery
{
    IReadOnlyList<FusePeerProcess> ListPeers(string installPath);
}

internal sealed class DelegateFusePeerDiscovery(Func<IReadOnlyList<FusePeerProcess>> listPeers) : IFusePeerDiscovery
{
    public IReadOnlyList<FusePeerProcess> ListPeers(string installPath) => listPeers();
}

/// <summary>
///     Enumerates running Fuse peer processes for update termination.
/// </summary>
internal sealed class FusePeerDiscovery : IFusePeerDiscovery
{
    public IReadOnlyList<FusePeerProcess> ListPeers(string installPath)
    {
        var peers = new List<FusePeerProcess>();
        var seen = new HashSet<int>();
        foreach (var processName in new[] { "fuse", "dotnet" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    if (!seen.Add(process.Id))
                        continue;

                    var commandLine = TryReadCommandLine(process);
                    if (!LooksLikeFusePeer(process, commandLine, installPath))
                        continue;

                    peers.Add(new FusePeerProcess(process.Id, FuseProcessParentId.TryRead(process.Id), commandLine));
                }
            }
        }

        return peers;
    }

    private static bool LooksLikeFusePeer(Process process, string commandLine, string installPath)
    {
        if (process.ProcessName.Equals("fuse", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!process.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return false;

        if (SharesInstallPath(commandLine, installPath))
            return true;

        if (CommandLineNamesFuseSubcommand(commandLine))
            return true;

        return LoadsFuseAssembly(process, installPath);
    }

    private static bool LoadsFuseAssembly(Process process, string installPath)
    {
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                var modulePath = module.FileName;
                if (string.IsNullOrWhiteSpace(modulePath))
                    continue;

                if (ToolInstallEndpoint.NormalizeInstallPath(modulePath) == installPath)
                    return true;
            }
        }
        catch (Exception)
        {
            // Module enumeration can fail for elevated or exited processes.
        }

        return false;
    }

    private static bool CommandLineNamesFuseSubcommand(string commandLine) =>
        commandLine.Contains(" mcp", StringComparison.OrdinalIgnoreCase)
        || commandLine.Contains(" host", StringComparison.OrdinalIgnoreCase);

    private static bool SharesInstallPath(string commandLine, string installPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        return commandLine.Contains(installPath, StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains(Path.GetFileName(installPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string TryReadCommandLine(Process process)
    {
        var fromOs = FuseProcessCommandLine.TryRead(process.Id);
        if (!string.IsNullOrWhiteSpace(fromOs))
            return fromOs;

        try
        {
            return process.MainModule?.FileName ?? process.ProcessName;
        }
        catch (Exception)
        {
            return process.ProcessName;
        }
    }
}

/// <summary>
///     Reads parent process ids from the operating system.
/// </summary>
internal static class FuseProcessParentId
{
    internal static int? TryRead(int processId)
    {
        if (OperatingSystem.IsLinux())
            return TryReadLinux(processId);

        if (OperatingSystem.IsWindows())
            return TryReadWindows(processId);

        return null;
    }

    private static int? TryReadLinux(int processId)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{processId}/status"))
            {
                if (!line.StartsWith("PPid:", StringComparison.Ordinal))
                    continue;

                return int.TryParse(line.AsSpan(5).Trim(), out var parentId) ? parentId : null;
            }
        }
        catch (Exception)
        {
            // Best effort only.
        }

        return null;
    }

    private static int? TryReadWindows(int processId)
    {
        try
        {
            using var handle = WindowsProcessSnapshot.OpenProcess(WindowsProcessSnapshot.ProcessQueryLimitedInformation, false, processId);
            if (handle.IsInvalid)
                return null;

            var status = WindowsProcessSnapshot.NtQueryInformationProcess(
                handle,
                WindowsProcessSnapshot.ProcessBasicInformationClass,
                out WindowsProcessSnapshot.ProcessBasicInformation information,
                Marshal.SizeOf<WindowsProcessSnapshot.ProcessBasicInformation>(),
                out _);
            if (status != 0)
                return null;

            return (int)information.InheritedFromUniqueProcessId;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
///     Reads process command lines from the operating system.
/// </summary>
internal static class FuseProcessCommandLine
{
    internal static string TryRead(int processId)
    {
        if (OperatingSystem.IsLinux())
            return TryReadLinux(processId);

        if (OperatingSystem.IsWindows())
            return TryReadWindows(processId);

        return string.Empty;
    }

    private static string TryReadLinux(int processId)
    {
        try
        {
            var bytes = File.ReadAllBytes($"/proc/{processId}/cmdline");
            if (bytes.Length == 0)
                return string.Empty;

            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0)
                    bytes[i] = (byte)' ';
            }

            return Encoding.UTF8.GetString(bytes).Trim();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string TryReadWindows(int processId)
    {
        try
        {
            using var handle = WindowsProcessSnapshot.OpenProcess(
                WindowsProcessSnapshot.ProcessQueryLimitedInformation | WindowsProcessSnapshot.ProcessVmRead,
                false,
                processId);
            if (handle.IsInvalid)
                return string.Empty;

            var status = WindowsProcessSnapshot.NtQueryInformationProcess(
                handle,
                WindowsProcessSnapshot.ProcessCommandLineInformationClass,
                out WindowsProcessSnapshot.ProcessCommandLineInformation commandLineInformation,
                Marshal.SizeOf<WindowsProcessSnapshot.ProcessCommandLineInformation>(),
                out _);
            if (status != 0)
                return string.Empty;

            return commandLineInformation.CommandLine.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}

internal static class WindowsProcessSnapshot
{
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint ProcessVmRead = 0x0010;
    internal const int ProcessBasicInformationClass = 0;
    internal const int ProcessCommandLineInformationClass = 60;

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        out ProcessCommandLineInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessCommandLineInformation
    {
        public UNICODE_STRING CommandLine;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;

        public override string? ToString()
        {
            if (Buffer == IntPtr.Zero || Length == 0)
                return string.Empty;

            return Marshal.PtrToStringUni(Buffer, Length / 2);
        }
    }

    internal sealed class SafeProcessHandle : SafeHandle
    {
        private SafeProcessHandle() : base(IntPtr.Zero, true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
