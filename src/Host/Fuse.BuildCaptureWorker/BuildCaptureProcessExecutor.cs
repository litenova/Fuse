using System.Diagnostics;
using Fuse.Semantics;

namespace Fuse.BuildCaptureWorker;

/// <summary>
///     Runs the bounded non-incremental build that creates a compiler log, and owns cancellation of only that
///     process tree through the worker's process runner.
/// </summary>
internal sealed class BuildCaptureProcessExecutor
{
    private readonly IProcessRunner _processRunner;

    internal BuildCaptureProcessExecutor(IProcessRunner processRunner) => _processRunner = processRunner;

    // A non-incremental build is required because an up-to-date build has no C# compiler invocation to rehydrate.
    internal async Task<(int ExitCode, bool TimedOut, string? FirstError)> RunAsync(
        string buildTarget, string binlogPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(buildTarget) ?? Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(buildTarget);
        startInfo.ArgumentList.Add("--no-incremental");
        startInfo.ArgumentList.Add($"-bl:{binlogPath}");
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-v:quiet");

        var execution = await _processRunner.RunAsync(startInfo, timeout, cancellationToken);
        var output = string.Concat(execution.StandardOutput, Environment.NewLine, execution.StandardError);
        if (!execution.Started)
            return (-1, false, execution.StartError);
        if (execution.TimedOut)
            return (-1, true, null);

        var match = System.Text.RegularExpressions.Regex.Match(output, @"error\s+([A-Z]{2,}\d{3,})");
        return (execution.ExitCode ?? -1, false, match.Success ? match.Groups[1].Value : null);
    }
}
