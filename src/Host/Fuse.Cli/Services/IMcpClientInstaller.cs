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
