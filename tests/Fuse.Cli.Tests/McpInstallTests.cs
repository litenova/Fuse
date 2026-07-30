using System.Text.Json;
using Fuse.Cli.Commands;
using Fuse.Cli.Configuration.McpInstall;
using Fuse.Cli.Mcp;
using Fuse.Cli.Serialization;
using Fuse.Cli.Services;

namespace Fuse.Cli.Tests;

// Shares a collection with IndexCommandParseTests: both build the DotMake command tree via Cli.Parse<FuseCliCommand>,
// and concurrent builds race on DotMake's process-global command registry ("diagnostics conflicts"). Serialize them.
[Collection("DotMakeCliParse")]
public sealed class McpInstallTests
{
    [Fact]
    public async Task InstallAsync_ProjectScope_WritesClaudeCursorAndCopilotConfigs()
    {
        var root = CreateTempDirectory();
        var console = new RecordingConsoleUI();
        var service = new McpInstallService();

        var configured = await service.InstallAsync(
            [McpInstallClient.Claude, McpInstallClient.Cursor, McpInstallClient.Copilot],
            McpInstallScope.Project,
            root,
            "/usr/local/bin/fuse",
            writeRules: false,
            console,
            CancellationToken.None);

        Assert.Equal(3, configured);
        Assert.True(File.Exists(Path.Combine(root, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(root, ".cursor", "mcp.json")));
        Assert.True(File.Exists(Path.Combine(root, ".vscode", "mcp.json")));

        var claude = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(Path.Combine(root, ".mcp.json")),
            FuseCliJsonContext.Default.ClaudeMcpConfig);
        Assert.NotNull(claude);
        Assert.Equal("/usr/local/bin/fuse", claude!.McpServers["fuse"].Command);
        Assert.Equal(["mcp", "serve"], claude.McpServers["fuse"].Args);
        Assert.Equal("stdio", claude.McpServers["fuse"].Type);

        var cursor = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(Path.Combine(root, ".cursor", "mcp.json")),
            FuseCliJsonContext.Default.CursorMcpConfig);
        Assert.NotNull(cursor);
        Assert.Equal("/usr/local/bin/fuse", cursor!.McpServers["fuse"].Command);

