using System.Diagnostics;
using Fuse.BuildCaptureWorker;
using Fuse.Semantics;
using Xunit;

namespace Fuse.BuildCaptureWorker.Tests;

/// <summary>
///     Guards the capture build command that the worker owns and cancels as one process tree.
/// </summary>
public sealed class BuildCaptureProcessExecutionTests
{
    [Fact]
    public async Task Capture_uses_a_bounded_non_incremental_compiler_build()
    {
        var runner = new RecordingProcessRunner();
        var rehydrator = new BuildCaptureRehydrator(runner);
        var target = Path.Combine(Path.GetTempPath(), "fuse-capture-process-tests", "Project.csproj");

        var result = await rehydrator.CaptureAsync(target, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(runner.StartInfo);
        var startInfo = runner.StartInfo!;
        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal(
            ["build", target, "--no-incremental", "-nologo", "-v:quiet"],
            startInfo.ArgumentList.Where(argument => !argument.StartsWith("-bl:", StringComparison.Ordinal)).ToList());
        Assert.Contains(startInfo.ArgumentList, argument => argument.StartsWith("-bl:", StringComparison.Ordinal));
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(
            ProcessStartInfo startInfo,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            StartInfo = startInfo;
            return Task.FromResult(new ProcessExecutionResult(
                Started: true,
                ExitCode: 0,
                TimedOut: false,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                StartError: null));
        }
    }
}
