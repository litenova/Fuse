using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;

namespace Fuse.Cli.Commands;

/// <summary>
///     Updates the Fuse global tool, owning the process-lock choreography a bare <c>dotnet tool update</c>
///     cannot: it stops the other running Fuse hosts, then hands the update to a detached script that waits
///     for this process to exit before replacing the tool files.
/// </summary>
/// <remarks>
///     A running .NET global tool locks its own files (on Windows), so it cannot update itself in-process.
///     This command stops the long-lived Fuse hosts (for example the one an editor extension spawns) that
///     would hold the lock, writes a small platform-native updater via <see cref="ToolUpdatePlanner" />, and
///     launches it detached so the update runs once this short-lived CLI process exits. Reload the editor
///     window afterward so its MCP client re-handshakes with the new tool surface.
/// </remarks>
[CliCommand(
    Name = "update",
    Description = "Update the Fuse global tool (stops running hosts, then updates once this process exits).",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class UpdateCommand
{
    private readonly IConsoleUI _consoleUI;
    private readonly ToolUpdateLauncher _updateLauncher;

    /// <summary>
    ///     Initializes a new instance of the <see cref="UpdateCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the console UI is null, so this instance must not run.</remarks>
    public UpdateCommand() : this(null!, new ToolUpdateLauncher())
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="UpdateCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <param name="updateLauncher">The application-owned detached update launcher.</param>
    public UpdateCommand(IConsoleUI consoleUI, ToolUpdateLauncher updateLauncher)
    {
        _consoleUI = consoleUI;
        _updateLauncher = updateLauncher;
    }

    /// <summary>The exact version to install. Defaults to the latest stable on NuGet.</summary>
    /// <remarks>Named <c>to-version</c> rather than <c>version</c> so it does not shadow the global <c>--version</c>.</remarks>
    [CliOption(Name = "to-version", Required = false, Description = "The exact version to install (default: latest stable).")]
    public string? TargetVersion { get; set; }

    /// <summary>
    ///     When set, stops every Fuse peer on the machine before updating (pre-4.2 breadth).
    /// </summary>
    [CliOption(Name = "force-kill-peers", Required = false, Description = "Stop every Fuse peer on the machine before updating.")]
    public bool ForceKillPeers { get; set; }

    /// <summary>
    ///     Runs the update command: stops other Fuse hosts and launches the detached updater.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes once the updater has been launched.</returns>
    public Task RunAsync(CliContext context)
    {
        _consoleUI.WriteStep($"Current Fuse version: {FuseBuildInfo.Current}");

        // Explicit update: stop the other running hosts so they release their file locks, then hand off to the
        // detached updater that waits for this process to exit before replacing the tool files.
        var result = _updateLauncher.Launch(
            TargetVersion,
            stopOtherHosts: true,
            forceKillPeers: ForceKillPeers,
            onHostStopped: _consoleUI.WriteStep);
        if (!result.Launched)
        {
            // Falling back to a manual instruction is better than a stack trace: the user can still update by hand.
            _consoleUI.WriteError($"Could not launch the updater ({result.Error}). Run 'dotnet {result.DotnetArguments}' from a plain terminal with no Fuse process running.");
            return Task.CompletedTask;
        }

        _consoleUI.WriteSuccess("Update launched. It runs once this process exits.");
        _consoleUI.WriteStep($"Command: dotnet {result.DotnetArguments}");
        _consoleUI.WriteStep($"Log: {result.LogPath}");
        _consoleUI.WriteStep("Reload your editor window afterward so its MCP client picks up the new tool surface.");
        return Task.CompletedTask;
    }
}
