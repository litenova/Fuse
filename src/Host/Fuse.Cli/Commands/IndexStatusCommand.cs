using System.Text.Json;
using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Serialization;
using Fuse.Cli.Services;

namespace Fuse.Cli.Commands;

/// <summary>Displays the active or retained index job and derived-index storage for a repository.</summary>
[CliCommand(
    Name = "status",
    Description = "Show index job state, counts, and derived-index storage.",
    Parent = typeof(IndexCommand))]
public sealed class IndexStatusCommand
{
    private readonly IndexJobClient _jobs;
    private readonly IConsoleUI _consoleUI;

    /// <summary>Initializes an instance for CLI option binding only.</summary>
    public IndexStatusCommand() : this(null!, null!)
    {
    }

    /// <summary>Initializes the status command.</summary>
    /// <param name="jobs">The index lifecycle client.</param>
    /// <param name="consoleUI">The console output service.</param>
    public IndexStatusCommand(IndexJobClient jobs, IConsoleUI consoleUI)
    {
        _jobs = jobs;
        _consoleUI = consoleUI;
    }

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Whether to emit one JSON object.</summary>
    [CliOption(Name = "--json", Required = false, Description = "Emit one JSON status object.")]
    public bool Json { get; set; }

    /// <summary>Runs the status command.</summary>
    /// <param name="context">The CLI invocation context.</param>
    /// <returns>A task that completes after the current job state is read.</returns>
    public async Task RunAsync(CliContext context)
    {
        if (!IndexCommand.TryResolveRepositoryRoot(Path, out var root))
        {
            IndexCommand.RenderError(
                "workspace_identity_unresolved",
                "Fuse index status requires a Git repository. Run 'git init' or pass a path inside an existing repository.",
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
            var result = await _jobs.StatusAsync(root, context.CancellationToken);
            var storage = IndexStorageReader.Read(root);
            var store = await IndexStorageReader.ReadStoreAsync(root, context.CancellationToken);
            var payload = new IndexCliStatus(
                result.Snapshot is null ? store.State : "available", result.Snapshot, storage, store);
            if (Json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(payload, IndexCliJsonContext.Default.IndexCliStatus));
                return;
            }

            if (result.Snapshot is null)
            {
                WriteRetainedStore(store, storage);
                return;
            }

            WriteJob(result.Snapshot, storage);
        }
        catch (IndexJobClientException ex)
        {
            IndexCommand.RenderError(ex.Code, ex.Message, Json, exitCode: 3);
        }
        catch (Exception ex)
        {
            IndexCommand.RenderError("index_status_failed", ex.Message, Json, exitCode: 3);
        }
    }

    private void WriteRetainedStore(IndexStoreStatus store, IndexStorageSnapshot storage)
    {
        _consoleUI.WriteResult(
            $"No retained index job. Index mode: {store.IndexMode ?? "none"}; freshness: {store.Freshness}; "
            + $"files: {store.Files}; symbols: {store.Symbols}; chunks: {store.Chunks}; routes: {store.Routes}.");
        _consoleUI.WriteStep($"Storage: {storage.TotalFuseBytes} bytes in .fuse.");
        if (!string.IsNullOrWhiteSpace(store.LastFailure))
            _consoleUI.WriteError($"Last failure: {store.LastFailure}");
    }

    private void WriteJob(IndexJobSnapshot snapshot, IndexStorageSnapshot storage)
    {
        _consoleUI.WriteResult(
            $"Job {snapshot.JobId}: {snapshot.State}, {snapshot.Phase}, "
            + $"{snapshot.Counts.Files} files, {snapshot.Counts.Symbols} symbols.");
        _consoleUI.WriteStep(
            $"Storage: database {storage.DatabaseBytes} bytes, WAL {storage.WalBytes} bytes, "
            + $"shared memory {storage.SharedMemoryBytes} bytes, total {storage.TotalFuseBytes} bytes.");
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorCode))
            _consoleUI.WriteError($"{snapshot.ErrorCode}: {snapshot.ErrorMessage}");
    }
}
