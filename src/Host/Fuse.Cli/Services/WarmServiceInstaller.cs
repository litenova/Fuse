using System.Runtime.InteropServices;

namespace Fuse.Cli.Services;

/// <summary>
///     Installs and uninstalls the opt-in warm service (R40, option 1): it actually attempts the platform
///     registration (Windows <c>New-Service</c>, launchd <c>launchctl load</c>, systemd <c>systemctl --user
///     enable</c>) and falls back to printing the exact manual command when it cannot (for example no elevation on
///     Windows), rather than only reporting. The command runner is injectable so the attempt-then-fall-back logic
///     is testable without mutating the machine.
/// </summary>
public static class WarmServiceInstaller
{
    /// <summary>
    ///     Attempts to register the warm service, writing the platform unit/plist first where needed, then falling
    ///     back to the manual command on failure.
    /// </summary>
    /// <param name="fuseInvocation">The absolute fuse invocation the service should run.</param>
    /// <param name="runner">The command runner (returns an exit code); defaults to a real process runner.</param>
    /// <returns>The action result.</returns>
    public static WarmServiceActionResult Install(string fuseInvocation, Func<WarmServiceCommand, int>? runner = null)
    {
        runner ??= DefaultRunner;
        try
        {
            WriteUnitFileIfNeeded(fuseInvocation);
            var command = RegisterCommand(fuseInvocation);
            var exit = runner(command);
            if (exit == 0)
                return new WarmServiceActionResult(true, $"warm service installed ({WarmServiceDefinition.PlatformMechanism()}). {WarmServiceDefinition.FirstRunNotice()}");
            return Fallback(exit);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Fallback(reason: ex.Message);
        }
    }

    /// <summary>Attempts to unregister the warm service, falling back to the manual command on failure.</summary>
    /// <param name="runner">The command runner; defaults to a real process runner.</param>
    /// <returns>The action result.</returns>
    public static WarmServiceActionResult Uninstall(Func<WarmServiceCommand, int>? runner = null)
    {
        runner ??= DefaultRunner;
        try
        {
            var exit = runner(UnregisterCommand());
            if (exit == 0)
                return new WarmServiceActionResult(true, $"warm service uninstalled ({WarmServiceDefinition.PlatformMechanism()}).");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new WarmServiceActionResult(false, $"could not uninstall automatically ({ex.Message}); run manually: {WarmServiceDefinition.UninstallCommand()}");
        }

        return new WarmServiceActionResult(false, $"could not uninstall automatically; run manually: {WarmServiceDefinition.UninstallCommand()}");
    }

    private static WarmServiceActionResult Fallback(int? exit = null, string? reason = null)
    {
        var why = reason ?? (exit is not null ? $"exit {exit}" : "unknown");
        return new WarmServiceActionResult(
            false,
            $"could not register the warm service automatically ({why}; this often needs elevation). Run manually: {WarmServiceDefinition.InstallCommand("fuse")}");
    }

    // The runnable register command for the running platform. On Windows New-Service is self-contained; on unix the
    // unit/plist is written by WriteUnitFileIfNeeded and this enables/loads it.
    private static WarmServiceCommand RegisterCommand(string fuseInvocation)
    {
        if (OperatingSystem.IsWindows())
            return new WarmServiceCommand("powershell",
                $"-NoProfile -Command \"New-Service -Name {WarmService.ServiceName} -BinaryPathName '\\\"{fuseInvocation}\\\" warm --service run' -StartupType Automatic\"");
        if (OperatingSystem.IsMacOS())
            return new WarmServiceCommand("launchctl", $"load {PlistPath()}");
        return new WarmServiceCommand("systemctl", $"--user enable --now {WarmService.ServiceName}.service");
    }

    private static WarmServiceCommand UnregisterCommand()
    {
        if (OperatingSystem.IsWindows())
            return new WarmServiceCommand("powershell", $"-NoProfile -Command \"Remove-Service -Name {WarmService.ServiceName}\"");
        if (OperatingSystem.IsMacOS())
            return new WarmServiceCommand("launchctl", $"unload {PlistPath()}");
        return new WarmServiceCommand("systemctl", $"--user disable --now {WarmService.ServiceName}.service");
    }

    // Unix services need a unit/plist file on disk before enable/load; Windows New-Service does not.
    private static void WriteUnitFileIfNeeded(string fuseInvocation)
    {
        if (OperatingSystem.IsWindows())
            return;

        if (OperatingSystem.IsMacOS())
        {
            var plist = PlistPath();
            Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
            File.WriteAllText(plist,
                $"<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>Label</key><string>{WarmService.ServiceName}</string>" +
                $"<key>ProgramArguments</key><array><string>{fuseInvocation}</string><string>warm</string><string>--service</string><string>run</string></array>" +
                "<key>RunAtLoad</key><true/></dict></plist>");
            return;
        }

        var unit = SystemdUnitPath();
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        File.WriteAllText(unit,
            $"[Unit]\nDescription=Fuse warm service\n[Service]\nExecStart={fuseInvocation} warm --service run\nRestart=on-failure\n[Install]\nWantedBy=default.target\n");
    }

    private static string PlistPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", WarmService.ServiceName + ".plist");

    private static string SystemdUnitPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user", WarmService.ServiceName + ".service");

    private static int DefaultRunner(WarmServiceCommand command)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(command.Executable, command.Arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
                return -1;
            process.WaitForExit(TimeSpan.FromSeconds(30));
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }
}
