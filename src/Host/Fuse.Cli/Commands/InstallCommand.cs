using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Collection.FileSystem;

namespace Fuse.Cli.Commands;

/// <summary>
///     Registers Fuse as an MCP server with supported AI coding clients.
/// </summary>
[CliCommand(
    Name = "install",
    Description = "Write MCP client registration and managed Fuse guidance for launching 'fuse mcp serve'.",
    Parent = typeof(McpCommand))]
public sealed class InstallCommand
{
    private readonly IConsoleUI _consoleUI;
    private readonly McpDoctorService _mcpDoctorService;
    private readonly McpInstallService _mcpInstallService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InstallCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>
    ///     Used by DotMake.CommandLine to bind options; the console UI is <see langword="null" />, so this instance
    ///     must not run.
    /// </remarks>
    public InstallCommand() : this(null!, null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="InstallCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <param name="mcpInstallService">The service that writes MCP client configuration.</param>
    /// <param name="mcpDoctorService">The service that verifies the resulting registration without writing files.</param>
    public InstallCommand(IConsoleUI consoleUI, McpInstallService mcpInstallService, McpDoctorService mcpDoctorService)
    {
        _consoleUI = consoleUI;
        _mcpInstallService = mcpInstallService;
        _mcpDoctorService = mcpDoctorService;
    }

    /// <summary>
    ///     Gets or sets the AI client to configure, or <c>all</c> for every supported client.
    /// </summary>
    [CliOption(Description = "AI client: claude, cursor, copilot, opencode, kilo, codex, grok, or all (default: all).")]
    public string Client { get; set; } = "all";

    /// <summary>
    ///     Gets or sets the registration scope: <c>project</c> (this directory) or <c>user</c> (all projects).
    /// </summary>
    [CliOption(Description = "Config scope: project (the enclosing Git repository) or user (the client's user config). This does not change index scope.")]
    public string Scope { get; set; } = "project";

    /// <summary>
    ///     Gets or sets the Fuse executable the client should launch; defaults to the running binary or <c>fuse</c>.
    /// </summary>
    [CliOption(Required = false, Description = "Executable the client launches (default: the running fuse binary or fuse on PATH).")]
    public string? Command { get; set; }

    /// <summary>
    ///     When set, skips the managed Fuse guidance that is otherwise written to each configured client's
    ///     documented instruction file. At project scope, the default guidance write also appends <c>.fuse/</c>
    ///     to <c>.gitignore</c> when no equivalent entry exists.
    /// </summary>
    [CliOption(Name = "--no-rules", Required = false, Description = "Register MCP only. Do not write managed Fuse instructions to AGENTS.md, CLAUDE.md, or the client's documented equivalent.")]
    public bool NoRules { get; set; }

    /// <summary>
    ///     When set, also writes Fuse's ambient-verification hooks (S3) into the project's Claude Code
    ///     <c>.claude/settings.json</c>: a PostToolUse hook running <c>fuse check --delta --fast</c> after edits and
    ///     a Stop hook running <c>fuse gate</c>. Requires the explicit flag; the merge preserves other settings and
    ///     is idempotent.
    /// </summary>
    [CliOption(Name = "--with-hooks", Required = false, Description = "Also write Claude Code ambient-verification hooks (PostToolUse -> fuse check --delta, Stop -> fuse gate) into project .claude/settings.json.")]
    public bool WithHooks { get; set; }

    /// <summary>
    ///     Writes MCP configuration for the selected client(s) and scope.
    /// </summary>
    /// <param name="context">The CLI invocation context.</param>
    /// <returns>A task that completes when registration finishes.</returns>
    /// <remarks>
    ///     Project scope writes client config files at the nearest enclosing Git root. User scope writes each
    ///     client's documented user config, except Claude Code, which is registered through its CLI. The MCP client
    ///     launches <c>fuse mcp serve</c> as a child process.
    /// </remarks>
    public async Task RunAsync(CliContext context)
    {
        if (!TryParseClient(Client, out var clients))
        {
            _consoleUI.WriteError("Unknown client. Use claude, cursor, copilot, opencode, kilo, codex, grok, or all.");
            return;
        }

        if (!TryParseScope(Scope, out var scope))
        {
            _consoleUI.WriteError("Unknown scope. Use project or user.");
            return;
        }

        if (WithHooks && scope == McpInstallScope.User)
        {
            _consoleUI.WriteError("--with-hooks is project-scoped. Run it with --scope project from inside the target Git repository.");
            return;
        }

        var configured = await _mcpInstallService.InstallAsync(
            clients,
            scope,
            projectDirectory: null,
            fuseCommand: Command,
            writeRules: !NoRules,
            _consoleUI,
            context.CancellationToken);

        if (configured == 0)
        {
            _consoleUI.WriteError("No MCP clients were configured.");
            return;
        }

        _consoleUI.WriteResult(
            $"Registered {configured} MCP client{(configured == 1 ? string.Empty : "s")} at {Scope.ToLowerInvariant()} scope. " +
            "The client can launch 'fuse mcp serve' when MCP is enabled.");
        _consoleUI.WriteStep(
            NoRules
                ? "Registration wrote client configuration only. It did not write managed guidance, install the Fuse binary, start a permanent service, create an index, or install an agent skill."
                : "Registration wrote client configuration and versioned managed guidance. It did not install the Fuse binary, start a permanent service, create an index, or install an agent skill.");
        _consoleUI.WriteStep(
            "Workspace-scoped tools activate only when the requested folder resolves to a Git repository. Nested folders share the repository-root index; fuse_reduce remains available outside Git repositories.");

        if (WithHooks)
            WriteClaudeHooks();

        var doctor = await _mcpDoctorService.DiagnoseAsync(
            clients,
            scope,
            projectDirectory: null,
            includeHooks: WithHooks,
            cancellationToken: context.CancellationToken);
        if (doctor.RestartRequired)
            _consoleUI.WriteStep("MCP doctor: restart the configured client to load its updated registration and guidance.");

        if (NoRules)
            _consoleUI.WriteStep(
                "Managed agent instructions were skipped by --no-rules. MCP server instructions are still advertised when the client connects.");
    }

    // Writes (or idempotently updates) the project's .claude/settings.json with Fuse's ambient-verification hooks.
    // The command the hooks invoke is the explicit --command or the fuse global tool on PATH; the working tree is
    // touched only under the explicit --with-hooks flag.
    private void WriteClaudeHooks()
    {
        if (!WorkspaceIdentityResolver.TryResolveRepositoryRoot(Directory.GetCurrentDirectory(), out var projectRoot))
        {
            _consoleUI.WriteError("Claude hooks require a Git repository identity; no hooks were written.");
            return;
        }
        var settingsPath = System.IO.Path.Combine(projectRoot, ".claude", "settings.json");
        var existing = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        if (ClaudeHooksConfig.AlreadyInstalled(existing))
        {
            _consoleUI.WriteStep($"Ambient-verification hooks already present in {settingsPath}.");
            return;
        }

        var fuseCommand = string.IsNullOrWhiteSpace(Command) ? "fuse" : Command!;
        var merged = ClaudeHooksConfig.Merge(existing, fuseCommand);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, merged);
        _consoleUI.WriteSuccess($"Wrote ambient-verification hooks to {settingsPath} (PostToolUse -> check --delta, Stop -> gate).");
        _consoleUI.WriteStep("Remove them by deleting the two fuse hook entries from that file's \"hooks\" section.");
    }

    internal static bool TryParseClient(string value, out IReadOnlyList<McpInstallClient> clients)
    {
        clients = value.Trim().ToLowerInvariant() switch
        {
            "claude" => [McpInstallClient.Claude],
            "cursor" => [McpInstallClient.Cursor],
            "copilot" => [McpInstallClient.Copilot],
            "opencode" => [McpInstallClient.OpenCode],
            "kilo" or "kilocode" or "kilo-code" => [McpInstallClient.Kilo],
            "codex" => [McpInstallClient.Codex],
            "grok" or "grokbuild" or "grok-build" => [McpInstallClient.Grok],
            "all" =>
            [
                McpInstallClient.Claude,
                McpInstallClient.Cursor,
                McpInstallClient.Copilot,
                McpInstallClient.OpenCode,
                McpInstallClient.Kilo,
                McpInstallClient.Codex,
                McpInstallClient.Grok,
            ],
            _ => [],
        };

        return clients.Count > 0;
    }

    internal static bool TryParseScope(string value, out McpInstallScope scope)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "project":
                scope = McpInstallScope.Project;
                return true;
            case "user":
                scope = McpInstallScope.User;
                return true;
            default:
                scope = default;
                return false;
        }
    }
}
