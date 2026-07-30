using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Fuse.Cli.Services;

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
