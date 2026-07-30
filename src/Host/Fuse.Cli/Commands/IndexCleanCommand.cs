using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Reduction.Caching;
using Microsoft.Data.Sqlite;

namespace Fuse.Cli.Commands;

/// <summary>Cancels an index job and removes only Fuse-derived index files for one repository.</summary>
[CliCommand(
    Name = "clean",
    Description = "Cancel indexing and delete Fuse-derived index files for a workspace.",
    Parent = typeof(IndexCommand))]
public sealed class IndexCleanCommand
{
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IndexJobClient _jobs;
    private readonly IConsoleUI _consoleUI;

    /// <summary>Initializes an instance for CLI option binding only.</summary>
    public IndexCleanCommand() : this(null!, null!)
    {
    }

    /// <summary>Initializes the cleanup command.</summary>
    /// <param name="jobs">The index lifecycle client.</param>
    /// <param name="consoleUI">The console output service.</param>
    public IndexCleanCommand(IndexJobClient jobs, IConsoleUI consoleUI)
    {
        _jobs = jobs;
        _consoleUI = consoleUI;
    }

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Whether to delete the displayed derived files without an interactive confirmation.</summary>
    [CliOption(Name = "--yes", Required = false, Description = "Delete the documented Fuse-derived files without prompting.")]
    public bool Yes { get; set; }

    /// <summary>Runs the cleanup command.</summary>
    /// <param name="context">The CLI invocation context.</param>
    /// <returns>A task that completes after derived files are removed.</returns>
    public async Task RunAsync(CliContext context)
    {
        if (!IndexCommand.TryResolveRepositoryRoot(Path, out var root))
        {
            IndexCommand.RenderError(
                "workspace_identity_unresolved",
                "Fuse index clean requires a Git repository. Run 'git init' or pass a path inside an existing repository.",
                json: false,
                exitCode: 2);
            return;
        }

        if (!Directory.Exists(root))
        {
            IndexCommand.RenderError("workspace_not_found", $"Directory does not exist: {root}", json: false, exitCode: 2);
            return;
        }

        var displayedTargets = IndexStorageReader.DerivedFiles(root).Where(File.Exists).ToArray();
        if (!Yes && !Confirm(displayedTargets))
            return;

        try
        {
            await _jobs.CancelAsync(root, context.CancellationToken);
            await WaitForStopAsync(root, context.CancellationToken);
            // Release this process's pooled connections to the store before deleting its files. Only this
            // database's pool is cleared; parallel fixtures and other roots keep their own native pools.
            var databasePath = FuseStorePaths.ResolveDatabasePath(root);
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={databasePath}"));
            var targets = IndexStorageReader.DerivedFiles(root).Where(File.Exists).ToArray();
            foreach (var target in targets)
                File.Delete(target);
            _consoleUI.WriteSuccess($"Removed {targets.Length} Fuse-derived file(s).");
        }
        catch (Exception ex)
        {
            IndexCommand.RenderError("index_clean_failed", ex.Message, json: false, exitCode: 3);
        }
    }

    private bool Confirm(IReadOnlyList<string> targets)
    {
        if (Console.IsInputRedirected)
        {
            IndexCommand.RenderError(
                "clean_confirmation_required",
                "Use 'fuse index clean --yes' when input is redirected.",
                json: false,
                exitCode: 2);
            return false;
        }

        _consoleUI.WriteResult("The following Fuse-derived files will be removed:");
        foreach (var target in targets)
            _consoleUI.WriteStep(target);
        Console.Write("Delete these files? [y/N] ");
        var answer = Console.ReadLine();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private async Task WaitForStopAsync(string root, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StopWait);
        while (true)
        {
            var status = await _jobs.StatusAsync(root, timeout.Token);
            if (status.Snapshot is not { State: IndexJobState.Queued or IndexJobState.Running or IndexJobState.Cancelling })
                return;
            await Task.Delay(StopPollInterval, timeout.Token);
        }
    }
}
