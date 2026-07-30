using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;

namespace Fuse.Cli.Commands;

/// <summary>
///     The CLI find union: exact names, paths, and text; wiring resolution; signatures; graph neighbors; and
///     task localization. It returns index facts and locations only, never source bodies.
/// </summary>
[CliCommand(
    Name = "find",
    Description = "Find indexed code by exact lookup, wiring, graph neighborhood, or task.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class FindCommand
{
    internal const int Limit = 50;
    private readonly IConsoleUI _consoleUI;
    private readonly IChangeSource? _changeSource;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FindCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; dependencies are null, so this instance must not run.</remarks>
    public FindCommand() : this(null!, null)
    {
    }

    /// <summary>Initializes a find command without task-change support.</summary>
    /// <param name="consoleUI">The console output service.</param>
    public FindCommand(IConsoleUI consoleUI) : this(consoleUI, null)
    {
    }

    /// <summary>Initializes a find command.</summary>
    /// <param name="consoleUI">The console output service.</param>
    /// <param name="changeSource">The Git change source used by task localization.</param>
    public FindCommand(IConsoleUI consoleUI, IChangeSource? changeSource)
    {
        _consoleUI = consoleUI;
        _changeSource = changeSource;
    }

    /// <summary>The name, path fragment, text, wiring identifier, or task to find.</summary>
    [CliArgument(Description = "The name, path fragment, text, wiring identifier, or task to find.")]
    public string Query { get; set; } = string.Empty;

    /// <summary>The workspace directory.</summary>
    [CliOption(Required = false, Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>The find behavior to run.</summary>
    [CliOption(Description = "Kind: symbol, path, text, all, service, request, route, config, signatures, neighbors, or task.")]
    public string Kind { get; set; } = "all";

    /// <summary>The Git base used by task localization to seed candidates.</summary>
    [CliOption(Name = "--changed-since", Required = false, Description = "A Git base ref whose changed files seed task candidates.")]
    public string? ChangedSince { get; set; }

    /// <summary>The maximum task candidates or graph neighbors to return.</summary>
    [CliOption(Name = "--max-candidates", Description = "Maximum task candidates or graph neighbors to return.")]
    public int MaxCandidates { get; set; } = Limit;

    /// <summary>Whether task localization must refuse insufficient signal.</summary>
    [CliOption(Name = "--strict", Description = "Refuse task localization with insufficient signal and return its navigation map.")]
    public bool Strict { get; set; }

    /// <summary>Whether task localization should add graph neighbors to candidate files.</summary>
    [CliOption(Name = "--expand", Description = "Add graph neighbors to task candidates.")]
    public bool Expand { get; set; }

    /// <summary>
    ///     Runs the find command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the result has been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Query))
            {
                FuseOperationalErrors.WriteCliError(
                    _consoleUI,
                    FuseOperationalErrors.Format(FuseOperationalErrors.ValidationErrorPrefix, "specify a query to find."));
                return;
            }

            var root = WorkspacePathResolver.ResolveRepositoryRoot(Path);
            var databasePath = FuseStorePaths.ResolveDatabasePath(root);
            if (!File.Exists(databasePath))
            {
                FuseOperationalErrors.WriteCliError(_consoleUI, FuseOperationalErrors.FormatIndexNotBuilt(databasePath));
                return;
            }

            await using var store = await IndexCoordinator.Default.OpenForReadOnlyAsync(root, context.CancellationToken);
            var result = await FindAsync(store, root, context.CancellationToken);
            _consoleUI.WriteResult(result);
        }
        catch (Exception ex)
        {
            FuseOperationalErrors.ReportCli(_consoleUI, ex);
        }
    }

    private async Task<string> FindAsync(
        WorkspaceIndexStore store,
        string root,
        CancellationToken cancellationToken)
    {
        var kind = Kind.Trim().ToLowerInvariant();
        return kind switch
        {
            "all" or "symbol" or "path" or "text" => await FindExactAsync(store, kind, cancellationToken),
            "service" => FormatResolution(await new SemanticResolver(store).ResolveServiceAsync(Query, cancellationToken)),
            "request" => FormatResolution(await new SemanticResolver(store).ResolveRequestAsync(Query, cancellationToken)),
            "route" => FormatResolution(await new SemanticResolver(store).ResolveRouteAsync(Query, cancellationToken)),
            "config" => FormatResolution(await new SemanticResolver(store).ResolveConfigAsync(Query, cancellationToken)),
            "signatures" => await FindSignaturesAsync(store, cancellationToken),
            "neighbors" => await FindNeighborsAsync(store, cancellationToken),
            "task" => await FindTaskAsync(store, root, cancellationToken),
            _ => throw new ArgumentException(
                "kind must be symbol, path, text, all, service, request, route, config, signatures, neighbors, or task."),
        };
    }

    private async Task<string> FindExactAsync(
        WorkspaceIndexStore store,
        string kind,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        if (kind is "all" or "symbol")
        {
            var symbols = await store.FindSymbolsByNameAsync(Query, Limit, cancellationToken);
            builder.AppendLine($"symbols ({symbols.Count}):");
            foreach (var symbol in symbols)
                builder.AppendLine($"  {symbol.Kind,-11} {symbol.FullyQualifiedName}  ({symbol.FilePath}:{symbol.StartLine})");
        }

        if (kind is "all" or "path")
        {
            var files = await store.FindFilesByPathAsync(Query, Limit, cancellationToken);
            builder.AppendLine($"paths ({files.Count}):");
            foreach (var file in files)
                builder.AppendLine($"  {file.NormalizedPath}");
        }

        if (kind is "all" or "text")
        {
            var hits = await store.SearchAsync(new SearchQuery(Query, Limit), cancellationToken);
            builder.AppendLine($"text ({hits.Count}):");
            foreach (var hit in hits)
                builder.AppendLine($"  {hit.Score:F2}  {hit.Name ?? hit.Kind}  ({hit.FilePath}:{hit.StartLine})");
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string> FindSignaturesAsync(WorkspaceIndexStore store, CancellationToken cancellationToken)
    {
        var matches = await store.GetSignaturesByNamesAsync([Query], limitPerName: 5, cancellationToken);
        var builder = new StringBuilder();
        builder.AppendLine($"signatures: {Query}");
        if (matches.Count == 0)
            builder.AppendLine("  no match in the index");
        foreach (var match in matches)
        {
            var signature = string.IsNullOrWhiteSpace(match.Signature)
                ? $"{match.Kind} {match.FullyQualifiedName} (no semantic signature recorded)"
                : $"{match.Accessibility} {match.Signature}";
            builder.AppendLine($"  {signature}  ({match.FilePath}:{match.StartLine})");
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string> FindNeighborsAsync(WorkspaceIndexStore store, CancellationToken cancellationToken)
    {
        var items = await new GraphNeighborhoodExplorer(store).CallersAndImplementersAsync(
            Query,
            Math.Clamp(MaxCandidates, 1, Limit),
            cancellationToken);
        var builder = new StringBuilder();
        builder.AppendLine($"neighbors of {Query}: {items.Count}");
        foreach (var item in items)
            builder.AppendLine($"  {item.Path}{(item.Symbol is null ? string.Empty : "  " + item.Symbol)}  [{item.Reason}]");
        return builder.ToString().TrimEnd();
    }

    private async Task<string> FindTaskAsync(
        WorkspaceIndexStore store,
        string root,
        CancellationToken cancellationToken)
    {
        if (!store.FullTextSearchAvailable)
            return "Task localization needs SQLite FTS5. Use fuse_find with kind=symbol, path, or service, or use native repository search.";

        var result = await new SemanticRetrievalEngine(store, _changeSource).LocalizeAsync(
            new LocalizationRequest(
                root,
                Query: Query,
                ChangedSince: ChangedSince,
                MaxCandidates: Math.Clamp(MaxCandidates, 1, Limit),
                Strict: Strict,
                ExpandGraph: Expand),
            cancellationToken);
        return LocalizationFormatter.Format(result).TrimEnd();
    }

    private static string FormatResolution(ResolveResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"resolve {result.Target.ToString().ToLowerInvariant()}: {result.Query}");
        if (result.Matches.Count == 0)
            builder.AppendLine("  no matches");
        foreach (var match in result.Matches)
        {
            var location = match.FilePath is null ? string.Empty : $"  ({match.FilePath}:{match.StartLine})";
            builder.AppendLine($"  [{match.Relation}] {match.Kind} {match.DisplayName}{location}");
            if (match.Signature is not null)
                builder.AppendLine($"      {match.Signature}");
        }

        return builder.ToString().TrimEnd();
    }
}
