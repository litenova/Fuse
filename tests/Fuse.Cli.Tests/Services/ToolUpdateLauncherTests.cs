using Fuse.Cli.Services;

namespace Fuse.Cli.Tests.Services;

public sealed class ToolUpdateLauncherTests
{
    private const int SelfId = 1000;
    // Resolve the install path exactly as ToolInstallLock/StopFusePeers do (Environment.ProcessPath first): under
    // dotnet test the process path is the test host, not this assembly, so the assembly location would mismatch the
    // mutex name TryAcquire actually holds and the install-mutex holder would never be recognized.
    private static readonly string InstallPath = ToolInstallEndpoint.ResolveInstallPath();
    private static readonly string InstallMutexName = ToolInstallEndpoint.MutexName(InstallPath);

    [Fact]
    public void SelectPeersToStop_ForceKillPeers_SelectsEveryOtherFusePeer()
    {
        var peers = new FusePeerProcess[]
        {
            new(SelfId, 1, "fuse update"),
            new(2001, 1, "dotnet fuse.dll mcp serve"),
            new(2002, 1, "dotnet fuse.dll mcp serve"),
        };

        var selected = FuseProcessStopSelector.SelectPeersToStop(peers, SelfId, InstallPath, InstallMutexName, forceKillPeers: true);

        Assert.Equal([2001, 2002], selected.Select(peer => peer.ProcessId).OrderBy(id => id));
    }

    [Fact]
    public void SelectPeersToStop_Narrow_Default_KillsOnlyChildLineage()
    {
        var peers = new FusePeerProcess[]
        {
            new(SelfId, 1, "fuse update"),
            new(2001, SelfId, "dotnet fuse.dll host --directory /repo-a"),
            new(2002, 2001, "dotnet fuse.dll mcp serve"),
            new(3001, 1, "dotnet fuse.dll mcp serve"),
        };

        var selected = FuseProcessStopSelector.SelectPeersToStop(peers, SelfId, InstallPath, InstallMutexName, forceKillPeers: false);

        Assert.Equal([2001, 2002], selected.Select(peer => peer.ProcessId).OrderBy(id => id));
        Assert.DoesNotContain(selected, peer => peer.ProcessId == 3001);
    }

    [Fact]
    public void SelectPeersToStop_Narrow_Default_DoesNotKillUnrelatedSiblingPeer()
    {
        var peers = new FusePeerProcess[]
        {
            new(SelfId, 1, "fuse update"),
            new(4001, 1, $"dotnet \"{InstallPath}\" mcp serve"),
            new(4002, 1, $"dotnet \"{InstallPath}\" mcp serve"),
        };

        var selected = FuseProcessStopSelector.SelectPeersToStop(peers, SelfId, InstallPath, InstallMutexName, forceKillPeers: false);

        Assert.Empty(selected);
    }

    [Fact]
    public void SelectPeersToStop_Narrow_WithInstallMutexHolder_IncludesSameInstallHost()
    {
        // Hold the install mutex on a dedicated thread. AnyProcessHoldsInstallMutex runs on this test thread, and a
        // Windows named mutex is owned per thread: acquiring it here would be reentrant and read as free.
        using var lockHolder = new ForeignMutexHolder(InstallMutexName);

        var peers = new FusePeerProcess[]
        {
            new(SelfId, 1, "fuse update"),
            new(5001, 1, $"dotnet \"{InstallPath}\" host --directory /repo-a"),
            new(5002, 1, $"dotnet \"{InstallPath}\" mcp serve"),
        };

        var selected = FuseProcessStopSelector.SelectPeersToStop(peers, SelfId, InstallPath, InstallMutexName, forceKillPeers: false);

        Assert.Contains(selected, peer => peer.ProcessId == 5001);
        Assert.DoesNotContain(selected, peer => peer.ProcessId == 5002);
    }

    [Fact]
    public void Launch_WithInjectedPeers_StopOtherHostsFalse_DoesNotStopPeers()
    {
        var stopped = new List<int>();
        var launcher = new ToolUpdateLauncher(() =>
        [
            new FusePeerProcess(9001, 1, "fuse mcp serve"),
        ]);

        var result = launcher.Launch(
            version: null,
            stopOtherHosts: false,
            forceKillPeers: true,
            onHostStopped: message =>
            {
                var start = message.IndexOf("(pid ", StringComparison.Ordinal) + 5;
                var end = message.IndexOf(')', start);
                if (start > 4 && end > start && int.TryParse(message.AsSpan(start, end - start), out var pid))
                    stopped.Add(pid);
            });

        Assert.True(result.Launched);
        Assert.Empty(stopped);
    }

    [Fact]
    public void Launch_Uses_the_injected_detached_process_launcher()
    {
        var processLauncher = new RecordingProcessLauncher();
        var launcher = new ToolUpdateLauncher(
            new DelegateFusePeerDiscovery(() => []),
            processLauncher);

        var result = launcher.Launch(version: "4.4.0", stopOtherHosts: false);

        Assert.True(result.Launched);
        Assert.NotNull(processLauncher.ScriptPath);
        Assert.Equal(OperatingSystem.IsWindows(), processLauncher.IsWindows);
    }

    // Holds a named mutex on a dedicated thread so a check on any other thread sees it held by another owner
    // (a Windows named mutex is owned per thread; a same-thread reacquire is reentrant and would read as free).
    private sealed class ForeignMutexHolder : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _acquired = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public ForeignMutexHolder(string name)
        {
            _thread = new Thread(() =>
            {
                using var mutex = new Mutex(initiallyOwned: false, name);
                if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("could not acquire the install mutex for the test.");
                _acquired.Set();
                _release.Wait();
                mutex.ReleaseMutex();
            })
            {
                IsBackground = true,
            };
            _thread.Start();
            Assert.True(_acquired.Wait(TimeSpan.FromSeconds(5)));
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(5));
            _acquired.Dispose();
            _release.Dispose();
        }
    }

    private sealed class RecordingProcessLauncher : IDetachedUpdateProcessLauncher
    {
        public string? ScriptPath { get; private set; }

        public bool? IsWindows { get; private set; }

        public void Launch(string scriptPath, bool isWindows)
        {
            ScriptPath = scriptPath;
            IsWindows = isWindows;
        }
    }
}
