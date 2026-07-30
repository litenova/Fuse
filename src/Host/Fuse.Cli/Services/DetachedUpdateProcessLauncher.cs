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
            };

        Process.Start(startInfo);
    }
}
