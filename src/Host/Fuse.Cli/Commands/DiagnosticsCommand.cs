using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;

namespace Fuse.Cli.Commands;

/// <summary>
///     Reports the state of a workspace's index: schema version, mode, status, record counts, full-text search
///     availability, and the database location. Useful for diagnosing a missing or stale index. Mirrors
///     <c>fuse_workspace action=status</c> on the fast read-only path (R16).
/// </summary>
[CliCommand(
    Name = "diagnostics",
    Description = "Report the index state for a workspace (schema, mode, counts, store location).",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class DiagnosticsCommand
{
    private readonly IConsoleUI _consoleUI;
    private readonly IndexCoordinator _indexCoordinator;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiagnosticsCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the console UI is null, so this instance must not run.</remarks>
    public DiagnosticsCommand() : this(null!, new IndexCoordinator())
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiagnosticsCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for output.</param>
    public DiagnosticsCommand(IConsoleUI consoleUI) : this(consoleUI, new IndexCoordinator())
    {
    }

    /// <summary>Initializes a diagnostics command with its host-owned index coordinator.</summary>
    /// <param name="consoleUI">The console UI for output.</param>
    /// <param name="indexCoordinator">The host-owned coordinator used to open the committed index.</param>
    public DiagnosticsCommand(IConsoleUI consoleUI, IndexCoordinator indexCoordinator)
    {
        _consoleUI = consoleUI;
        _indexCoordinator = indexCoordinator;
    }

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>
    ///     Runs the diagnostics command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the diagnostics have been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        try
        {
            var root = System.IO.Path.GetFullPath(Path);
            if (!Directory.Exists(root))
            {
                FuseOperationalErrors.WriteCliError(_consoleUI, FuseOperationalErrors.FormatWorkspaceNotFound(root));
                return;
            }

            var databasePath = FuseStorePaths.ResolveDatabasePath(root);
            var builder = new StringBuilder();
            builder.AppendLine($"workspace: {root}");
            builder.AppendLine($"store: {databasePath}");

            if (!File.Exists(databasePath))
            {
                builder.AppendLine("index_state: not_indexed");
                builder.AppendLine("index: not built (run 'fuse index')");
                _consoleUI.WriteResult(builder.ToString());
                return;
            }

            await using var store = await _indexCoordinator.OpenForReadOnlyAsync(root, context.CancellationToken);
            var state = await store.GetStateAsync(context.CancellationToken);
            var indexedBy = await store.GetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, context.CancellationToken);
            var indexState = state.FileCount == 0
                ? "not_indexed"
                : await ComputeIndexStateAsync(store, state, context.CancellationToken);

            builder.AppendLine($"index_state: {indexState}");
            builder.AppendLine($"fuse version: {FuseBuildInfo.Current}");
            builder.AppendLine($"indexed by: {indexedBy ?? "(unknown; pre-stamp or rebuilt)"}");
            builder.AppendLine($"schema version: {state.SchemaVersion}");
            builder.AppendLine($"status: {state.Status}");
            builder.AppendLine($"index mode: {state.Mode ?? "(never indexed)"}");
            builder.AppendLine($"files: {state.FileCount}");
            builder.AppendLine($"symbols: {state.SymbolCount}");
            builder.AppendLine($"full-text search: {(state.FtsAvailable ? "available" : "unavailable")}");

            _consoleUI.WriteResult(builder.ToString());
        }
        catch (Exception ex)
        {
            FuseOperationalErrors.ReportCli(_consoleUI, ex);
        }
    }

    private static async Task<string> ComputeIndexStateAsync(
        WorkspaceIndexStore store, WorkspaceIndexState state, CancellationToken cancellationToken)
    {
        if (state.FileCount == 0)
            return "not_indexed";

        var pending = await store.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, cancellationToken);
        if (pending == "1")
        {
            var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
            return mode == "syntax" ? "building_syntax" : "upgrade_pending";
        }

        var staleRaw = await store.GetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, cancellationToken);
        if (int.TryParse(staleRaw, out var stale) && stale > 0)
            return "stale_as_of";

        return "ready";
    }
}
