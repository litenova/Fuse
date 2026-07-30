using System.Reflection;
using System.Text.Json;
using Fuse.Cli.Rpc;
using Fuse.Collection.FileSystem;

namespace Fuse.Cli.Services;

/// <summary>
///     Reads MCP registration, managed guidance, daemon, repository, and index-job state without modifying files.
/// </summary>
/// <remarks>
///     The doctor never starts a daemon or an index job. It reports the state already visible from the selected
///     client configuration and repository endpoint, so it is safe to run after installation and during recovery.
/// </remarks>
public sealed class McpDoctorService
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    ///     Diagnoses selected MCP clients at a project or user scope.
    /// </summary>
    /// <param name="clients">The clients to inspect.</param>
    /// <param name="scope">The registration scope to inspect.</param>
    /// <param name="projectDirectory">A path inside the project repository, or null for the current directory.</param>
    /// <param name="includeHooks">Whether to inspect the optional project-scoped Claude hooks.</param>
    /// <param name="cancellationToken">Cancels daemon probes.</param>
    /// <returns>A read-only diagnostic report.</returns>
    public async Task<McpDoctorReport> DiagnoseAsync(
        IReadOnlyList<McpInstallClient> clients,
        McpInstallScope scope,
        string? projectDirectory,
        bool includeHooks,
        CancellationToken cancellationToken)
    {
        var requestedDirectory = string.IsNullOrWhiteSpace(projectDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(projectDirectory);
        string? repositoryRoot = null;
        var repositoryResolved = Directory.Exists(requestedDirectory)
            && WorkspaceIdentityResolver.TryResolveRepositoryRoot(requestedDirectory, out repositoryRoot);
        var root = repositoryResolved ? repositoryRoot! : requestedDirectory;
        var issues = new List<string>();

        if (!repositoryResolved && scope == McpInstallScope.Project)
        {
            issues.Add(
                $"workspace_identity_unresolved: '{requestedDirectory}' is not inside a Git repository. Run 'fuse mcp install' from the target repository.");
        }

        var clientReports = clients
            .Select(client => BuildClientReport(client, scope, root, issues))
            .ToArray();
        var daemon = await ReadDaemonAsync(repositoryResolved ? repositoryRoot! : null, cancellationToken);
        var index = await ReadIndexAsync(repositoryResolved ? repositoryRoot! : null, daemon, cancellationToken);
        var hooks = ReadHooks(repositoryResolved ? repositoryRoot! : null, includeHooks, issues);

        if (daemon.State == "protocol_mismatch")
        {
            issues.Add(
                $"daemon_protocol_mismatch: daemon protocol {daemon.ProtocolVersion} does not match required protocol {FuseHostService.ProtocolVersion}. Run 'fuse index' to replace the stale daemon.");
        }

        return new McpDoctorReport(
            RunningVersion: RunningVersion(),
            RunningExecutable: McpInstallFiles.ResolveFuseCommand(),
            PathExecutable: McpInstallFiles.FindExecutableOnPath("fuse"),
            Scope: scope.ToString().ToLowerInvariant(),
            RepositoryIdentity: repositoryResolved ? "resolved" : "unresolved",
            RepositoryRoot: repositoryResolved ? repositoryRoot : null,
            Clients: clientReports,
            Daemon: daemon,
            Index: index,
            Hooks: hooks,
            RestartRequired: clientReports.Any(report => report.Registration == "registered"),
            Issues: issues.ToArray());
    }

    private static McpDoctorClientReport BuildClientReport(
        McpInstallClient client,
        McpInstallScope scope,
        string projectRoot,
        ICollection<string> issues)
    {
        var configPath = TryGetConfigPath(client, scope, projectRoot);
        var registration = ReadRegistration(client, scope, configPath);
        var commandState = CommandState(registration.Command);
        var instructionPath = McpInstallFiles.GetInstructionPath(client, scope, projectRoot);
        var instructionState = ReadInstructionState(instructionPath);
        var detail = registration.Detail;

        if (registration.State is "missing" or "invalid")
        {
            detail ??= $"Run 'fuse mcp install --client {ClientName(client)} --scope {scope.ToString().ToLowerInvariant()}'.";
            issues.Add($"{ClientName(client)} registration is {registration.State}. {detail}");
        }

        if (commandState == "invalid_command_path")
        {
            detail ??= $"The registered executable does not exist: {registration.Command}. Run 'fuse mcp install --client {ClientName(client)} --command <path>'.";
            issues.Add($"{ClientName(client)} has an invalid command path: {registration.Command}.");
        }

        if (instructionState is "missing" or "outdated")
        {
            var action = instructionState == "outdated"
                ? "update"
                : "write";
            issues.Add(
                $"{ClientName(client)} managed guidance is {instructionState}. Run 'fuse mcp install --client {ClientName(client)}' to {action} it.");
        }

        return new McpDoctorClientReport(
            McpInstallFiles.DescribeClient(client),
            configPath,
            registration.State,
            registration.Command,
            commandState,
            instructionPath,
            instructionState,
            detail);
    }

    private static async Task<McpDoctorDaemonReport> ReadDaemonAsync(string? root, CancellationToken cancellationToken)
    {
        if (root is null)
        {
            return new McpDoctorDaemonReport(
                "unavailable", null, null, null, "Resolve the repository identity before probing the daemon.");
        }

        var handshake = await FuseHostClient.TryHandshakeAsync(root, TimeSpan.FromMilliseconds(500), cancellationToken);
        if (handshake is null)
        {
            return new McpDoctorDaemonReport(
                "unavailable", null, null, null, "No daemon is running. The next index or MCP read starts one.");
        }

        var stats = await FuseHostClient.TryStatsIgnoringProtocolAsync(root, TimeSpan.FromMilliseconds(500), cancellationToken);
        if (handshake.ProtocolVersion != FuseHostService.ProtocolVersion)
        {
            return new McpDoctorDaemonReport(
                "protocol_mismatch",
                handshake.HostVersion,
                handshake.ProtocolVersion,
                stats?.ProcessId,
                $"Required protocol: {FuseHostService.ProtocolVersion}.");
        }

        return new McpDoctorDaemonReport(
            "reachable",
            handshake.HostVersion,
            handshake.ProtocolVersion,
            stats?.ProcessId,
            null);
    }

    private static async Task<McpDoctorIndexReport> ReadIndexAsync(
        string? root,
        McpDoctorDaemonReport daemon,
        CancellationToken cancellationToken)
    {
        if (root is null)
            return new McpDoctorIndexReport("unavailable", null, null, null, "Resolve the repository identity first.");
        if (daemon.State == "unavailable")
            return new McpDoctorIndexReport("unknown", null, null, null, "No daemon is running.");
        if (daemon.State == "protocol_mismatch")
            return new McpDoctorIndexReport("unknown", null, null, null, "The daemon protocol is stale.");

        var snapshot = await FuseHostClient.TryIndexStatusAsync(root, TimeSpan.FromMilliseconds(500), cancellationToken);
        return snapshot is null
            ? new McpDoctorIndexReport("no_active_job", null, null, null, "No active or retained daemon job.")
            : new McpDoctorIndexReport(
                "available",
                snapshot.JobId,
                snapshot.State.ToString(),
                snapshot.Phase.ToString(),
                snapshot.ErrorCode is null ? null : $"{snapshot.ErrorCode}: {snapshot.ErrorMessage}");
    }

    private static McpDoctorHooksReport ReadHooks(string? root, bool requested, ICollection<string> issues)
    {
        if (!requested)
            return new McpDoctorHooksReport(false, "not_requested", null, null);
        if (root is null)
        {
            return new McpDoctorHooksReport(
                true, "unavailable", null, "Resolve the repository identity before checking project-scoped hooks.");
        }

        var settingsPath = Path.Combine(root, ".claude", "settings.json");
        string? content = null;
        try
        {
            content = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new McpDoctorHooksReport(true, "unavailable", settingsPath, ex.Message);
        }

        if (ClaudeHooksConfig.AlreadyInstalled(content))
            return new McpDoctorHooksReport(true, "installed", settingsPath, null);

        const string detail = "Run 'fuse mcp install --with-hooks' to write the project-scoped hooks.";
        issues.Add($"Claude hooks are missing. {detail}");
        return new McpDoctorHooksReport(true, "missing", settingsPath, detail);
    }

    private static (string State, string? Command, string? Detail) ReadRegistration(
        McpInstallClient client,
        McpInstallScope scope,
        string? configPath)
    {
        if (client == McpInstallClient.Claude && scope == McpInstallScope.User)
        {
            return McpInstallFiles.FindExecutableOnPath("claude") is null
                ? ("missing", null, "Claude Code CLI is not on PATH, so user-scope registration cannot be verified.")
                : ("cli_registration_unverified", null, "Claude Code owns its user-scope registration through the claude CLI.");
        }

        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            return ("missing", null, "The selected MCP configuration file is absent.");

        try
        {
            return client is McpInstallClient.Codex or McpInstallClient.Grok
                ? ReadTomlRegistration(configPath)
                : ReadJsonRegistration(client, configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ("invalid", null, $"Could not read the selected MCP configuration: {ex.Message}");
        }
    }

    private static (string State, string? Command, string? Detail) ReadJsonRegistration(
        McpInstallClient client,
        string configPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configPath), JsonOptions);
        var root = document.RootElement;
        JsonElement server = default;

        var found = client switch
        {
            McpInstallClient.Claude or McpInstallClient.Cursor => TryGetServer(root, "mcpServers", out server),
            McpInstallClient.Copilot => TryGetServer(root, "servers", out server),
            McpInstallClient.OpenCode or McpInstallClient.Kilo => TryGetServer(root, "mcp", out server),
            _ => false,
        };
        if (!found)
            return ("missing", null, "The configuration has no Fuse server entry.");

        if (client is McpInstallClient.OpenCode or McpInstallClient.Kilo)
        {
            if (!server.TryGetProperty("command", out var commandArray)
                || commandArray.ValueKind != JsonValueKind.Array
                || commandArray.GetArrayLength() == 0
                || commandArray[0].ValueKind != JsonValueKind.String)
            {
                return ("invalid", null, "The Fuse command array is missing its executable.");
            }

            return ("registered", commandArray[0].GetString(), null);
        }

        if (!server.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String)
            return ("invalid", null, "The Fuse server entry has no command string.");
        return ("registered", command.GetString(), null);
    }

    private static bool TryGetServer(JsonElement root, string containerName, out JsonElement server)
    {
        server = default;
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(containerName, out var container)
               && container.ValueKind == JsonValueKind.Object
               && container.TryGetProperty(McpInstallFiles.ServerName, out server)
               && server.ValueKind == JsonValueKind.Object;
    }

    private static (string State, string? Command, string? Detail) ReadTomlRegistration(string configPath)
    {
        var inFuseTable = false;
        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inFuseTable = line is "[mcp_servers.fuse]" or "[mcp_servers.\"fuse\"]";
                continue;
            }

            if (!inFuseTable || !line.StartsWith("command", StringComparison.Ordinal))
                continue;
            var separator = line.IndexOf('=');
            if (separator < 0)
                return ("invalid", null, "The Fuse TOML server entry has an invalid command line.");
            var value = line[(separator + 1)..].Trim();
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
                return ("invalid", null, "The Fuse TOML command must be a quoted string.");
            return ("registered", value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal), null);
        }

        return ("missing", null, "The configuration has no [mcp_servers.fuse] table.");
    }

    private static string CommandState(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "not_checked";
        if (Path.IsPathFullyQualified(command))
            return File.Exists(command) ? "valid" : "invalid_command_path";
        return McpInstallFiles.FindExecutableOnPath(command) is null ? "missing_from_path" : "valid";
    }

    private static string ReadInstructionState(string? instructionPath)
    {
        if (instructionPath is null)
            return "not_supported";
        if (!File.Exists(instructionPath))
            return "missing";

        try
        {
            var content = File.ReadAllText(instructionPath);
            if (McpInstallFiles.HasCurrentManagedRuleBlock(content))
                return "current";
            return content.Contains("<!-- fuse:begin", StringComparison.Ordinal) ? "outdated" : "missing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private static string? TryGetConfigPath(McpInstallClient client, McpInstallScope scope, string root)
    {
        try
        {
            return McpInstallFiles.GetConfigPath(client, scope, root);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string ClientName(McpInstallClient client) => client switch
    {
        McpInstallClient.Kilo => "kilo",
        McpInstallClient.Grok => "grok",
        _ => client.ToString().ToLowerInvariant(),
    };

    private static string RunningVersion() =>
        typeof(McpDoctorService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(McpDoctorService).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
