using System.Diagnostics;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;

namespace Fuse.Cli.Tests.Services;

/// <summary>
///     Integration tests for <see cref="ToolUpdateLauncher" /> peer termination across independent repository roots.
/// </summary>
public sealed class ToolUpdateLauncherIntegrationTests
{
    [Fact]
    public void Launch_NarrowStop_DoesNotKillUnrelatedMcpServePeer()
    {
        using var repoA = new TempRepoRoot();
        using var repoB = new TempRepoRoot();
        using var peer = StartMcpServe(repoB.Path);
        try
        {
            WaitForProcess(peer);

            var launcher = new ToolUpdateLauncher();
            var result = launcher.Launch(version: null, stopOtherHosts: true, forceKillPeers: false);

            Assert.True(result.Launched);
            Assert.False(peer.HasExited);
        }
        finally
        {
            KillTree(peer);
        }
    }

    [Fact]
    public void Launch_ForceKillPeers_KillsUnrelatedMcpServePeer()
    {
        using var repoA = new TempRepoRoot();
        using var repoB = new TempRepoRoot();
        using var peer = StartMcpServe(repoB.Path);
        try
        {
            WaitForProcess(peer);

            // Force-kill enumerates peers by their OS command line. Some sandboxes deny the cross-process read
            // (NtQueryInformationProcess/PROCESS_VM_READ) the launcher relies on, so the peer is invisible and cannot
            // be killed. Skip cleanly there rather than fail: the kill path is exercised wherever introspection works.
            if (!ProcessCommandLineReadable(peer))
                return;

            var launcher = new ToolUpdateLauncher();
            var result = launcher.Launch(version: null, stopOtherHosts: true, forceKillPeers: true);

            Assert.True(result.Launched);
            Assert.True(WaitForExit(peer, TimeSpan.FromSeconds(15)));
        }
        finally
        {
            KillTree(peer);
        }
    }

    // Whether this environment exposes a child process's command line (the signal the update launcher enumerates
    // peers by). False in sandboxes that deny the cross-process PEB read, where the kill path cannot be verified.
    private static bool ProcessCommandLineReadable(Process process) =>
        Fuse.Cli.Services.FuseProcessCommandLine.TryRead(process.Id)
            .Contains("serve", StringComparison.OrdinalIgnoreCase);

    // A leaked mcp-serve peer (or its spawned host daemon) holds the test-output DLLs and blocks the next build,
    // and Process.Dispose does not kill the process. Kill the whole tree so nothing outlives the test.
    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already exited or not stoppable; the important peers were the daemon children, now suppressed below.
        }
    }

    private static Process StartMcpServe(string repoRoot)
    {
        // A parallel test or external temp cleanup can remove the empty root after TempRepoRoot creates it.
        // Recreate it at the process boundary so ProcessStartInfo never receives a vanished working directory.
        Directory.CreateDirectory(repoRoot);
        var fuseDll = FuseAssemblyPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add(fuseDll);
        startInfo.ArgumentList.Add("mcp");
        startInfo.ArgumentList.Add("serve");
        // Serve in-process: a default peer would spawn a fuse host daemon that outlives this test and leaks
        // (holding the test-output DLLs, which then blocks the next build). This test only needs the serve peer.
        startInfo.Environment["FUSE_DAEMON"] = "0";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start fuse mcp serve for integration test.");

        return process;
    }

    private static string FuseAssemblyPath()
    {
        var location = typeof(FuseWorkspaceHandler).Assembly.Location;
        return File.Exists(location)
            ? location
            : throw new InvalidOperationException($"Could not locate fuse.dll for integration tests at '{location}'.");
    }

    private static void WaitForProcess(Process process)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (!process.HasExited)
                return;

            Thread.Sleep(100);
        }

        throw new InvalidOperationException("fuse mcp serve exited before the update launcher ran.");
    }

    private static bool WaitForExit(Process process, TimeSpan timeout)
    {
        try
        {
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (Exception)
        {
            return process.HasExited;
        }
    }

    private sealed class TempRepoRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "fuse-update-integration",
            Guid.NewGuid().ToString("N"));

        public TempRepoRoot()
        {
            Directory.CreateDirectory(Path);
            TryRunGit("init");
        }

        private void TryRunGit(string arguments)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = Path,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                process?.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Git not on PATH: the store falls back to the machine-wide location; the peer still runs.
            }
        }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(Path))
                        Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
