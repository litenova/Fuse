using Fuse.Cli.Configuration.McpInstall;

namespace Fuse.Cli.Services;

/// <summary>
///     The set of shipped MCP client installers. Adding a supported client means adding one installer here and
///     registering it in composition; no other code branches on the client.
/// </summary>
internal static class McpClientInstallerCatalog
{
    /// <summary>Creates one installer per supported client, in the order installation reports them.</summary>
    /// <returns>The shipped installers.</returns>
    internal static IReadOnlyList<IMcpClientInstaller> CreateDefault() =>
    [
        new ClaudeMcpClientInstaller(),
        new CursorMcpClientInstaller(),
        new CopilotMcpClientInstaller(),
        new OpenCodeMcpClientInstaller(),
        new KiloMcpClientInstaller(),
        new CodexMcpClientInstaller(),
        new GrokMcpClientInstaller(),
    ];
}

/// <summary>Registers Fuse with Claude Code, using its own CLI for user scope and a config file for project scope.</summary>
internal sealed class ClaudeMcpClientInstaller : IMcpClientInstaller
{
    /// <inheritdoc />
    public McpInstallClient Client => McpInstallClient.Claude;

    /// <inheritdoc />
    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        request.Scope == McpInstallScope.User
            ? McpInstallFiles.RegisterClaudeUserAsync(request.FuseCommand, request.ConsoleUI, request.CancellationToken)
            : Task.FromResult(McpInstallFiles.WriteClaudeProjectConfig(request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

/// <summary>Registers Fuse with Cursor.</summary>
internal sealed class CursorMcpClientInstaller : IMcpClientInstaller
{
    /// <inheritdoc />
    public McpInstallClient Client => McpInstallClient.Cursor;

    /// <inheritdoc />
    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        Task.FromResult(McpInstallFiles.WriteCursorConfig(request.Scope, request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

/// <summary>Registers Fuse with GitHub Copilot.</summary>
internal sealed class CopilotMcpClientInstaller : IMcpClientInstaller
{
    /// <inheritdoc />
    public McpInstallClient Client => McpInstallClient.Copilot;

    /// <inheritdoc />
    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        Task.FromResult(McpInstallFiles.WriteCopilotConfig(request.Scope, request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

/// <summary>Registers Fuse with OpenCode, whose config lists servers in a local array.</summary>
internal sealed class OpenCodeMcpClientInstaller : IMcpClientInstaller
{
    /// <inheritdoc />
    public McpInstallClient Client => McpInstallClient.OpenCode;

    /// <inheritdoc />
    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        Task.FromResult(McpInstallFiles.WriteLocalArrayConfig(Client, request.Scope, request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

/// <summary>Registers Fuse with Kilo Code, whose config lists servers in a local array.</summary>
internal sealed class KiloMcpClientInstaller : IMcpClientInstaller
{
    /// <inheritdoc />
    public McpInstallClient Client => McpInstallClient.Kilo;

    /// <inheritdoc />
    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        Task.FromResult(McpInstallFiles.WriteLocalArrayConfig(Client, request.Scope, request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

/// <summary>Registers Fuse with Codex, whose config is TOML.</summary>
internal sealed class CodexMcpClientInstaller : IMcpClientInstaller
{
    /// <inheritdoc />
    public McpInstallClient Client => McpInstallClient.Codex;

    /// <inheritdoc />
    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        Task.FromResult(McpInstallFiles.WriteTomlConfig(Client, request.Scope, request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

/// <summary>Registers Fuse with Grok Build, whose config is TOML.</summary>
internal sealed class GrokMcpClientInstaller : IMcpClientInstaller
{
    /// <inheritdoc />
    public McpInstallClient Client => McpInstallClient.Grok;

    /// <inheritdoc />
    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        Task.FromResult(McpInstallFiles.WriteTomlConfig(Client, request.Scope, request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}
