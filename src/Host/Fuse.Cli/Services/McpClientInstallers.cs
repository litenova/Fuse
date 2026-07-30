using Fuse.Cli.Configuration.McpInstall;

namespace Fuse.Cli.Services;

/// <summary>
///     Installs one supported MCP client registration from a normalized coordinator request.
/// </summary>
public interface IMcpClientInstaller
{
    /// <summary>Gets the client this installer owns.</summary>
    McpInstallClient Client { get; }

    /// <summary>Writes or updates this client's MCP registration.</summary>
    /// <param name="request">The normalized registration request.</param>
    /// <returns>True when registration was written or already current.</returns>
    Task<bool> InstallAsync(McpClientInstallRequest request);
}

/// <summary>
///     The validated input shared by each client-specific MCP installer.
/// </summary>
/// <param name="Scope">The requested project or user scope.</param>
/// <param name="ProjectRoot">The resolved repository root or user-scope path base.</param>
/// <param name="FuseCommand">The executable the client must launch.</param>
/// <param name="ConsoleUI">The caller's human-readable reporting surface.</param>
/// <param name="CancellationToken">The token that cancels external client registration work.</param>
public sealed record McpClientInstallRequest(
    McpInstallScope Scope,
    string ProjectRoot,
    string FuseCommand,
    IConsoleUI ConsoleUI,
    CancellationToken CancellationToken);

internal sealed class ClaudeMcpClientInstaller : IMcpClientInstaller
{
    public McpInstallClient Client => McpInstallClient.Claude;

    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        request.Scope == McpInstallScope.User
            ? McpInstallFiles.RegisterClaudeUserAsync(request.FuseCommand, request.ConsoleUI, request.CancellationToken)
            : Task.FromResult(McpInstallFiles.WriteClaudeProjectConfig(request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

internal sealed class CursorMcpClientInstaller : IMcpClientInstaller
{
    public McpInstallClient Client => McpInstallClient.Cursor;

    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        Task.FromResult(McpInstallFiles.WriteCursorConfig(request.Scope, request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

internal sealed class CopilotMcpClientInstaller : IMcpClientInstaller
{
    public McpInstallClient Client => McpInstallClient.Copilot;

    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        Task.FromResult(McpInstallFiles.WriteCopilotConfig(request.Scope, request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

internal sealed class OpenCodeMcpClientInstaller : IMcpClientInstaller
{
    public McpInstallClient Client => McpInstallClient.OpenCode;

    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        Task.FromResult(McpInstallFiles.WriteLocalArrayConfig(Client, request.Scope, request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

internal sealed class KiloMcpClientInstaller : IMcpClientInstaller
{
    public McpInstallClient Client => McpInstallClient.Kilo;

    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        Task.FromResult(McpInstallFiles.WriteLocalArrayConfig(Client, request.Scope, request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

internal sealed class CodexMcpClientInstaller : IMcpClientInstaller
{
    public McpInstallClient Client => McpInstallClient.Codex;

    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        Task.FromResult(McpInstallFiles.WriteTomlConfig(Client, request.Scope, request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

internal sealed class GrokMcpClientInstaller : IMcpClientInstaller
{
    public McpInstallClient Client => McpInstallClient.Grok;

    public Task<bool> InstallAsync(McpClientInstallRequest request) =>
        Task.FromResult(McpInstallFiles.WriteTomlConfig(Client, request.Scope, request.ProjectRoot, request.FuseCommand, request.ConsoleUI));
}

internal static class McpClientInstallerCatalog
{
    public static IReadOnlyList<IMcpClientInstaller> CreateDefault() =>
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
