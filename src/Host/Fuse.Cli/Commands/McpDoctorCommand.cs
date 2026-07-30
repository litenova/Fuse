using System.Text.Json;
using DotMake.CommandLine;
using Fuse.Cli.Serialization;
using Fuse.Cli.Services;

namespace Fuse.Cli.Commands;

/// <summary>
///     Inspects MCP registration, managed agent guidance, daemon reachability, repository identity, and index state.
/// </summary>
[CliCommand(
    Name = "doctor",
    Description = "Inspect MCP registration, managed guidance, daemon reachability, and index job state.",
    Parent = typeof(McpCommand))]
public sealed class McpDoctorCommand
{
    private readonly IConsoleUI _consoleUI;
    private readonly McpDoctorService _doctor;

    /// <summary>Initializes an instance for CLI option binding only.</summary>
    public McpDoctorCommand() : this(null!, null!)
    {
    }

    /// <summary>Initializes the MCP doctor command.</summary>
    /// <param name="consoleUI">The console output service.</param>
    /// <param name="doctor">The read-only MCP diagnostic service.</param>
    public McpDoctorCommand(IConsoleUI consoleUI, McpDoctorService doctor)
    {
        _consoleUI = consoleUI;
        _doctor = doctor;
    }

    /// <summary>The AI client to inspect, or <c>all</c> for every supported client.</summary>
    [CliOption(Description = "AI client: claude, cursor, copilot, opencode, kilo, codex, grok, or all (default: all).")]
    public string Client { get; set; } = "all";

    /// <summary>The registration scope to inspect.</summary>
    [CliOption(Description = "Config scope: project (the enclosing Git repository) or user (the client's user config).")]
    public string Scope { get; set; } = "project";

    /// <summary>The directory used to resolve a project repository identity.</summary>
    [CliArgument(Description = "Directory to inspect. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Whether to inspect project-scoped Claude ambient-verification hooks.</summary>
    [CliOption(Name = "--with-hooks", Required = false, Description = "Also inspect project-scoped Claude Code ambient-verification hooks.")]
    public bool WithHooks { get; set; }

    /// <summary>Whether to emit one JSON diagnostic object.</summary>
    [CliOption(Name = "--json", Required = false, Description = "Emit one JSON diagnostic object.")]
    public bool Json { get; set; }

    /// <summary>Runs the MCP doctor command.</summary>
    /// <param name="context">The invocation cancellation token.</param>
    /// <returns>A task that completes after the diagnostic report is written.</returns>
    public async Task RunAsync(CliContext context)
    {
        if (!InstallCommand.TryParseClient(Client, out var clients))
        {
            _consoleUI.WriteError("Unknown client. Use claude, cursor, copilot, opencode, kilo, codex, grok, or all.");
            return;
        }

        if (!InstallCommand.TryParseScope(Scope, out var scope))
        {
            _consoleUI.WriteError("Unknown scope. Use project or user.");
            return;
        }

        var report = await _doctor.DiagnoseAsync(clients, scope, Path, WithHooks, context.CancellationToken);
        if (Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(report, FuseCliJsonContext.Default.McpDoctorReport));
            return;
        }

        _consoleUI.WriteResult($"Fuse {report.RunningVersion}: {report.RunningExecutable}");
        _consoleUI.WriteStep($"PATH fuse: {report.PathExecutable ?? "not found"}");
        _consoleUI.WriteStep($"Repository: {report.RepositoryIdentity}{(report.RepositoryRoot is null ? string.Empty : $" ({report.RepositoryRoot})")}");
        _consoleUI.WriteStep(
            $"Daemon: {report.Daemon.State}{(report.Daemon.ProcessId is null ? string.Empty : $" (PID {report.Daemon.ProcessId})")}; "
            + $"protocol: {report.Daemon.ProtocolVersion?.ToString() ?? "none"}.");
        _consoleUI.WriteStep(
            $"Index job: {report.Index.State}{(report.Index.JobId is null ? string.Empty : $" ({report.Index.JobId}, {report.Index.JobState}, {report.Index.Phase})")}.");

        foreach (var client in report.Clients)
        {
            _consoleUI.WriteStep(
                $"{client.Client}: registration {client.Registration}; config {client.ConfigPath ?? "client CLI"}; "
                + $"command {client.ConfiguredCommand ?? "not recorded"} ({client.CommandState}); "
                + $"guidance {client.Instructions}{(client.InstructionPath is null ? string.Empty : $" ({client.InstructionPath})")}.");
        }

        if (report.Hooks.Requested)
            _consoleUI.WriteStep($"Claude hooks: {report.Hooks.State}.");
        foreach (var issue in report.Issues)
            _consoleUI.WriteError(issue);

        if (report.RestartRequired)
            _consoleUI.WriteResult("Restart the configured client to load the MCP registration and managed guidance.");
    }
}
