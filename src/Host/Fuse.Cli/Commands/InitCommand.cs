using DotMake.CommandLine;
using Fuse.Cli;
using Fuse.Cli.Services;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Semantics;

namespace Fuse.Cli.Commands;

/// <summary>
///     Creates a <c>fuse.json</c> configuration file in the current directory.
/// </summary>
[CliCommand(Name = "init", Description = "Create a fuse.json configuration file in the current directory.", Parent = typeof(FuseCliCommand))]
public sealed class InitCommand
{
    private readonly IConsoleUI _consoleUI;
    private readonly DotNetWorkspaceDiscoverer _workspaceDiscoverer;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InitCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>
    ///     Used by DotMake.CommandLine to bind options; the console UI is <see langword="null" />, so this instance
    ///     must not run.
    /// </remarks>
    public InitCommand() : this(null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="InitCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <param name="workspaceDiscoverer">The service that finds one unambiguous starter workspace.</param>
    public InitCommand(IConsoleUI consoleUI, DotNetWorkspaceDiscoverer workspaceDiscoverer)
    {
        _consoleUI = consoleUI;
        _workspaceDiscoverer = workspaceDiscoverer;
    }

    /// <summary>
    ///     Writes a starter <c>fuse.json</c> to the current directory when one does not already exist.
    /// </summary>
    /// <param name="context">The CLI invocation context.</param>
    /// <returns>A completed task once the file is written or the existing-file warning is reported.</returns>
    /// <remarks>
    ///     Creates <c>fuse.json</c> in the current working directory as a side effect. If the file already exists,
    ///     nothing is written and an error is reported through the console UI.
    /// </remarks>
    public async Task RunAsync(CliContext context)
    {
        var currentDirectory = System.IO.Directory.GetCurrentDirectory();
        var root = WorkspaceIdentityResolver.TryResolveRepositoryRoot(currentDirectory, out var repositoryRoot)
            ? repositoryRoot
            : currentDirectory;
        var targetPath = Path.Combine(root, WorkspaceConfiguration.FileName);
        if (File.Exists(targetPath))
        {
            _consoleUI.WriteError("fuse.json already exists in the current directory.");
            return;
        }

        string? workspace = null;
        try
        {
            var discovery = await _workspaceDiscoverer.DiscoverAsync(root, context.CancellationToken);
            if (discovery.Kind == WorkspaceKind.Solution && discovery.SolutionPath is not null)
                workspace = Path.GetRelativePath(root, discovery.SolutionPath);
            else if (discovery.Kind == WorkspaceKind.Projects && discovery.ProjectPaths.Count == 1)
                workspace = Path.GetRelativePath(root, discovery.ProjectPaths[0]);
        }
        catch (WorkspaceConfigurationException ex)
        {
            _consoleUI.WriteStep($"Workspace was not pinned: {ex.Message}");
        }

        File.WriteAllText(targetPath, WorkspaceConfiguration.CreateTemplate(workspace));
        _consoleUI.WriteSuccess($"Created {targetPath}");
        GitIgnoreHelper.TryEnsureFuseEntry(
            System.IO.Directory.GetCurrentDirectory(),
            _consoleUI.WriteStep,
            _consoleUI.WriteStep);
    }
}
