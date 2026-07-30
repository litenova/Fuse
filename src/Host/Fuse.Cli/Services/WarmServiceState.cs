using System.Runtime.InteropServices;
using System.Text.Json;
using Fuse.Cli.Serialization;
using Fuse.Reduction.Caching;

namespace Fuse.Cli.Services;

/// <summary>
///     Persists the recently-used repos the warm service re-warms (R40), as a bounded LRU under the user-data
///     directory, so the always-on service knows which repos to keep warm across sessions and reboots.
/// </summary>
public static class WarmServiceState
{
    private static string StatePath() =>
        Path.Combine(FuseStorePaths.GetUserDataDirectory(), "warm-service", "recent.json");

    /// <summary>Records a repo as most-recently-used, bounded by the LRU cap. Best-effort.</summary>
    /// <param name="root">The repo root that was warmed.</param>
    public static void Record(string root)
    {
        try
        {
            var lru = new WarmServiceLru();
            foreach (var existing in Recent())
                lru.Touch(existing);
            lru.Touch(Path.GetFullPath(root));

            var path = StatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new WarmServiceRecent(lru.Repos.ToList()), FuseCliJsonContext.Default.WarmServiceRecent);
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Returns the recently-used repos, most recent first (empty when none recorded).</summary>
    /// <returns>The recent repo roots.</returns>
    public static IReadOnlyList<string> Recent()
    {
        try
        {
            var path = StatePath();
            if (!File.Exists(path))
                return [];
            var state = JsonSerializer.Deserialize(File.ReadAllText(path), FuseCliJsonContext.Default.WarmServiceRecent);
            return state?.Roots ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}

/// <summary>The persisted recent-repos LRU for the warm service (R40).</summary>
/// <param name="Roots">The recent repo roots, most recent first.</param>
public sealed record WarmServiceRecent(IReadOnlyList<string> Roots);

/// <summary>
///     One tick of the warm-service loop (R40): warms the recent repos when not paused (battery/load), so the
///     always-on service keeps the hot set ready without draining a laptop. Pure and testable.
/// </summary>
public static class WarmServiceRunner
{
    /// <summary>
    ///     Runs one warm tick. When paused, warms nothing; otherwise warms each repo via <paramref name="warmOne" />.
    /// </summary>
    /// <param name="repos">The repos to keep warm.</param>
    /// <param name="paused">Whether pre-warming is paused (battery/load).</param>
    /// <param name="warmOne">Warms one repo.</param>
    /// <param name="cancellationToken">A token to cancel the tick.</param>
    /// <returns>The number of repos warmed this tick (zero when paused).</returns>
    public static async Task<int> RunOnceAsync(
        IReadOnlyList<string> repos, bool paused, Func<string, CancellationToken, Task> warmOne, CancellationToken cancellationToken)
    {
        if (paused)
            return 0;

        var warmed = 0;
        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await warmOne(repo, cancellationToken);
                warmed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort: one repo failing to warm must not stop the service.
            }
        }

        return warmed;
    }
}

/// <summary>Best-effort power state for the warm service's battery-aware pause (R40).</summary>
public static class PowerState
{
    /// <summary>Whether the machine is currently on battery power (Windows only; false elsewhere/best-effort).</summary>
    /// <returns><see langword="true" /> when on battery.</returns>
    public static bool OnBattery()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            return GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus; // 0 = offline (on battery), 1 = online.
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
