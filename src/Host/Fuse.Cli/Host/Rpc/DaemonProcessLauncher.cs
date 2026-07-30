using System.Diagnostics;

namespace Fuse.Cli.Rpc;

/// <summary>
///     Launches a detached <c>fuse host</c> daemon for a repository root (G5), so <c>mcp serve</c> can spawn the
///     shared daemon on demand and delegate its resident workspace to it. The daemon is started with the resident
///     workspace enabled and an idle-shutdown window, so it holds the warm compilation for every client and stops
///     itself when no client has used it for a while. The single-instance lock in the daemon makes a redundant
///     spawn harmless (the second daemon exits).
/// </summary>
public static class DaemonProcessLauncher
{
    /// <summary>
    ///     Builds the start info to launch a daemon for a root, handling both a published apphost (<c>fuse</c>) and
    ///     a framework-dependent run (<c>dotnet fuse.dll</c>). Pure, so the argument and environment wiring is
    ///     testable without spawning a process.
    /// </summary>
    /// <param name="processPath">The current process executable (<see cref="Environment.ProcessPath" />).</param>
    /// <param name="fuseDllPath">The fuse managed dll path, used when the process is <c>dotnet</c>.</param>
    /// <param name="root">The repository root the daemon should serve.</param>
    /// <param name="idleMinutes">The daemon's idle-shutdown window in minutes.</param>
    /// <returns>The configured start info.</returns>
    public static ProcessStartInfo BuildStartInfo(string processPath, string? fuseDllPath, string root, int idleMinutes)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
        };
        // Index jobs are syntax-first. A daemon starts no compiler state or eager index work until a caller asks.
        psi.Environment["FUSE_RESIDENT"] = "0";
        psi.Environment["FUSE_EAGER_INDEX"] = "0";
        psi.Environment["FUSE_BG_UPGRADE"] = "0";
        psi.Environment["FUSE_DAEMON_IDLE_MINUTES"] = idleMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var runningUnderDotnet = Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        psi.FileName = processPath;
        if (runningUnderDotnet && !string.IsNullOrEmpty(fuseDllPath))
            psi.ArgumentList.Add(fuseDllPath);
        psi.ArgumentList.Add("host");
        psi.ArgumentList.Add("--directory");
        psi.ArgumentList.Add(root);
        return psi;
    }

    /// <summary>
    ///     Spawns a detached daemon for a root (best-effort; the single-instance lock resolves a race). Never
    ///     throws: a failure to spawn is left for the supervisor's probe to observe as "not running".
    /// </summary>
    /// <param name="root">The repository root the daemon should serve.</param>
    /// <param name="idleMinutes">The daemon's idle-shutdown window in minutes.</param>
    public static void Spawn(string root, int idleMinutes)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            return;
        var fuseDll = typeof(DaemonProcessLauncher).Assembly.Location;
        try
        {
            using var process = Process.Start(BuildStartInfo(processPath, fuseDll, root, idleMinutes));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Best-effort spawn; the supervisor's probe reports the daemon as not running and the caller falls back.
        }
    }
}
