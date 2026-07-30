using System.Diagnostics;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

public sealed class OwnedProcessRunnerTests
{
    private static readonly TimeSpan ChildStartupTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Caller_cancellation_stops_the_owned_process_tree()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-owned-process", Guid.NewGuid().ToString("N"));
        var childPidPath = Path.Combine(work, "child.pid");
        Directory.CreateDirectory(work);
        var parentStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var runner = new OwnedProcessRunner(processId => parentStarted.TrySetResult(processId));
            using var cancellation = new CancellationTokenSource();
            var execution = runner.RunAsync(
                CreateSleepingTree(childPidPath),
                TimeSpan.FromMinutes(1),
                cancellation.Token);

            var parentPid = await parentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var childPid = await WaitForChildPidAsync(childPidPath);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await execution);
            await WaitForExitAsync(parentPid);
            await WaitForExitAsync(childPid);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static ProcessStartInfo CreateSleepingTree(string childPidPath)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "pwsh";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "$child = Start-Process -FilePath 'ping.exe' -ArgumentList @('127.0.0.1','-n','60') -PassThru; "
                + $"[System.IO.File]::WriteAllText('{EscapePowerShell(childPidPath)}', [string]$child.Id); "
                + "Wait-Process -Id $child.Id");
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"sleep 60 & echo $! > '{EscapeShell(childPidPath)}'; wait");
        }

        return startInfo;
    }

    private static async Task<int> WaitForChildPidAsync(string childPidPath)
    {
        var deadline = DateTime.UtcNow + ChildStartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(childPidPath)
                && int.TryParse(await File.ReadAllTextAsync(childPidPath), out var processId)
                && processId > 0)
            {
                return processId;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException("The owned child process did not write its process ID.");
    }

    private static async Task WaitForExitAsync(int processId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException($"Owned process {processId} remained alive after cancellation.");
    }

    private static string EscapePowerShell(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    private static string EscapeShell(string path) => path.Replace("'", "'\\''", StringComparison.Ordinal);
}
