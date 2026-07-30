using Fuse.Cli.Configuration.McpInstall;
using Fuse.Collection.FileSystem;

namespace Fuse.Cli.Services;

/// <summary>
///     Coordinates MCP client registration, managed guidance, and project-scoped ignore setup.
/// </summary>
public sealed class McpInstallService
{
    private readonly IReadOnlyDictionary<McpInstallClient, IMcpClientInstaller> _installers;

    /// <summary>
    ///     Initializes an installation coordinator with the built-in client installers.
    /// </summary>
    public McpInstallService() : this(McpClientInstallerCatalog.CreateDefault())
    {
    }

    /// <summary>
    ///     Initializes an installation coordinator with one installer per supported client.
    /// </summary>
    /// <param name="installers">The client-specific registration writers.</param>
    public McpInstallService(IEnumerable<IMcpClientInstaller> installers)
    {
        ArgumentNullException.ThrowIfNull(installers);
        _installers = installers.ToDictionary(installer => installer.Client);
    }

    /// <summary>
    ///     Registers Fuse with the requested MCP clients at the given scope.
    /// </summary>
    /// <param name="clients">The clients to configure.</param>
    /// <param name="scope">Project-local files or user-global registration.</param>
    /// <param name="projectDirectory">A path inside the project repository; defaults to the current directory.</param>
    /// <param name="fuseCommand">The executable the client should launch; defaults to the running binary or <c>fuse</c>.</param>
    /// <param name="writeRules">Whether to write managed task-routing guidance after successful registration.</param>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <param name="cancellationToken">A token that cancels client registration.</param>
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
        if (!McpInstallFiles.TryValidateFuseCommand(fuseCommand, out var command, out var validationError))
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
            if (!_installers.TryGetValue(client, out var installer))
            {
                consoleUI.WriteError($"No installer is registered for {McpInstallFiles.DescribeClient(client)}.");
                continue;
            }

            var success = await installer.InstallAsync(
                new McpClientInstallRequest(scope, projectRoot, command, consoleUI, cancellationToken));
            if (success)
                configuredClients.Add(client);
        }

        if (writeRules)
        {
            foreach (var client in configuredClients)
                McpInstallFiles.WriteClientRule(client, scope, projectRoot, consoleUI);

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
}
