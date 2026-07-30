using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;

namespace Fuse.Cli.Commands;

/// <summary>
///     Prints a map of the indexed workspace: symbols, routes, and summary counts. Reads the persistent
///     index; run <c>index</c> first.
/// </summary>
[CliCommand(
    Name = "map",
    Description = "Print a map of the indexed workspace (symbols, routes, counts).",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class MapCommand
{
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MapCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the console UI is null, so this instance must not run.</remarks>
    public MapCommand() : this(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="MapCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for output.</param>
    public MapCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

    /// <summary>The workspace directory to map. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory to map. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>The detail to include: symbols (default), routes, or all.</summary>
    [CliOption(Description = "Detail to include: symbols (default), routes, or all.")]
    public string Detail { get; set; } = "symbols";

    /// <summary>The maximum rows to show per section.</summary>
    [CliOption(Name = "--max-rows", Description = "Maximum rows per section.")]
    public int MaxRows { get; set; } = 200;

    /// <summary>
    ///     Runs the map command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the map has been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
        {
            _consoleUI.WriteError($"No index found at {databasePath}. Run 'fuse index' first.");
            return;
        }

        if (!TryParseDetail(Detail, out var detail))
        {
            _consoleUI.WriteError($"Unknown detail '{Detail}'. Use symbols, routes, or all.");
            return;
        }

        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(context.CancellationToken);

        var renderer = new WorkspaceMapRenderer(store, store);
        var map = await renderer.RenderAsync(detail, MaxRows, context.CancellationToken);
        _consoleUI.WriteResult(map);
    }

    private static bool TryParseDetail(string value, out MapDetail detail)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "symbols" or "":
                detail = MapDetail.Symbols;
                return true;
            case "routes":
                detail = MapDetail.Routes;
                return true;
            case "all":
                detail = MapDetail.All;
                return true;
            default:
                detail = MapDetail.Symbols;
                return false;
        }
    }
}
