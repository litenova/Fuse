using System.Diagnostics;
using System.Text.Json;
using Fuse.Cli;
using Fuse.Cli.Configuration.McpInstall;
using Fuse.Cli.Mcp;
using Fuse.Cli.Serialization;
using Fuse.Collection.FileSystem;

namespace Fuse.Cli.Services;

/// <summary>
///     Writes MCP client configuration so an AI client can launch <c>fuse mcp serve</c> automatically.
/// </summary>
public sealed class McpInstallService
{
    /// <summary>
    ///     Environment variable overriding the user profile root for MCP install paths. Used by tests to
    ///     redirect user-scope writes away from the real home directory.
    /// </summary>
    internal const string UserProfileOverrideEnvironmentVariable = "FUSE_MCP_INSTALL_HOME";

    internal const string ServerName = "fuse";

    // The client launches `fuse mcp serve` with no environment block. The shared daemon and syntax-first index
    // behavior ship in the binary; compiler analysis starts only from an explicit semantic request.
    private static readonly string[] ServeArguments = ["mcp", "serve"];

    /// <summary>
    ///     Registers Fuse with the requested MCP clients at the given scope.
    /// </summary>
    /// <param name="clients">The clients to configure.</param>
    /// <param name="scope">Project-local files or user-global registration.</param>
    /// <param name="projectDirectory">A path inside the project repository; defaults to the current directory.</param>
    /// <param name="fuseCommand">The executable the client should launch; defaults to the running binary or <c>fuse</c>.</param>
    /// <param name="writeRules">
    ///     When <see langword="true" />, also writes task-routing guidance for the <c>fuse_*</c> tools into each
    ///     configured client's instruction file when that client has a documented file. At project scope, also appends
    ///     <c>.fuse/</c> to <c>.gitignore</c> when no equivalent entry exists.
    /// </param>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <param name="cancellationToken">A token that cancels Claude CLI registration.</param>
    /// <returns>The number of clients configured successfully.</returns>
    public async Task<int> InstallAsync(
        IReadOnlyList<McpInstallClient> clients,
        McpInstallScope scope,
        string? projectDirectory,
        string? fuseCommand,
        bool writeRules,
        IConsoleUI consoleUI,
        CancellationToken cancellationToken)
    {
        if (!TryValidateFuseCommand(fuseCommand, out var command, out var validationError))
        {
            consoleUI.WriteError(validationError!);
            return 0;
        }

        var requestedProjectRoot = string.IsNullOrWhiteSpace(projectDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(projectDirectory);
        var projectRoot = requestedProjectRoot;
        if (scope == McpInstallScope.Project)
        {
            if (!WorkspaceIdentityResolver.TryResolveRepositoryRoot(requestedProjectRoot, out projectRoot))
            {
                consoleUI.WriteError(
                    $"Project-scope MCP registration requires a Git repository identity. "
                    + $"'{Path.GetFullPath(requestedProjectRoot)}' is not inside a Git repository; no files were written.");
                return 0;
            }

            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedProjectRoot)),
                    projectRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                consoleUI.WriteStep($"Resolved project scope to repository root: {projectRoot}");
            }
        }

        var configuredClients = new List<McpInstallClient>(clients.Count);
        foreach (var client in clients)
        {
            var success = client switch
            {
                McpInstallClient.Claude => scope == McpInstallScope.User
                    ? await RegisterClaudeUserAsync(command, consoleUI, cancellationToken)
                    : WriteClaudeProjectConfig(projectRoot, command, consoleUI),
                McpInstallClient.Cursor => WriteCursorConfig(scope, projectRoot, command, consoleUI),
                McpInstallClient.Copilot => WriteCopilotConfig(scope, projectRoot, command, consoleUI),
                McpInstallClient.OpenCode => WriteLocalArrayConfig(client, scope, projectRoot, command, consoleUI),
                McpInstallClient.Kilo => WriteLocalArrayConfig(client, scope, projectRoot, command, consoleUI),
                McpInstallClient.Codex => WriteTomlConfig(client, scope, projectRoot, command, consoleUI),
                McpInstallClient.Grok => WriteTomlConfig(client, scope, projectRoot, command, consoleUI),
                _ => false,
            };

            if (success)
                configuredClients.Add(client);
        }

        if (writeRules)
        {
            foreach (var client in configuredClients)
                WriteClientRule(client, scope, projectRoot, consoleUI);

            if (scope == McpInstallScope.Project && configuredClients.Count > 0)
            {
                GitIgnoreHelper.TryEnsureFuseEntry(
                    projectRoot,
                    consoleUI.WriteStep,
                    consoleUI.WriteStep);
            }
        }

        return configuredClients.Count;
    }

    /// <summary>
    ///     Resolves the Fuse executable path for MCP registration.
    /// </summary>
    /// <returns>The current process path when available; otherwise <c>fuse</c>.</returns>
    internal static string ResolveFuseCommand()
    {
        var processPath = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(processPath) ? "fuse" : processPath;
    }

    /// <summary>
    ///     Validates a caller-supplied executable path for MCP registration.
    /// </summary>
    /// <param name="fuseCommand">The raw <c>--command</c> value, or <see langword="null" /> to resolve the default.</param>
    /// <param name="command">The sanitized executable path or name.</param>
    /// <param name="errorMessage">A user-facing error when validation fails.</param>
    /// <returns><see langword="true" /> when <paramref name="command" /> is safe to register.</returns>
    internal static bool TryValidateFuseCommand(string? fuseCommand, out string command, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(fuseCommand))
        {
            command = ResolveFuseCommand();
            errorMessage = null;
            return true;
        }

        command = fuseCommand.Trim();

        if (command.Any(char.IsControl))
        {
            errorMessage = "Invalid --command: control characters and newlines are not allowed.";
            return false;
        }

        // A single executable path or name; arguments belong in the MCP server's args list, not the command field.
        if (command.IndexOfAny(['|', '&', ';', '`', '$', '<', '>']) >= 0)
        {
            errorMessage = "Invalid --command: shell metacharacters are not allowed. Pass a single executable path.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private static bool WriteClaudeProjectConfig(string projectRoot, string fuseCommand, IConsoleUI consoleUI)
    {
        var path = GetConfigPath(McpInstallClient.Claude, McpInstallScope.Project, projectRoot);
        var config = LoadOrCreateClaude(path);
        config.McpServers ??= new Dictionary<string, ClaudeMcpServer>();
        config.McpServers[ServerName] = CreateClaudeServer(fuseCommand);
        WriteClaudeConfig(path, config);
        consoleUI.WriteSuccess($"Registered Fuse with Claude Code (project): {path}");
        return true;
    }

    private static bool WriteCursorConfig(
        McpInstallScope scope,
        string projectRoot,
        string fuseCommand,
        IConsoleUI consoleUI)
    {
        var path = GetConfigPath(McpInstallClient.Cursor, scope, projectRoot);

        var config = LoadOrCreateCursor(path);
        config.McpServers ??= new Dictionary<string, CursorMcpServer>();
        config.McpServers[ServerName] = CreateCursorServer(fuseCommand);
        WriteCursorConfigFile(path, config);
        consoleUI.WriteSuccess($"Registered Fuse with Cursor ({DescribeScope(scope)}): {path}");
        return true;
    }

    private static bool WriteCopilotConfig(
        McpInstallScope scope,
        string projectRoot,
        string fuseCommand,
        IConsoleUI consoleUI)
    {
        var path = GetConfigPath(McpInstallClient.Copilot, scope, projectRoot);

        var config = LoadOrCreateCopilot(path);
        config.Servers ??= new Dictionary<string, CopilotMcpServer>();
        config.Servers[ServerName] = CreateCopilotServer(fuseCommand);
        WriteCopilotConfigFile(path, config);
        consoleUI.WriteSuccess($"Registered Fuse with GitHub Copilot ({DescribeScope(scope)}): {path}");
        return true;
    }

    private static bool WriteLocalArrayConfig(
        McpInstallClient client,
        McpInstallScope scope,
        string projectRoot,
        string fuseCommand,
        IConsoleUI consoleUI)
    {
        var path = GetConfigPath(client, scope, projectRoot);
        var config = LoadOrCreateLocalArrayConfig(path);
        config.Mcp ??= new Dictionary<string, JsonElement>();
        config.Mcp[ServerName] = JsonSerializer.SerializeToElement(
            new LocalArrayMcpServer
            {
                Command = [fuseCommand, .. ServeArguments],
            },
            FuseCliJsonContext.Default.LocalArrayMcpServer);
        WriteJson(path, JsonSerializer.Serialize(config, FuseCliJsonContext.Default.LocalArrayMcpConfig));
        consoleUI.WriteSuccess(
            $"Registered Fuse with {DescribeClient(client)} ({DescribeScope(scope)}): {path}");
        return true;
    }

    private static bool WriteTomlConfig(
        McpInstallClient client,
        McpInstallScope scope,
        string projectRoot,
        string fuseCommand,
        IConsoleUI consoleUI)
    {
        var path = GetConfigPath(client, scope, projectRoot);
        UpsertTomlServer(path, fuseCommand);
        consoleUI.WriteSuccess(
            $"Registered Fuse with {DescribeClient(client)} ({DescribeScope(scope)}): {path}");
        return true;
    }

    private static async Task<bool> RegisterClaudeUserAsync(
        string fuseCommand,
        IConsoleUI consoleUI,
        CancellationToken cancellationToken)
    {
        var claudePath = FindExecutableOnPath("claude");
        if (claudePath is null)
        {
            consoleUI.WriteError(
                "Claude Code CLI not found on PATH. Install Claude Code, then run: claude mcp add fuse --scope user -- fuse mcp serve");
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = claudePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("mcp");
        startInfo.ArgumentList.Add("add");
        startInfo.ArgumentList.Add(ServerName);
        startInfo.ArgumentList.Add("--scope");
        startInfo.ArgumentList.Add("user");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(fuseCommand);
        foreach (var argument in ServeArguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            consoleUI.WriteError("Failed to start the Claude Code CLI for MCP registration.");
            return false;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode == 0)
        {
            consoleUI.WriteSuccess("Registered Fuse with Claude Code (user scope, all projects).");
            return true;
        }

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        if (detail.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            consoleUI.WriteStep("Fuse is already registered with Claude Code (user scope).");
            return true;
        }

        consoleUI.WriteError(
            string.IsNullOrWhiteSpace(detail)
                ? "Claude Code MCP registration failed. Run: claude mcp add fuse --scope user -- fuse mcp serve"
                : $"Claude Code MCP registration failed: {detail}");
        return false;
    }

    private static ClaudeMcpConfig LoadOrCreateClaude(string path)
    {
        if (!File.Exists(path))
            return new ClaudeMcpConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, FuseCliJsonContext.Default.ClaudeMcpConfig) ?? new ClaudeMcpConfig();
    }

    private static CursorMcpConfig LoadOrCreateCursor(string path)
    {
        if (!File.Exists(path))
            return new CursorMcpConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, FuseCliJsonContext.Default.CursorMcpConfig) ?? new CursorMcpConfig();
    }

    private static CopilotMcpConfig LoadOrCreateCopilot(string path)
    {
        if (!File.Exists(path))
            return new CopilotMcpConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, FuseCliJsonContext.Default.CopilotMcpConfig) ?? new CopilotMcpConfig();
    }

    private static LocalArrayMcpConfig LoadOrCreateLocalArrayConfig(string path)
    {
        if (!File.Exists(path))
            return new LocalArrayMcpConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, FuseCliJsonContext.Default.LocalArrayMcpConfig)
               ?? new LocalArrayMcpConfig();
    }

    private static void WriteClaudeConfig(string path, ClaudeMcpConfig config) =>
        WriteJson(path, JsonSerializer.Serialize(config, FuseCliJsonContext.Default.ClaudeMcpConfig));

    private static void WriteCursorConfigFile(string path, CursorMcpConfig config) =>
        WriteJson(path, JsonSerializer.Serialize(config, FuseCliJsonContext.Default.CursorMcpConfig));

    private static void WriteCopilotConfigFile(string path, CopilotMcpConfig config) =>
        WriteJson(path, JsonSerializer.Serialize(config, FuseCliJsonContext.Default.CopilotMcpConfig));

    private static void WriteJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json + Environment.NewLine);
    }

    private const string TomlBeginMarker = "# fuse:begin (managed by fuse mcp install)";
    private const string TomlEndMarker = "# fuse:end";

    private static void UpsertTomlServer(string path, string fuseCommand)
    {
        var block = string.Join(
            "\n",
            TomlBeginMarker,
            "[mcp_servers.fuse]",
            $"command = \"{EscapeTomlBasicString(fuseCommand)}\"",
            "args = [\"mcp\", \"serve\"]",
            TomlEndMarker);

        var existing = File.Exists(path)
            ? File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal)
            : string.Empty;
        string content;

        var managedBegin = existing.IndexOf(TomlBeginMarker, StringComparison.Ordinal);
        var managedEnd = existing.IndexOf(TomlEndMarker, StringComparison.Ordinal);
        if (managedBegin >= 0 && managedEnd > managedBegin)
        {
            content = existing[..managedBegin] + block + existing[(managedEnd + TomlEndMarker.Length)..];
        }
        else
        {
            var lines = existing.Split('\n').ToList();
            var tableStart = lines.FindIndex(IsFuseTomlRootHeader);
            if (tableStart >= 0)
            {
                var tableEnd = tableStart + 1;
                while (tableEnd < lines.Count)
                {
                    if (IsTomlTableHeader(lines[tableEnd]) && !IsFuseTomlNestedHeader(lines[tableEnd]))
                        break;

                    tableEnd++;
                }

                lines.RemoveRange(tableStart, tableEnd - tableStart);
                lines.Insert(tableStart, block);
                content = string.Join("\n", lines);
            }
            else
            {
                var trimmed = existing.TrimEnd('\n');
                content = trimmed.Length == 0 ? block : trimmed + "\n\n" + block;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.TrimEnd('\n') + "\n");
    }

    private static bool IsFuseTomlRootHeader(string line)
    {
        var header = line.Trim();
        return header is "[mcp_servers.fuse]" or "[mcp_servers.\"fuse\"]";
    }

    private static bool IsFuseTomlNestedHeader(string line)
    {
        var header = line.Trim();
        return header.StartsWith("[mcp_servers.fuse.", StringComparison.Ordinal)
               || header.StartsWith("[mcp_servers.\"fuse\".", StringComparison.Ordinal);
    }

    private static bool IsTomlTableHeader(string line)
    {
        var value = line.Trim();
        return value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal);
    }

    private static string EscapeTomlBasicString(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static ClaudeMcpServer CreateClaudeServer(string fuseCommand) =>
        new()
        {
            Type = "stdio",
            Command = fuseCommand,
            Args = [.. ServeArguments],
        };

    private static CursorMcpServer CreateCursorServer(string fuseCommand) =>
        new()
        {
            Command = fuseCommand,
            Args = [.. ServeArguments],
        };

    private static CopilotMcpServer CreateCopilotServer(string fuseCommand) =>
        new()
        {
            Type = "stdio",
            Command = fuseCommand,
            Args = [.. ServeArguments],
        };

    /// <summary>
    ///     Resolves the MCP config file path for a client and scope.
    /// </summary>
    /// <param name="client">The MCP client.</param>
    /// <param name="scope">Project or user scope.</param>
    /// <param name="projectRoot">The project root for project scope.</param>
    /// <returns>The config file path the installer writes.</returns>
    internal static string GetConfigPath(McpInstallClient client, McpInstallScope scope, string projectRoot)
    {
        if (scope == McpInstallScope.User)
        {
            var userRoot = GetUserProfileDirectory();
            return client switch
            {
                // Claude Code stores user-scope servers in ~/.claude.json, which is owned by the Claude CLI; user
                // scope is registered via `claude mcp add --scope user`, not by writing a file. See RegisterClaudeUserAsync.
                McpInstallClient.Claude => throw new NotSupportedException(
                    "Claude Code user scope is registered through the Claude CLI, not a config file path."),
                McpInstallClient.Cursor => Path.Combine(userRoot, ".cursor", "mcp.json"),
                // VS Code reads user-level MCP config from the profile directory, not ~/.vscode.
                McpInstallClient.Copilot => Path.Combine(GetVsCodeUserConfigDirectory(), "mcp.json"),
                McpInstallClient.OpenCode => GetOpenCodeConfigPath(scope, projectRoot),
                McpInstallClient.Kilo => GetKiloConfigPath(scope, projectRoot),
                McpInstallClient.Codex => Path.Combine(GetClientHomeDirectory("CODEX_HOME", ".codex"), "config.toml"),
                McpInstallClient.Grok => Path.Combine(GetClientHomeDirectory("GROK_HOME", ".grok"), "config.toml"),
                _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
            };
        }

        return client switch
        {
            McpInstallClient.Claude => Path.Combine(projectRoot, ".mcp.json"),
            McpInstallClient.Cursor => Path.Combine(projectRoot, ".cursor", "mcp.json"),
            McpInstallClient.Copilot => Path.Combine(projectRoot, ".vscode", "mcp.json"),
            McpInstallClient.OpenCode => GetOpenCodeConfigPath(scope, projectRoot),
            McpInstallClient.Kilo => GetKiloConfigPath(scope, projectRoot),
            McpInstallClient.Codex => Path.Combine(projectRoot, ".codex", "config.toml"),
            McpInstallClient.Grok => Path.Combine(projectRoot, ".grok", "config.toml"),
            _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
        };
    }

    /// <summary>
    ///     Resolves the instruction file managed for a client and scope.
    /// </summary>
    /// <param name="client">The MCP client.</param>
    /// <param name="scope">The selected installation scope.</param>
    /// <param name="projectRoot">The repository root used for project scope.</param>
    /// <returns>The managed instruction path, or null when the client has no documented instruction file at that scope.</returns>
    internal static string? GetInstructionPath(McpInstallClient client, McpInstallScope scope, string projectRoot) => client switch
    {
        McpInstallClient.Claude => scope == McpInstallScope.User
            ? Path.Combine(GetUserProfileDirectory(), ".claude", "CLAUDE.md")
            : Path.Combine(projectRoot, "CLAUDE.md"),
        McpInstallClient.Cursor => scope == McpInstallScope.Project
            ? Path.Combine(projectRoot, ".cursor", "rules", "fuse.mdc")
            : null,
        McpInstallClient.Copilot => scope == McpInstallScope.Project
            ? Path.Combine(projectRoot, ".github", "copilot-instructions.md")
            : null,
        McpInstallClient.OpenCode => scope == McpInstallScope.User
            ? Path.Combine(GetConfigHomeDirectory(), "opencode", "AGENTS.md")
            : Path.Combine(projectRoot, "AGENTS.md"),
        McpInstallClient.Kilo => scope == McpInstallScope.User
            ? Path.Combine(GetConfigHomeDirectory(), "kilo", "AGENTS.md")
            : Path.Combine(projectRoot, "AGENTS.md"),
        McpInstallClient.Codex => scope == McpInstallScope.User
            ? Path.Combine(GetClientHomeDirectory("CODEX_HOME", ".codex"), "AGENTS.md")
            : Path.Combine(projectRoot, "AGENTS.md"),
        McpInstallClient.Grok => scope == McpInstallScope.Project
            ? Path.Combine(projectRoot, "AGENTS.md")
            : null,
        _ => null,
    };

    /// <summary>Checks whether an instruction file contains the current managed guidance block.</summary>
    /// <param name="content">The instruction file content, or null when the file is absent.</param>
    /// <returns>True when the v4.4 managed block has both markers.</returns>
    internal static bool HasCurrentManagedRuleBlock(string? content) =>
        !string.IsNullOrEmpty(content)
        && content.Contains(RuleBeginMarker, StringComparison.Ordinal)
        && content.Contains(RuleEndMarker, StringComparison.Ordinal);

    private static string DescribeScope(McpInstallScope scope) =>
        scope == McpInstallScope.User ? "user scope, all projects" : "project";

    private static string GetOpenCodeConfigPath(McpInstallScope scope, string projectRoot)
    {
        var directory = scope == McpInstallScope.User
            ? Path.Combine(GetConfigHomeDirectory(), "opencode")
            : projectRoot;
        return FirstExistingPath(
            Path.Combine(directory, "opencode.json"),
            Path.Combine(directory, "opencode.jsonc"));
    }

    private static string GetKiloConfigPath(McpInstallScope scope, string projectRoot)
    {
        if (scope == McpInstallScope.User)
        {
            var directory = Path.Combine(GetConfigHomeDirectory(), "kilo");
            return FirstExistingPath(
                Path.Combine(directory, "kilo.jsonc"),
                Path.Combine(directory, "kilo.json"),
                Path.Combine(directory, "config.json"));
        }

        return FirstExistingPath(
            Path.Combine(projectRoot, ".kilo", "kilo.jsonc"),
            Path.Combine(projectRoot, ".kilo", "kilo.json"),
            Path.Combine(projectRoot, "kilo.jsonc"),
            Path.Combine(projectRoot, "kilo.json"));
    }

    private static string FirstExistingPath(string defaultPath, params string[] alternatives)
    {
        if (File.Exists(defaultPath))
            return defaultPath;

        return alternatives.FirstOrDefault(File.Exists) ?? defaultPath;
    }

    /// <summary>Returns the display name used in client-facing installation diagnostics.</summary>
    /// <param name="client">The supported MCP client.</param>
    /// <returns>The documented client display name.</returns>
    internal static string DescribeClient(McpInstallClient client) => client switch
    {
        McpInstallClient.Claude => "Claude Code",
        McpInstallClient.Cursor => "Cursor",
        McpInstallClient.Copilot => "GitHub Copilot",
        McpInstallClient.OpenCode => "OpenCode",
        McpInstallClient.Kilo => "Kilo Code",
        McpInstallClient.Codex => "Codex",
        McpInstallClient.Grok => "Grok Build",
        _ => client.ToString(),
    };

    // The v4.4 marker is intentionally short and versioned. UpsertMarkedBlock also recognizes the previous
    // managed marker so a new install replaces it without touching user-authored text around the block.
    internal const string RuleBeginMarker = "<!-- fuse:begin v4.4 -->";
    internal const string RuleEndMarker = "<!-- fuse:end -->";
    private const string RuleBeginPrefix = "<!-- fuse:begin";

    /// <summary>
    ///     Writes the Fuse usage rule into the given client's instruction file, scope permitting.
    /// </summary>
    /// <param name="client">The MCP client whose instruction file to update.</param>
    /// <param name="scope">Project scope writes repository files; user scope writes documented global instruction files.</param>
    /// <param name="projectRoot">The project root for project-scoped rule files.</param>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <returns><see langword="true" /> when a rule file was written; <see langword="false" /> when skipped.</returns>
    private static bool WriteClientRule(
        McpInstallClient client,
        McpInstallScope scope,
        string projectRoot,
        IConsoleUI consoleUI)
    {
        switch (client)
        {
            case McpInstallClient.Claude:
                var claudePath = scope == McpInstallScope.User
                    ? Path.Combine(GetUserProfileDirectory(), ".claude", "CLAUDE.md")
                    : Path.Combine(projectRoot, "CLAUDE.md");
                UpsertMarkedBlock(claudePath, FuseAgentGuidance.RuleBody);
                consoleUI.WriteSuccess($"Wrote Fuse rule for Claude Code: {claudePath}");
                return true;

            case McpInstallClient.Cursor:
                if (scope == McpInstallScope.User)
                {
                    consoleUI.WriteStep("Cursor has no user-global rules file; skipped the rule (use project scope for the Cursor rule).");
                    return false;
                }

                var cursorPath = Path.Combine(projectRoot, ".cursor", "rules", "fuse.mdc");
                WriteCursorRuleFile(cursorPath);
                consoleUI.WriteSuccess($"Wrote Fuse rule for Cursor: {cursorPath}");
                return true;

            case McpInstallClient.Copilot:
                if (scope == McpInstallScope.User)
                {
                    consoleUI.WriteStep("GitHub Copilot has no user-global instructions file; skipped the rule (use project scope for the Copilot rule).");
                    return false;
                }

                var copilotPath = Path.Combine(projectRoot, ".github", "copilot-instructions.md");
                UpsertMarkedBlock(copilotPath, FuseAgentGuidance.RuleBody);
                consoleUI.WriteSuccess($"Wrote Fuse rule for GitHub Copilot: {copilotPath}");
                return true;

            case McpInstallClient.OpenCode:
                return WriteAgentsRule(
                    client,
                    scope == McpInstallScope.User
                        ? Path.Combine(GetConfigHomeDirectory(), "opencode", "AGENTS.md")
                        : Path.Combine(projectRoot, "AGENTS.md"),
                    consoleUI);

            case McpInstallClient.Kilo:
                return WriteAgentsRule(
                    client,
                    scope == McpInstallScope.User
                        ? Path.Combine(GetConfigHomeDirectory(), "kilo", "AGENTS.md")
                        : Path.Combine(projectRoot, "AGENTS.md"),
                    consoleUI);

            case McpInstallClient.Codex:
                return WriteAgentsRule(
                    client,
                    scope == McpInstallScope.User
                        ? Path.Combine(GetClientHomeDirectory("CODEX_HOME", ".codex"), "AGENTS.md")
                        : Path.Combine(projectRoot, "AGENTS.md"),
                    consoleUI);

            case McpInstallClient.Grok:
                if (scope == McpInstallScope.User)
                {
                    consoleUI.WriteStep(
                        "Grok Build has no documented user-global AGENTS.md file; skipped the rule. Use project scope for the Grok Build rule.");
                    return false;
                }

                return WriteAgentsRule(client, Path.Combine(projectRoot, "AGENTS.md"), consoleUI);

            default:
                return false;
        }
    }

    private static bool WriteAgentsRule(McpInstallClient client, string path, IConsoleUI consoleUI)
    {
        UpsertMarkedBlock(path, FuseAgentGuidance.RuleBody);
        consoleUI.WriteSuccess($"Wrote Fuse rule for {DescribeClient(client)}: {path}");
        return true;
    }

    /// <summary>
    ///     Inserts or replaces the marker-delimited Fuse rule block in a freeform markdown instruction file,
    ///     preserving all content outside the markers.
    /// </summary>
    /// <param name="path">The instruction file path.</param>
    /// <param name="body">The rule body to wrap between the begin and end markers.</param>
    private static void UpsertMarkedBlock(string path, string body)
    {
        var block = RuleBeginMarker + "\n" + body + "\n" + RuleEndMarker;

        string content;
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            var begin = existing.IndexOf(RuleBeginPrefix, StringComparison.Ordinal);
            var end = begin < 0
                ? -1
                : existing.IndexOf(RuleEndMarker, begin + RuleBeginPrefix.Length, StringComparison.Ordinal);
            if (begin >= 0 && end > begin)
            {
                // Replace the existing managed region in place, leaving surrounding content untouched.
                content = existing[..begin] + block + existing[(end + RuleEndMarker.Length)..];
            }
            else
            {
                // Append after the user's content with a blank-line separator.
                var separator = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : "\n";
                content = existing + separator + "\n" + block + "\n";
            }
        }
        else
        {
            content = block + "\n";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    ///     Writes the Cursor rule as a dedicated, fully managed <c>.mdc</c> file with always-apply frontmatter.
    /// </summary>
    /// <param name="path">The <c>.cursor/rules/fuse.mdc</c> path.</param>
    private static void WriteCursorRuleFile(string path)
    {
        var content = string.Join(
            "\n",
            "---",
            "description: Prefer Fuse MCP tools for codebase context gathering",
            "alwaysApply: true",
            "---",
            "",
            RuleBeginMarker,
            FuseAgentGuidance.RuleBody) + "\n";

        content = content.TrimEnd('\n') + "\n" + RuleEndMarker + "\n";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    ///     Resolves the user profile root for MCP install paths, honouring
    ///     <see cref="UserProfileOverrideEnvironmentVariable" /> when set.
    /// </summary>
    internal static string GetUserProfileDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(UserProfileOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string GetConfigHomeDirectory()
    {
        var profileOverride = Environment.GetEnvironmentVariable(UserProfileOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(profileOverride))
            return Path.Combine(Path.GetFullPath(profileOverride), ".config");

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrWhiteSpace(xdgConfig)
            ? Path.Combine(GetUserProfileDirectory(), ".config")
            : Path.GetFullPath(xdgConfig);
    }

    private static string GetClientHomeDirectory(string environmentVariable, string defaultDirectory)
    {
        var profileOverride = Environment.GetEnvironmentVariable(UserProfileOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(profileOverride))
            return Path.Combine(Path.GetFullPath(profileOverride), defaultDirectory);

        var configuredHome = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(GetUserProfileDirectory(), defaultDirectory)
            : Path.GetFullPath(configuredHome);
    }

    /// <summary>
    ///     Resolves the VS Code user profile directory that holds the user-level <c>mcp.json</c>.
    /// </summary>
    /// <returns>The platform-specific <c>Code/User</c> directory.</returns>
    /// <remarks>
    ///     Windows uses <c>%APPDATA%\Code\User</c>, macOS uses <c>~/Library/Application Support/Code/User</c>, and
    ///     other platforms honour <c>XDG_CONFIG_HOME</c> (defaulting to <c>~/.config</c>) under <c>Code/User</c>.
    ///     When <see cref="UserProfileOverrideEnvironmentVariable" /> is set, paths are derived from that root instead.
    /// </remarks>
    private static string GetVsCodeUserConfigDirectory()
    {
        var profileRoot = GetUserProfileDirectory();
        if (OperatingSystem.IsWindows())
            return Path.Combine(profileRoot, "AppData", "Roaming", "Code", "User");

        if (OperatingSystem.IsMacOS())
            return Path.Combine(profileRoot, "Library", "Application Support", "Code", "User");

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrWhiteSpace(xdgConfig) ? Path.Combine(profileRoot, ".config") : xdgConfig;
        return Path.Combine(configHome, "Code", "User");
    }

    /// <summary>Finds an executable on the current process PATH.</summary>
    /// <param name="name">The executable base name.</param>
    /// <returns>The resolved executable path, or null when PATH does not contain it.</returns>
    internal static string? FindExecutableOnPath(string name)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        var extensions = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("PATHEXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              ?? [".EXE", ".CMD", ".BAT"]
            : [string.Empty];

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
