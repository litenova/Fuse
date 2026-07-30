using System.Diagnostics;

namespace Fuse.Semantics;

/// <summary>
///     Starts a bounded child process owned by the calling Fuse operation. Cancellation and timeout both terminate
///     only that process tree, so a cancelled repository job cannot leave its compiler worker running.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    ///     Runs a process with redirected output until it exits, the timeout elapses, or the caller cancels.
    /// </summary>
    /// <param name="startInfo">The fully bounded process command and arguments.</param>
    /// <param name="timeout">The maximum process lifetime.</param>
    /// <param name="cancellationToken">Cancels the owned process tree and throws cancellation to the caller.</param>
    /// <returns>The captured process outcome.</returns>
    Task<ProcessExecutionResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
///     The captured outcome of one owned external process execution.
/// </summary>
/// <param name="Started">Whether the process started successfully.</param>
/// <param name="ExitCode">The process exit code when it exited.</param>
/// <param name="TimedOut">Whether the runner stopped the process after its timeout elapsed.</param>
/// <param name="StandardOutput">The captured standard output.</param>
/// <param name="StandardError">The captured standard error.</param>
/// <param name="StartError">The direct error when the process could not start.</param>
public sealed record ProcessExecutionResult(
    bool Started,
    int? ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError,
    string? StartError);

/// <summary>
///     Default <see cref="IProcessRunner" /> implementation for processes Fuse owns. It drains both redirected
///     streams while the process runs and kills the process tree on timeout or caller cancellation.
/// </summary>
public sealed class OwnedProcessRunner : IProcessRunner
{
    private readonly Action<int>? _processStarted;

    /// <summary>Initializes a process runner for production use.</summary>
    public OwnedProcessRunner()
    {
    }

    internal OwnedProcessRunner(Action<int>? processStarted) => _processStarted = processStarted;

    /// <inheritdoc />
    public async Task<ProcessExecutionResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The process timeout must be positive.");
        if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
        {
            throw new ArgumentException(
                "Owned process execution requires redirected standard output and standard error.",
                nameof(startInfo));
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcessExecutionResult(false, null, false, string.Empty, string.Empty, ex.Message);
        }

        _processStarted?.Invoke(process.Id);
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            KillOwnedProcessTree(process);
            await DrainAsync(standardOutput, standardError);
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessExecutionResult(
                true,
                process.HasExited ? process.ExitCode : null,
                true,
                CompletedOutput(standardOutput),
                CompletedOutput(standardError),
                null);
        }

        await Task.WhenAll(standardOutput, standardError);
        return new ProcessExecutionResult(
            true,
            process.ExitCode,
            false,
            await standardOutput,
            await standardError,
            null);
    }

    private static void KillOwnedProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited while cancellation was being handled.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The operating system already released the process handle.
        }
    }

    private static async Task DrainAsync(Task<string> standardOutput, Task<string> standardError)
    {
        try
        {
            await Task.WhenAll(standardOutput, standardError).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // The caller cancellation is rethrown after the owned process has been stopped.
        }
        catch (TimeoutException)
        {
            // Disposal closes the remaining redirected streams after the bounded drain wait.
        }
    }

    private static string CompletedOutput(Task<string> output) =>
        output.Status == TaskStatus.RanToCompletion ? output.Result : string.Empty;
}
