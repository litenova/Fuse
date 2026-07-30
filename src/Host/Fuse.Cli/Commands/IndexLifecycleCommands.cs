using System.Text.Json;
using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Serialization;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;

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
            var payload = new IndexCliStatus(result.Snapshot is null ? store.State : "available", result.Snapshot, storage, store);
            if (Json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(payload, IndexCliJsonContext.Default.IndexCliStatus));
                return;
            }

            if (result.Snapshot is null)
            {
                _consoleUI.WriteResult(
                    $"No retained index job. Index mode: {store.IndexMode ?? "none"}; freshness: {store.Freshness}; "
                    + $"files: {store.Files}; symbols: {store.Symbols}; chunks: {store.Chunks}; routes: {store.Routes}.");
                _consoleUI.WriteStep($"Storage: {storage.TotalFuseBytes} bytes in .fuse.");
                if (!string.IsNullOrWhiteSpace(store.LastFailure))
                    _consoleUI.WriteError($"Last failure: {store.LastFailure}");
                return;
            }

            var snapshot = result.Snapshot;
            _consoleUI.WriteResult(
                $"Job {snapshot.JobId}: {snapshot.State}, {snapshot.Phase}, "
                + $"{snapshot.Counts.Files} files, {snapshot.Counts.Symbols} symbols.");
            _consoleUI.WriteStep(
                $"Storage: database {storage.DatabaseBytes} bytes, WAL {storage.WalBytes} bytes, "
                + $"shared memory {storage.SharedMemoryBytes} bytes, total {storage.TotalFuseBytes} bytes.");
            if (!string.IsNullOrWhiteSpace(snapshot.ErrorCode))
                _consoleUI.WriteError($"{snapshot.ErrorCode}: {snapshot.ErrorMessage}");
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
}

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
            var storage = IndexStorageReader.Read(root);
            var store = await IndexStorageReader.ReadStoreAsync(root, context.CancellationToken);
            var payload = new IndexCliStatus(
                result.Snapshot is null ? "no_active_job" : "cancellation_requested",
                result.Snapshot,
                storage,
                store);
            if (Json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(payload, IndexCliJsonContext.Default.IndexCliStatus));
                return;
            }

            if (result.Snapshot is null)
                _consoleUI.WriteResult("No active index job.");
            else
                _consoleUI.WriteResult($"Cancellation requested for job {result.Snapshot.JobId}.");
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

/// <summary>Cancels an index job and removes only Fuse-derived index files for one repository.</summary>
[CliCommand(
    Name = "clean",
    Description = "Cancel indexing and delete Fuse-derived index files for a workspace.",
    Parent = typeof(IndexCommand))]
public sealed class IndexCleanCommand
{
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

        var targets = IndexStorageReader.DerivedFiles(root).Where(File.Exists).ToArray();
        if (!Yes && !Confirm(targets))
            return;

        try
        {
            await _jobs.CancelAsync(root, context.CancellationToken);
            await WaitForStopAsync(root, context.CancellationToken);
            var databasePath = FuseStorePaths.ResolveDatabasePath(root);
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={databasePath}"));
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
            IndexCommand.RenderError("clean_confirmation_required", "Use 'fuse index clean --yes' when input is redirected.", json: false, exitCode: 2);
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
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var status = await _jobs.StatusAsync(root, timeout.Token);
            if (status.Snapshot is not { State: IndexJobState.Queued or IndexJobState.Running or IndexJobState.Cancelling })
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }
}

/// <summary>The JSON and human status payload for index lifecycle commands.</summary>
/// <param name="Status">The status command outcome.</param>
/// <param name="Job">The retained index job, when one exists.</param>
/// <param name="Storage">The current derived-index storage usage.</param>
/// <param name="Store">The persisted index state and counts.</param>
public sealed record IndexCliStatus(
    string Status,
    IndexJobSnapshot? Job,
    IndexStorageSnapshot Storage,
    IndexStoreStatus Store);

/// <summary>Reads the known SQLite-derived files without opening or mutating the database.</summary>
internal static class IndexStorageReader
{
    /// <summary>Gets the known derived files that cleanup may delete.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Only documented Fuse-derived file paths.</returns>
    public static IReadOnlyList<string> DerivedFiles(string root)
    {
        var database = FuseStorePaths.ResolveDatabasePath(root);
        var directory = Path.GetDirectoryName(database)!;
        return
        [
            database,
            database + "-wal",
            database + "-shm",
            Path.Combine(directory, "fuse-cache.db"),
            Path.Combine(directory, "fuse-cache.db-wal"),
            Path.Combine(directory, "fuse-cache.db-shm"),
            Path.Combine(directory, "r60-semantics.json"),
        ];
    }

    /// <summary>Reads derived-index file sizes without creating a SQLite connection.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The current storage snapshot.</returns>
    public static IndexStorageSnapshot Read(string root)
    {
        var database = FuseStorePaths.ResolveDatabasePath(root);
        var wal = database + "-wal";
        var sharedMemory = database + "-shm";
        var directory = Path.GetDirectoryName(database)!;
        return new IndexStorageSnapshot(
            SizeOf(database),
            SizeOf(wal),
            SizeOf(sharedMemory),
            Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Sum(SizeOf)
                : 0);
    }

    /// <summary>Reads index metadata and counts without starting or mutating an index job.</summary>
    /// <param name="root">The canonical repository root.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The persisted index summary, or <see cref="IndexStoreStatus.NotIndexed" /> when no readable database exists.</returns>
    public static async Task<IndexStoreStatus> ReadStoreAsync(string root, CancellationToken cancellationToken)
    {
        var database = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(database))
            return IndexStoreStatus.NotIndexed;

        try
        {
            await using var store = new WorkspaceIndexStore(database);
            if (await store.OpenForReadAsync(cancellationToken) is not WorkspaceIndexReadOpenStatus.Ready)
                return new IndexStoreStatus("unavailable", null, "schema_or_contract_mismatch", 0, 0, 0, 0, null, null);

            var state = await store.GetStateAsync(cancellationToken);
            var manifest = await WorkspaceIndexManifest.ValidateAsync(root, store, cancellationToken);
            var routes = await store.GetRouteCountAsync(cancellationToken);
            var completedAt = await store.GetMetaAsync(WorkspaceIndexManifest.CompletedUtcMetaKey, cancellationToken);
            var jobState = await store.GetMetaAsync("index_job_state", cancellationToken);
            var errorCode = await store.GetMetaAsync("index_job_error_code", cancellationToken);
            var errorMessage = await store.GetMetaAsync("index_job_error_message", cancellationToken);
            var lastFailure = string.Equals(jobState, "failed", StringComparison.Ordinal)
                ? string.IsNullOrWhiteSpace(errorCode)
                    ? errorMessage
                    : $"{errorCode}: {errorMessage}"
                : null;
            return new IndexStoreStatus(
                state.Status.ToString().ToLowerInvariant(),
                state.Mode,
                manifest.Ready ? "ready" : manifest.Detail,
                state.FileCount,
                state.SymbolCount,
                state.ChunkCount,
                routes,
                completedAt,
                lastFailure);
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            return new IndexStoreStatus("unavailable", null, "unreadable", 0, 0, 0, 0, null, null);
        }
    }

    private static long SizeOf(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
