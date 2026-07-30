using System.Diagnostics;

namespace Fuse.Cli.Services;

/// <summary>
///     Starts the platform-specific detached process that applies a prepared Fuse tool update after the current
///     command exits.
/// </summary>
internal interface IDetachedUpdateProcessLauncher
{
    void Launch(string scriptPath, bool isWindows);
}

/// <summary>
///     Starts the generated update script without coupling update planning or peer discovery to process launch.
/// </summary>
internal sealed class DetachedUpdateProcessLauncher : IDetachedUpdateProcessLauncher
{
    /// <inheritdoc />
    /// <remarks>
    ///     The updater outlives the command that starts it, so it must not hold the caller's standard streams: an
    ///     inherited stdout keeps a piped or redirected <c>fuse update</c> open until the update finishes. Windows
    ///     uses <c>ShellExecuteEx</c>, which does not inherit handles; the POSIX path redirects instead, so the
    ///     child receives its own pipes.
    /// </remarks>
    public void Launch(string scriptPath, bool isWindows)
    {
        var startInfo = isWindows
            ? new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            }
            : new ProcessStartInfo("/bin/sh", $"\"{scriptPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

        using var process = Process.Start(startInfo);
        if (!isWindows)
            process?.StandardInput.Close();
    }
}
