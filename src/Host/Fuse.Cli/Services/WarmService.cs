using System.Runtime.InteropServices;
using System.Text.Json;
using Fuse.Cli.Serialization;
using Fuse.Reduction.Caching;

namespace Fuse.Cli.Services;

/// <summary>
///     The opt-in always-on warm service (R40, Layer 3): a background OS service (Windows service, launchd agent,
///     systemd user unit) installed only by an explicit <c>fuse warm --service install</c> - never by
///     <c>fuse mcp install</c> - that keeps a store-backed daemon alive and re-warms a bounded LRU of
///     recently-used repos so they are ready before a session opens. Guardrails, all enforced here: store-backed
///     only (never a resident Roslyn compilation), a hard LRU cap, battery/load awareness (pause under battery or
///     high load), idle-evict, and a clean uninstall. It stays opt-in for the agent-first default (R38/R39 cover
///     the agent case); a promotion to opt-out requires measuring idle RSS/CPU/battery and showing they are light.
/// </summary>
public static class WarmService
{
    /// <summary>The service name registered with the OS.</summary>
    public const string ServiceName = "fuse-warm";
}

/// <summary>
///     A bounded, most-recently-used set of warm repos (R40). The service pre-warms only the last <see cref="Cap" />
///     repos opened this cycle; a repo not opened this cycle is never pre-warmed, so the warm set never grows
///     without bound.
/// </summary>
public sealed class WarmServiceLru
{
    /// <summary>The default hard cap on the number of warm repos.</summary>
    public const int DefaultCap = 5;

    private readonly LinkedList<string> _order = new(); // front = most recent.
    private readonly Dictionary<string, LinkedListNode<string>> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _cap;

    /// <summary>Initializes a new instance of the <see cref="WarmServiceLru" /> class.</summary>
    /// <param name="cap">The hard cap on warm repos (defaults to <see cref="DefaultCap" />).</param>
    public WarmServiceLru(int cap = DefaultCap) => _cap = cap > 0 ? cap : DefaultCap;

    /// <summary>The hard cap on warm repos.</summary>
    public int Cap => _cap;

    /// <summary>The warm repos, most recent first.</summary>
    public IReadOnlyList<string> Repos => _order.ToList();

    /// <summary>
    ///     Records a repo as most-recently-used, evicting the least-recently-used repos beyond the cap.
    /// </summary>
    /// <param name="root">The repo root that was opened.</param>
    /// <returns>The roots evicted by this touch (their pre-warm should stop), most recently evicted last.</returns>
    public IReadOnlyList<string> Touch(string root)
    {
        var key = System.IO.Path.GetFullPath(root);
        if (_index.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            _order.AddFirst(existing);
        }
        else
        {
            _index[key] = _order.AddFirst(key);
        }

        var evicted = new List<string>();
        while (_order.Count > _cap)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _index.Remove(last.Value);
            evicted.Add(last.Value);
        }

        return evicted;
    }
}

/// <summary>
///     The battery/load and idle policy for the warm service (R40): it must not drain a laptop or fight the
///     foreground, so it pauses pre-warming on battery or under high system load, and evicts a repo idle past a
///     window.
/// </summary>
public static class WarmServicePolicy
{
    /// <summary>Whether pre-warming should pause: on battery, or when the system is under high load.</summary>
    /// <param name="onBattery">Whether the machine is on battery power.</param>
    /// <param name="highLoad">Whether the system is under high CPU load.</param>
    /// <returns><see langword="true" /> when pre-warming should pause.</returns>
    public static bool ShouldPause(bool onBattery, bool highLoad) => onBattery || highLoad;

    /// <summary>Whether a repo idle for <paramref name="idle" /> should be evicted from the warm set.</summary>
    /// <param name="idle">How long the repo has been idle.</param>
    /// <param name="idleWindow">The idle-evict window.</param>
    /// <returns><see langword="true" /> when the repo should be evicted.</returns>
    public static bool ShouldEvict(TimeSpan idle, TimeSpan idleWindow) => idle >= idleWindow;
}

/// <summary>
///     Generates the per-platform service definition and the register/unregister commands for the warm service
///     (R40). The generation is pure and testable; the actual OS registration (which needs privileges and mutates
///     the machine) is performed only by the explicit <c>fuse warm --service install</c> command.
/// </summary>
public static class WarmServiceDefinition
{
    /// <summary>
    ///     The install command a user (or the installer) runs to register the warm service for the running
    ///     platform, keeping a warm daemon alive. Includes the first-run notice describing how to disable it.
    /// </summary>
    /// <param name="fuseInvocation">The absolute invocation of the fuse tool (for example the apphost path).</param>
    /// <returns>The platform-native register command text.</returns>
    public static string InstallCommand(string fuseInvocation)
    {
        if (OperatingSystem.IsWindows())
            return $"New-Service -Name {WarmService.ServiceName} -BinaryPathName '\"{fuseInvocation}\" warm --service run' -StartupType Automatic";
        if (OperatingSystem.IsMacOS())
            return $"launchctl load ~/Library/LaunchAgents/{WarmService.ServiceName}.plist";
        return $"systemctl --user enable --now {WarmService.ServiceName}.service";
    }

    /// <summary>The uninstall command that unregisters the warm service for the running platform (clean uninstall).</summary>
    /// <returns>The platform-native unregister command text.</returns>
    public static string UninstallCommand()
    {
        if (OperatingSystem.IsWindows())
            return $"Remove-Service -Name {WarmService.ServiceName}";
        if (OperatingSystem.IsMacOS())
            return $"launchctl unload ~/Library/LaunchAgents/{WarmService.ServiceName}.plist";
        return $"systemctl --user disable --now {WarmService.ServiceName}.service";
    }

    /// <summary>The one-line first-run notice naming how to disable the service, shown on install.</summary>
    /// <returns>The notice text.</returns>
    public static string FirstRunNotice() =>
        $"Fuse warm service installed (opt-in). It keeps recently-used repos warm, store-backed only, battery-aware. " +
        $"Disable any time with: fuse warm --service uninstall.";

    /// <summary>A short description of the running platform's service mechanism, for the install report.</summary>
    /// <returns>The platform mechanism name.</returns>
    public static string PlatformMechanism() =>
        OperatingSystem.IsWindows() ? "Windows service"
        : OperatingSystem.IsMacOS() ? "launchd agent"
        : "systemd user unit";
}

/// <summary>A runnable command (executable plus arguments) the warm-service installer invokes.</summary>
/// <param name="Executable">The executable to run.</param>
/// <param name="Arguments">The argument string.</param>
public sealed record WarmServiceCommand(string Executable, string Arguments);

/// <summary>The outcome of a warm-service install or uninstall attempt (R40).</summary>
/// <param name="Succeeded">Whether the OS registration/unregistration succeeded.</param>
/// <param name="Message">A human-readable result, including the manual command to run on a failed attempt.</param>
public sealed record WarmServiceActionResult(bool Succeeded, string Message);