        var copilot = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(Path.Combine(root, ".vscode", "mcp.json")),
            FuseCliJsonContext.Default.CopilotMcpConfig);
        Assert.NotNull(copilot);
        Assert.Equal("/usr/local/bin/fuse", copilot!.Servers["fuse"].Command);
        Assert.Equal("stdio", copilot.Servers["fuse"].Type);
    }

    [Fact]
    public async Task InstallAsync_ProjectScope_WritesOpenCodeKiloCodexAndGrokConfigs()
    {
        var root = CreateTempDirectory();
        var service = new McpInstallService();

        var configured = await service.InstallAsync(
            [McpInstallClient.OpenCode, McpInstallClient.Kilo, McpInstallClient.Codex, McpInstallClient.Grok],
            McpInstallScope.Project,
            root,
            "C:\\Tools\\fuse.exe",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        Assert.Equal(4, configured);

        foreach (var client in new[] { McpInstallClient.OpenCode, McpInstallClient.Kilo })
        {
            var path = McpInstallService.GetConfigPath(client, McpInstallScope.Project, root);
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var fuse = config.RootElement.GetProperty("mcp").GetProperty("fuse");
            Assert.Equal("local", fuse.GetProperty("type").GetString());
            Assert.True(fuse.GetProperty("enabled").GetBoolean());
            Assert.Equal(
                ["C:\\Tools\\fuse.exe", "mcp", "serve"],
                fuse.GetProperty("command").EnumerateArray().Select(item => item.GetString()!).ToArray());
        }

        foreach (var client in new[] { McpInstallClient.Codex, McpInstallClient.Grok })
        {
            var path = McpInstallService.GetConfigPath(client, McpInstallScope.Project, root);
            var config = await File.ReadAllTextAsync(path);
            Assert.Contains("[mcp_servers.fuse]", config);
            Assert.Contains("command = \"C:\\\\Tools\\\\fuse.exe\"", config);
            Assert.Contains("args = [\"mcp\", \"serve\"]", config);
        }
    }

    [Fact]
    public async Task InstallAsync_MergesExistingServers()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, ".cursor"));
        await File.WriteAllTextAsync(
            Path.Combine(root, ".cursor", "mcp.json"),
            """
            {
              "mcpServers": {
                "other": {
                  "command": "other-tool",
                  "args": ["run"]
                }
              }
            }
            """);

        var service = new McpInstallService();
        var configured = await service.InstallAsync(
            [McpInstallClient.Cursor],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        Assert.Equal(1, configured);

        var cursor = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(Path.Combine(root, ".cursor", "mcp.json")),
            FuseCliJsonContext.Default.CursorMcpConfig);
        Assert.NotNull(cursor);
        Assert.Equal(2, cursor!.McpServers.Count);
        Assert.Equal("other-tool", cursor.McpServers["other"].Command);
        Assert.Equal("fuse", cursor.McpServers["fuse"].Command);
    }

    [Fact]
    public void GetConfigPath_UserScope_UsesUserProfileDirectory()
    {
        using var home = new TempHomeScope();
        var projectRoot = CreateTempDirectory();

        Assert.Equal(
            Path.Combine(home.Root, ".cursor", "mcp.json"),
            McpInstallService.GetConfigPath(McpInstallClient.Cursor, McpInstallScope.User, projectRoot));

        // VS Code reads user-level MCP config from its profile directory (Code/User), not ~/.vscode.
        var copilotUser = McpInstallService.GetConfigPath(McpInstallClient.Copilot, McpInstallScope.User, projectRoot);
        Assert.EndsWith(Path.Combine("Code", "User", "mcp.json"), copilotUser);
        Assert.DoesNotContain(Path.Combine(".vscode", "mcp.json"), copilotUser);
        Assert.StartsWith(home.Root, copilotUser, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            Path.Combine(home.Root, ".config", "opencode", "opencode.json"),
            McpInstallService.GetConfigPath(McpInstallClient.OpenCode, McpInstallScope.User, projectRoot));
        Assert.Equal(
            Path.Combine(home.Root, ".config", "kilo", "kilo.jsonc"),
            McpInstallService.GetConfigPath(McpInstallClient.Kilo, McpInstallScope.User, projectRoot));
        Assert.Equal(
            Path.Combine(home.Root, ".codex", "config.toml"),
            McpInstallService.GetConfigPath(McpInstallClient.Codex, McpInstallScope.User, projectRoot));
        Assert.Equal(
            Path.Combine(home.Root, ".grok", "config.toml"),
            McpInstallService.GetConfigPath(McpInstallClient.Grok, McpInstallScope.User, projectRoot));
    }

    [Fact]
    public void GetConfigPath_ClaudeUserScope_IsNotSupported()
    {
        // Claude user scope goes through the Claude CLI, so there is no file path to return.
        var exception = Assert.Throws<NotSupportedException>(() =>
            McpInstallService.GetConfigPath(McpInstallClient.Claude, McpInstallScope.User, CreateTempDirectory()));
        Assert.Contains("Claude CLI", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_UserScope_WritesCursorAndCopilotConfigs()
    {
        using var home = new TempHomeScope();
        var projectRoot = CreateTempDirectory();
        var service = new McpInstallService();

        var configured = await service.InstallAsync(
            [McpInstallClient.Cursor, McpInstallClient.Copilot],
            McpInstallScope.User,
            projectRoot,
            "/usr/local/bin/fuse",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        Assert.Equal(2, configured);

        var cursorPath = McpInstallService.GetConfigPath(McpInstallClient.Cursor, McpInstallScope.User, projectRoot);
        Assert.True(File.Exists(cursorPath));
        var cursor = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(cursorPath),
            FuseCliJsonContext.Default.CursorMcpConfig);
        Assert.NotNull(cursor);
        Assert.Equal("/usr/local/bin/fuse", cursor!.McpServers["fuse"].Command);
        Assert.Equal(["mcp", "serve"], cursor.McpServers["fuse"].Args);

        var copilotPath = McpInstallService.GetConfigPath(McpInstallClient.Copilot, McpInstallScope.User, projectRoot);
        Assert.True(File.Exists(copilotPath));
        var copilot = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(copilotPath),
            FuseCliJsonContext.Default.CopilotMcpConfig);
        Assert.NotNull(copilot);
        Assert.Equal("/usr/local/bin/fuse", copilot!.Servers["fuse"].Command);
        Assert.Equal("stdio", copilot.Servers["fuse"].Type);
        Assert.Equal(["mcp", "serve"], copilot.Servers["fuse"].Args);

        // User-scope writes stay under the redirected home, not the project directory.
        Assert.False(File.Exists(Path.Combine(projectRoot, ".cursor", "mcp.json")));
        Assert.False(File.Exists(Path.Combine(projectRoot, ".vscode", "mcp.json")));
    }

    [Fact]
    public async Task InstallAsync_UserScope_WritesNewClientConfigs()
    {
        using var home = new TempHomeScope();
        var projectRoot = CreateTempDirectory();
        var service = new McpInstallService();

        var configured = await service.InstallAsync(
            [McpInstallClient.OpenCode, McpInstallClient.Kilo, McpInstallClient.Codex, McpInstallClient.Grok],
            McpInstallScope.User,
            projectRoot,
            "fuse",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        Assert.Equal(4, configured);
        Assert.All(
            new[] { McpInstallClient.OpenCode, McpInstallClient.Kilo, McpInstallClient.Codex, McpInstallClient.Grok },
            client => Assert.True(File.Exists(
                McpInstallService.GetConfigPath(client, McpInstallScope.User, projectRoot))));
        Assert.False(File.Exists(Path.Combine(projectRoot, "opencode.json")));
        Assert.False(File.Exists(Path.Combine(projectRoot, ".codex", "config.toml")));
    }

    [Theory]
    [InlineData(McpInstallClient.Cursor)]
    [InlineData(McpInstallClient.Copilot)]
    public async Task InstallAsync_UserScope_MergesExistingServers(McpInstallClient client)
    {
        using var home = new TempHomeScope();
        var projectRoot = CreateTempDirectory();
        var configPath = McpInstallService.GetConfigPath(client, McpInstallScope.User, projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        if (client == McpInstallClient.Cursor)
        {
            await File.WriteAllTextAsync(
                configPath,
                """
                {
                  "mcpServers": {
                    "other": {
                      "command": "other-tool",
                      "args": ["run"]
                    }
                  }
                }
                """);
        }
        else
        {
            await File.WriteAllTextAsync(
                configPath,
                """
                {
                  "servers": {
                    "other": {
                      "type": "stdio",
                      "command": "other-tool",
                      "args": ["run"]
                    }
                  }
                }
                """);
        }

        var service = new McpInstallService();
        var configured = await service.InstallAsync(
            [client],
            McpInstallScope.User,
            projectRoot,
            "fuse",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        Assert.Equal(1, configured);

        if (client == McpInstallClient.Cursor)
        {
            var cursor = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(configPath),
                FuseCliJsonContext.Default.CursorMcpConfig);
            Assert.NotNull(cursor);
            Assert.Equal(2, cursor!.McpServers.Count);
            Assert.Equal("other-tool", cursor.McpServers["other"].Command);
            Assert.Equal("fuse", cursor.McpServers["fuse"].Command);
        }
        else
        {
            var copilot = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(configPath),
                FuseCliJsonContext.Default.CopilotMcpConfig);
            Assert.NotNull(copilot);
            Assert.Equal(2, copilot!.Servers.Count);
            Assert.Equal("other-tool", copilot.Servers["other"].Command);
            Assert.Equal("fuse", copilot.Servers["fuse"].Command);
        }
    }

    [Fact]
    public async Task InstallAsync_UserScope_Claude_RejectsWithoutCli()
    {
        using var home = new TempHomeScope();
        using var path = new EmptyPathScope();
        var projectRoot = CreateTempDirectory();
        var console = new RecordingConsoleUI();
        var service = new McpInstallService();

        var configured = await service.InstallAsync(
            [McpInstallClient.Claude],
            McpInstallScope.User,
            projectRoot,
            "fuse",
            writeRules: true,
            console,
            CancellationToken.None);

        Assert.Equal(0, configured);
        Assert.Contains("Claude Code CLI not found on PATH", Assert.Single(console.Errors));
        Assert.False(File.Exists(Path.Combine(projectRoot, ".mcp.json")));
        Assert.False(File.Exists(Path.Combine(home.Root, ".claude.json")));
        Assert.False(File.Exists(Path.Combine(home.Root, ".claude", "CLAUDE.md")));
    }

    [Theory]
    [InlineData("fuse;rm")]
    [InlineData("fuse|cat")]
    [InlineData("fuse&whoami")]
    [InlineData("fuse`id`")]
    [InlineData("fuse\nserve")]
    [InlineData("fuse\0serve")]
    public void TryValidateFuseCommand_RejectsUnsafeCommands(string command)
    {
        var valid = McpInstallService.TryValidateFuseCommand(command, out _, out var errorMessage);

        Assert.False(valid);
        Assert.False(string.IsNullOrWhiteSpace(errorMessage));
    }

    [Theory]
    [InlineData("/usr/local/bin/fuse")]
    [InlineData("fuse")]
    [InlineData(null)]
    public void TryValidateFuseCommand_AcceptsSafeCommands(string? command)
    {
        var valid = McpInstallService.TryValidateFuseCommand(command, out var resolved, out var errorMessage);

        Assert.True(valid);
        Assert.Null(errorMessage);
        Assert.False(string.IsNullOrWhiteSpace(resolved));
    }

    [Fact]
    public async Task InstallAsync_InvalidCommand_ReturnsZero()
    {
        var root = CreateTempDirectory();
        var console = new RecordingConsoleUI();
        var service = new McpInstallService();

        var configured = await service.InstallAsync(
            [McpInstallClient.Cursor],
            McpInstallScope.Project,
            root,
            "fuse;rm",
            writeRules: false,
            console,
            CancellationToken.None);

        Assert.Equal(0, configured);
        Assert.Contains("shell metacharacters", Assert.Single(console.Errors), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(root, ".cursor", "mcp.json")));
    }

    [Fact]
    public async Task InstallAsync_PreservesUnmodeledFieldsOfOtherServers()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, ".vscode"));
        await File.WriteAllTextAsync(
            Path.Combine(root, ".vscode", "mcp.json"),
            """
            {
              "inputs": [
                { "id": "token", "type": "promptString" }
              ],
              "servers": {
                "other": {
                  "type": "stdio",
                  "command": "other-tool",
                  "args": ["run"],
                  "env": { "API_KEY": "${input:token}" }
                }
              }
            }
            """);

        var service = new McpInstallService();
        var configured = await service.InstallAsync(
            [McpInstallClient.Copilot],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        Assert.Equal(1, configured);

        using var written = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(root, ".vscode", "mcp.json")));
        var rootElement = written.RootElement;

        // Fuse was added.
        Assert.True(rootElement.GetProperty("servers").TryGetProperty("fuse", out _));

        // The unrelated server keeps its env block...
        var other = rootElement.GetProperty("servers").GetProperty("other");
        Assert.Equal("${input:token}", other.GetProperty("env").GetProperty("API_KEY").GetString());

        // ...and the top-level inputs array survives.
        Assert.Equal(1, rootElement.GetProperty("inputs").GetArrayLength());
        Assert.Equal("token", rootElement.GetProperty("inputs")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData(McpInstallClient.OpenCode)]
    [InlineData(McpInstallClient.Kilo)]
    public async Task InstallAsync_LocalArrayConfig_PreservesRemoteServersAndTopLevelFields(McpInstallClient client)
    {
        var root = CreateTempDirectory();
        var path = McpInstallService.GetConfigPath(client, McpInstallScope.Project, root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """
            {
              "$schema": "https://example.test/schema.json",
              "mcp": {
                "remote": {
                  "type": "remote",
                  "url": "https://example.test/mcp",
                  "enabled": false
                }
              }
            }
            """);

        var configured = await new McpInstallService().InstallAsync(
            [client],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        Assert.Equal(1, configured);
        using var written = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("https://example.test/schema.json", written.RootElement.GetProperty("$schema").GetString());
        var remote = written.RootElement.GetProperty("mcp").GetProperty("remote");
        Assert.Equal("remote", remote.GetProperty("type").GetString());
        Assert.Equal("https://example.test/mcp", remote.GetProperty("url").GetString());
        Assert.False(remote.TryGetProperty("command", out _));
        Assert.True(written.RootElement.GetProperty("mcp").TryGetProperty("fuse", out _));
    }

    [Theory]
    [InlineData(McpInstallClient.Codex)]
    [InlineData(McpInstallClient.Grok)]
    public async Task InstallAsync_TomlConfig_ReplacesFuseTableAndPreservesOtherTables(McpInstallClient client)
    {
        var root = CreateTempDirectory();
        var path = McpInstallService.GetConfigPath(client, McpInstallScope.Project, root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """
            [models]
            default = "existing"

            [mcp_servers.fuse]
            command = "old-fuse"
            args = ["old"]

            [mcp_servers.other]
            command = "other-tool"
            args = ["run"]
            """);

        var service = new McpInstallService();
        for (var i = 0; i < 2; i++)
            await service.InstallAsync(
                [client],
                McpInstallScope.Project,
                root,
                "new-fuse",
                writeRules: false,
                new RecordingConsoleUI(),
                CancellationToken.None);

        var written = await File.ReadAllTextAsync(path);
        Assert.Contains("[models]", written);
        Assert.Contains("default = \"existing\"", written);
        Assert.Contains("[mcp_servers.other]", written);
        Assert.Contains("command = \"other-tool\"", written);
        Assert.Contains("command = \"new-fuse\"", written);
        Assert.DoesNotContain("old-fuse", written);
        Assert.Equal(1, written.Split("[mcp_servers.fuse]").Length - 1);
        Assert.Equal(1, written.Split("# fuse:begin").Length - 1);
    }

    [Fact]
    public void GetConfigPath_ProjectScope_UsesProjectDirectory()
    {
        var projectRoot = CreateTempDirectory();

        Assert.Equal(
            Path.Combine(projectRoot, ".mcp.json"),
            McpInstallService.GetConfigPath(McpInstallClient.Claude, McpInstallScope.Project, projectRoot));
        Assert.Equal(
            Path.Combine(projectRoot, ".cursor", "mcp.json"),
            McpInstallService.GetConfigPath(McpInstallClient.Cursor, McpInstallScope.Project, projectRoot));
        Assert.Equal(
            Path.Combine(projectRoot, "opencode.json"),
            McpInstallService.GetConfigPath(McpInstallClient.OpenCode, McpInstallScope.Project, projectRoot));
        Assert.Equal(
            Path.Combine(projectRoot, ".kilo", "kilo.jsonc"),
            McpInstallService.GetConfigPath(McpInstallClient.Kilo, McpInstallScope.Project, projectRoot));
        Assert.Equal(
            Path.Combine(projectRoot, ".codex", "config.toml"),
            McpInstallService.GetConfigPath(McpInstallClient.Codex, McpInstallScope.Project, projectRoot));
        Assert.Equal(
            Path.Combine(projectRoot, ".grok", "config.toml"),
            McpInstallService.GetConfigPath(McpInstallClient.Grok, McpInstallScope.Project, projectRoot));
    }

    [Fact]
    public void GetConfigPath_PrefersExistingSupportedJsonConfigNames()
    {
        var openCodeRoot = CreateTempDirectory();
        var openCodeJsonc = Path.Combine(openCodeRoot, "opencode.jsonc");
        File.WriteAllText(openCodeJsonc, "{}");
        Assert.Equal(
            openCodeJsonc,
            McpInstallService.GetConfigPath(McpInstallClient.OpenCode, McpInstallScope.Project, openCodeRoot));

        var kiloRoot = CreateTempDirectory();
        var kiloJson = Path.Combine(kiloRoot, "kilo.json");
        File.WriteAllText(kiloJson, "{}");
        Assert.Equal(
            kiloJson,
            McpInstallService.GetConfigPath(McpInstallClient.Kilo, McpInstallScope.Project, kiloRoot));
    }

    [Fact]
    public void ResolveFuseCommand_UsesProcessPathWhenAvailable()
    {
        var path = McpInstallService.ResolveFuseCommand();
        Assert.False(string.IsNullOrWhiteSpace(path));
    }

    [Fact]
    public async Task InstallAsync_WriteRules_ProjectScope_WritesRuleFilesForAllClients()
    {
        var root = CreateTempDirectory();
        var service = new McpInstallService();

        await service.InstallAsync(
            [
                McpInstallClient.Claude,
                McpInstallClient.Cursor,
                McpInstallClient.Copilot,
                McpInstallClient.OpenCode,
                McpInstallClient.Kilo,
                McpInstallClient.Codex,
                McpInstallClient.Grok,
            ],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: true,
            new RecordingConsoleUI(),
            CancellationToken.None);

        var claude = await File.ReadAllTextAsync(Path.Combine(root, "CLAUDE.md"));
        AssertProperAgentGuidance(claude);
        Assert.Contains("<!-- fuse:begin", claude);

        var cursor = await File.ReadAllTextAsync(Path.Combine(root, ".cursor", "rules", "fuse.mdc"));
        Assert.Contains("alwaysApply: true", cursor);
        AssertProperAgentGuidance(cursor);

        var copilot = await File.ReadAllTextAsync(Path.Combine(root, ".github", "copilot-instructions.md"));
        AssertProperAgentGuidance(copilot);

        var agents = await File.ReadAllTextAsync(Path.Combine(root, "AGENTS.md"));
        AssertProperAgentGuidance(agents);
        Assert.Equal(1, agents.Split("<!-- fuse:begin").Length - 1);

        var gitIgnore = await File.ReadAllTextAsync(Path.Combine(root, ".gitignore"));
        Assert.Contains(".fuse/", gitIgnore);
    }

    [Fact]
    public async Task InstallAsync_WriteRules_ProjectScope_DoesNotDuplicateGitIgnoreEntry()
    {
        var root = CreateTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".fuse/\n");

        var service = new McpInstallService();
        await service.InstallAsync(
            [McpInstallClient.Cursor],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: true,
            new RecordingConsoleUI(),
            CancellationToken.None);

        var gitIgnore = await File.ReadAllTextAsync(Path.Combine(root, ".gitignore"));
        Assert.Equal(".fuse/\n", gitIgnore);
    }

    [Fact]
    public async Task InstallAsync_ProjectScopeWithoutRules_DoesNotWriteGitIgnore()
    {
        var root = CreateTempDirectory();
        var service = new McpInstallService();

        await service.InstallAsync(
            [McpInstallClient.Cursor],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(root, ".gitignore")));
    }

    [Fact]
    public async Task InstallAsync_ProjectScope_RefusesFolderWithoutGitIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-install-no-identity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var console = new RecordingConsoleUI();

        try
        {
            var configured = await new McpInstallService().InstallAsync(
                [McpInstallClient.Cursor],
                McpInstallScope.Project,
                root,
                "fuse",
                writeRules: true,
                console,
                CancellationToken.None);

            Assert.Equal(0, configured);
            Assert.Contains(console.Errors, error => error.Contains("Git repository identity", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(root, ".cursor", "mcp.json")));
            Assert.False(File.Exists(Path.Combine(root, "AGENTS.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_ProjectScope_FromNestedFolderWritesAtRepositoryRoot()
    {
        var root = CreateTempDirectory();
        var nested = Path.Combine(root, "src", "Feature");
        Directory.CreateDirectory(nested);
        var console = new RecordingConsoleUI();

        var configured = await new McpInstallService().InstallAsync(
            [McpInstallClient.Cursor],
            McpInstallScope.Project,
            nested,
            "fuse",
            writeRules: true,
            console,
            CancellationToken.None);

        Assert.Equal(1, configured);
        Assert.True(File.Exists(Path.Combine(root, ".cursor", "mcp.json")));
        Assert.True(File.Exists(Path.Combine(root, ".cursor", "rules", "fuse.mdc")));
        Assert.False(File.Exists(Path.Combine(nested, ".cursor", "mcp.json")));
        Assert.Contains(console.Steps, step => step.Contains("repository root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_UserScope_DoesNotRequireRepositoryIdentity()
    {
        using var home = new TempHomeScope();
        var root = Path.Combine(Path.GetTempPath(), "fuse-install-user-no-identity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var configured = await new McpInstallService().InstallAsync(
                [McpInstallClient.Cursor],
                McpInstallScope.User,
                root,
                "fuse",
                writeRules: false,
                new RecordingConsoleUI(),
                CancellationToken.None);

            Assert.Equal(1, configured);
            Assert.True(File.Exists(Path.Combine(home.Root, ".cursor", "mcp.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WriteRules_IsIdempotentAndPreservesSurroundingContent()
    {
        var root = CreateTempDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(root, "CLAUDE.md"),
            "# My project rules\n\nKeep this paragraph.\n");

        var service = new McpInstallService();

        // Run twice; the managed block must appear exactly once and the user's content must survive.
        for (var i = 0; i < 2; i++)
            await service.InstallAsync(
                [McpInstallClient.Claude],
                McpInstallScope.Project,
                root,
                "fuse",
                writeRules: true,
                new RecordingConsoleUI(),
                CancellationToken.None);

        var claude = await File.ReadAllTextAsync(Path.Combine(root, "CLAUDE.md"));
        Assert.Contains("Keep this paragraph.", claude);
        Assert.Equal("# My project rules", claude.Split('\n')[0]);
        var occurrences = claude.Split("<!-- fuse:begin").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task InstallAsync_WriteRules_UpgradesLegacyManagedBlockAndPreservesUserText()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "CLAUDE.md");
        await File.WriteAllTextAsync(
            path,
            "# Local guidance\n\n<!-- fuse:begin (managed by `fuse mcp install --rules`; edit outside these markers) -->\nold guidance\n<!-- fuse:end -->\n\nKeep this text.\n");

        await new McpInstallService().InstallAsync(
            [McpInstallClient.Claude],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: true,
            new RecordingConsoleUI(),
            CancellationToken.None);

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("# Local guidance", content);
        Assert.Contains("Keep this text.", content);
        Assert.Contains("<!-- fuse:begin v4.4 -->", content);
        Assert.DoesNotContain("old guidance", content);
        Assert.Equal(1, content.Split("<!-- fuse:begin").Length - 1);
    }

    [Fact]
    public async Task InstallAsync_WriteRules_UserScope_SkipsCursorAndCopilot()
    {
        var root = CreateTempDirectory();
        var service = new McpInstallService();

        await service.InstallAsync(
            [McpInstallClient.Cursor, McpInstallClient.Copilot],
            McpInstallScope.User,
            root,
            "fuse",
            writeRules: true,
            new RecordingConsoleUI(),
            CancellationToken.None);

        // User scope has no Cursor/Copilot rules file, so nothing is written under the project root.
        Assert.False(File.Exists(Path.Combine(root, ".cursor", "rules", "fuse.mdc")));
        Assert.False(File.Exists(Path.Combine(root, ".github", "copilot-instructions.md")));
    }

    [Fact]
    public async Task InstallAsync_WriteRules_UserScope_UsesDocumentedGlobalFiles()
    {
        using var home = new TempHomeScope();
        var root = CreateTempDirectory();
        var console = new RecordingConsoleUI();

        await new McpInstallService().InstallAsync(
            [McpInstallClient.OpenCode, McpInstallClient.Kilo, McpInstallClient.Codex, McpInstallClient.Grok],
            McpInstallScope.User,
            root,
            "fuse",
            writeRules: true,
            console,
            CancellationToken.None);

        var rulePaths = new[]
        {
            Path.Combine(home.Root, ".config", "opencode", "AGENTS.md"),
            Path.Combine(home.Root, ".config", "kilo", "AGENTS.md"),
            Path.Combine(home.Root, ".codex", "AGENTS.md"),
        };
        foreach (var path in rulePaths)
            AssertProperAgentGuidance(await File.ReadAllTextAsync(path));

        Assert.False(File.Exists(Path.Combine(home.Root, ".grok", "AGENTS.md")));
        Assert.Contains(
            console.Steps,
            message => message.Contains("no documented user-global AGENTS.md", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentGuidance_UsesTaskSpecificRoutingAndStatesVerificationLimits()
    {
        AssertProperAgentGuidance(FuseAgentGuidance.RuleBody);
        AssertProperAgentGuidance(FuseAgentGuidance.ServerInstructions);
        Assert.Contains("fuse_workspace action=apply", FuseAgentGuidance.ServerInstructions);
        Assert.Contains("does not apply a multi-file patch", FuseAgentGuidance.ServerInstructions);
    }

    [Fact]
    public void Install_DoesNotRequireCommandOption()
    {
        // Regression: the optional --command option was inferred as required by the command framework, so
        // `fuse mcp install` failed with "Option '--command' is required" unless a value was passed. Parse only
        // (no execution) so this never writes real client config.
        var result = DotMake.CommandLine.Cli.Parse<Fuse.Cli.FuseCliCommand>(["mcp", "install", "--scope", "user"]);

        Assert.DoesNotContain(
            result.ParseResult.Errors,
            error => error.Message.Contains("command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Install_AcceptsNoRulesAndDefaultsToManagedGuidance()
    {
        var result = DotMake.CommandLine.Cli.Parse<Fuse.Cli.FuseCliCommand>(["mcp", "install", "--no-rules", "--scope", "user"]);

        Assert.DoesNotContain(result.ParseResult.Errors, error => error.Message.Contains("no-rules", StringComparison.OrdinalIgnoreCase));
        Assert.False(new InstallCommand().NoRules);
    }

    [Fact]
    public void Install_RejectsRemovedRulesOption()
    {
        var result = DotMake.CommandLine.Cli.Parse<Fuse.Cli.FuseCliCommand>(["mcp", "install", "--rules"]);

        Assert.Contains(result.ParseResult.Errors, error => error.Message.Contains("rules", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void McpDoctor_AcceptsJsonAndHookOptions()
    {
        var result = DotMake.CommandLine.Cli.Parse<Fuse.Cli.FuseCliCommand>(
            ["mcp", "doctor", "--client", "codex", "--with-hooks", "--json"]);

        Assert.Empty(result.ParseResult.Errors);
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("kilo")]
    [InlineData("codex")]
    [InlineData("grok")]
    public void Install_AcceptsNewClientNames(string client)
    {
        var parsed = InstallCommand.TryParseClient(client, out var clients);

        Assert.True(parsed);
        Assert.Single(clients);
    }

    [Fact]
    public void Install_AllIncludesEverySupportedClient()
    {
        var parsed = InstallCommand.TryParseClient("all", out var clients);

        Assert.True(parsed);
        Assert.Equal(Enum.GetValues<McpInstallClient>(), clients);
    }

    private static void AssertProperAgentGuidance(string content)
    {
        Assert.Contains("For repository code tasks:", content);
        Assert.Contains("Call fuse_workspace with action=status before broad discovery.", content);
        Assert.Contains("Call fuse_find before broad file search", content);
        Assert.Contains("Call fuse_find with kind=task for open-ended localization.", content);
        Assert.Contains("Call fuse_impact before changing a public signature.", content);
        Assert.Contains("Call fuse_check after a proposed single-file edit.", content);
        Assert.Contains("Call fuse_review before handoff.", content);
        Assert.Contains("Do not repeat a refused query.", content);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fuse-install-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    private sealed class RecordingConsoleUI : IConsoleUI
    {
        public List<string> Errors { get; } = [];

        public List<string> Steps { get; } = [];

        public void WriteSuccess(string message)
        {
        }

        public void WriteError(string message) => Errors.Add(message);

        public void WriteStep(string message)
        {
            Steps.Add(message);
        }

        public void WriteResult(string message)
        {
        }
    }

    private sealed class TempHomeScope : IDisposable
    {
        private readonly string? _originalHome;

        public TempHomeScope()
        {
            Root = Path.Combine(Path.GetTempPath(), "fuse-install-home", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            _originalHome = Environment.GetEnvironmentVariable(McpInstallService.UserProfileOverrideEnvironmentVariable);
            Environment.SetEnvironmentVariable(McpInstallService.UserProfileOverrideEnvironmentVariable, Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(McpInstallService.UserProfileOverrideEnvironmentVariable, _originalHome);
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class EmptyPathScope : IDisposable
    {
        private readonly string? _originalPath;

        public EmptyPathScope()
        {
            _originalPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", string.Empty);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("PATH", _originalPath);
    }
}
