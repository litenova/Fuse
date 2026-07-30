using System.Text.Json;
using Fuse.Cli.Configuration.McpInstall;
using Fuse.Cli.Serialization;
using Fuse.Cli.Services;

namespace Fuse.Cli.Tests;

// R21: `fuse mcp install` golden config documents agent-first defaults (no env block required).
public sealed class McpInstallGoldenTests
{
    [Theory]
    [InlineData(McpInstallClient.Cursor, ".cursor/mcp.json", "mcpServers")]
    [InlineData(McpInstallClient.Copilot, ".vscode/mcp.json", "servers")]
    public async Task InstallAsync_ProjectScope_GoldenConfigHasNoEnvBlock(
        McpInstallClient client,
        string relativePath,
        string serversProperty)
    {
        var root = CreateTempDirectory();
        var service = new McpInstallService();

        var configured = await service.InstallAsync(
            [client],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        Assert.Equal(1, configured);

        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var golden = await File.ReadAllTextAsync(path);
        using var document = JsonDocument.Parse(golden);
        var fuse = document.RootElement.GetProperty(serversProperty).GetProperty("fuse");

        Assert.Equal("fuse", fuse.GetProperty("command").GetString());
        Assert.Equal(["mcp", "serve"], fuse.GetProperty("args").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(fuse.TryGetProperty("env", out _));
        Assert.DoesNotContain("\"env\"", golden, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_ClaudeProjectScope_GoldenConfigHasNoEnvBlock()
    {
        var root = CreateTempDirectory();
        var service = new McpInstallService();

        await service.InstallAsync(
            [McpInstallClient.Claude],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        var golden = await File.ReadAllTextAsync(Path.Combine(root, ".mcp.json"));
        using var document = JsonDocument.Parse(golden);
        var fuse = document.RootElement.GetProperty("mcpServers").GetProperty("fuse");

        Assert.Equal("fuse", fuse.GetProperty("command").GetString());
        Assert.Equal("stdio", fuse.GetProperty("type").GetString());
        Assert.Equal(["mcp", "serve"], fuse.GetProperty("args").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(fuse.TryGetProperty("env", out _));
    }

    [Theory]
    [InlineData(McpInstallClient.OpenCode)]
    [InlineData(McpInstallClient.Kilo)]
    public async Task InstallAsync_CommandArrayConfigHasNoEnvBlock(McpInstallClient client)
    {
        var root = CreateTempDirectory();
        await new McpInstallService().InstallAsync(
            [client],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        var path = McpInstallFiles.GetConfigPath(client, McpInstallScope.Project, root);
        var golden = await File.ReadAllTextAsync(path);
        using var document = JsonDocument.Parse(golden);
        var fuse = document.RootElement.GetProperty("mcp").GetProperty("fuse");

        Assert.Equal(
            ["fuse", "mcp", "serve"],
            fuse.GetProperty("command").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.False(fuse.TryGetProperty("env", out _));
    }

    [Theory]
    [InlineData(McpInstallClient.Codex)]
    [InlineData(McpInstallClient.Grok)]
    public async Task InstallAsync_TomlConfigHasNoEnvBlock(McpInstallClient client)
    {
        var root = CreateTempDirectory();
        await new McpInstallService().InstallAsync(
            [client],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        var path = McpInstallFiles.GetConfigPath(client, McpInstallScope.Project, root);
        var golden = await File.ReadAllTextAsync(path);

        Assert.Contains("[mcp_servers.fuse]", golden);
        Assert.Contains("command = \"fuse\"", golden);
        Assert.Contains("args = [\"mcp\", \"serve\"]", golden);
        Assert.DoesNotContain("env", golden, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fuse-install-golden", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    private sealed class RecordingConsoleUI : IConsoleUI
    {
        public void WriteSuccess(string message)
        {
        }

        public void WriteError(string message)
        {
        }

        public void WriteStep(string message)
        {
        }

        public void WriteResult(string message)
        {
        }
    }
}
