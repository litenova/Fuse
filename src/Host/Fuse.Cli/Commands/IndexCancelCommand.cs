using System.Text.Json;
using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Serialization;
using Fuse.Cli.Services;

namespace Fuse.Cli.Commands;

/// <summary>Requests cancellation of the active repository index job.</summary>
[CliCommand(
    Name = "cancel",
    Description = "Request cancellation of the active repository index job.",
    Parent = typeof(IndexCommand))]
public sealed class IndexCancelCommand
{
    private readonly IndexJobClient _jobs;
    private readonly IConsoleUI _consoleUI;

    /// <summary>Initializes an instance for CLI option binding only.</summary>
    public IndexCancelCommand() : this(null!, null!)
    {
    }

    /// <summary>Initializes the cancellation command.</summary>
    /// <param name="jobs">The index lifecycle client.</param>
    /// <param name="consoleUI">The console output service.</param>
    public IndexCancelCommand(IndexJobClient jobs, IConsoleUI consoleUI)
    {
        _jobs = jobs;
        _consoleUI = consoleUI;
    }

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Whether to emit one JSON object.</summary>
    [CliOption(Name = "--json", Required = false, Description = "Emit one JSON cancellation object.")]
    public bool Json { get; set; }

    /// <summary>Runs the cancellation command.</summary>
    /// <param name="context">The CLI invocation context.</param>
    /// <returns>A task that completes after cancellation is requested.</returns>
    public async Task RunAsync(CliContext context)
    {
        if (!IndexCommand.TryResolveRepositoryRoot(Path, out var root))
        {
            IndexCommand.RenderError(
                "workspace_identity_unresolved",
                "Fuse index cancel requires a Git repository. Run 'git init' or pass a path inside an existing repository.",
                Json,
                exitCode: 2);
            return;
        }

        if (!Directory.Exists(root))
        {
            IndexCommand.RenderError("workspace_not_found", $"Directory does not exist: {root}", Json, exitCode: 2);
            return;
        }

        try
        {
            var result = await _jobs.CancelAsync(root, context.CancellationToken);
            var payload = new IndexCliStatus(
                result.Snapshot is null ? "no_active_job" : "cancellation_requested",
                result.Snapshot,
                IndexStorageReader.Read(root),
                await IndexStorageReader.ReadStoreAsync(root, context.CancellationToken));
            if (Json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(payload, IndexCliJsonContext.Default.IndexCliStatus));
                return;
            }

            _consoleUI.WriteResult(result.Snapshot is null
                ? "No active index job."
                : $"Cancellation requested for job {result.Snapshot.JobId}.");
        }
        catch (IndexJobClientException ex)
        {
            IndexCommand.RenderError(ex.Code, ex.Message, Json, exitCode: 3);
        }
        catch (Exception ex)
        {
            IndexCommand.RenderError("index_cancel_failed", ex.Message, Json, exitCode: 3);
        }
    }
}
