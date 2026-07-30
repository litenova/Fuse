using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Indexing;
using Fuse.Cli.Mcp;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;
using Fuse.Semantics;

namespace Fuse.Cli.Commands;

/// <summary>
///     Reviews the semantic impact of a change: the changed files, the blast radius (callers, DI consumers,
///     route and request handlers, options consumers, tests), and why each non-changed file is included. Reads
///     the persistent index; run <c>index</c> first. Source rendering is added in a later phase.
/// </summary>
[CliCommand(
    Name = "review",
    Description = "Review the semantic impact of a change since a git base ref.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class ReviewCommand
{
    private readonly IConsoleUI _consoleUI;
    private readonly IChangeSource _changeSource;
    private readonly ContentReductionPipeline _reductionPipeline;
    private readonly SemanticIndexer _indexer;
    private readonly FuseMcpRuntime _runtime;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ReviewCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependencies are null, so this instance must not run.</remarks>
    public ReviewCommand() : this(null!, null!, null!, null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ReviewCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for output.</param>
    /// <param name="changeSource">The change source for resolving the git base ref.</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render file bodies.</param>
    /// <param name="indexer">The semantic indexer (opens the store for the handoff packet).</param>
    /// <param name="runtime">The process-owned MCP runtime used for shared index access.</param>
    public ReviewCommand(
        IConsoleUI consoleUI,
        IChangeSource changeSource,
        ContentReductionPipeline reductionPipeline,
        SemanticIndexer indexer,
        FuseMcpRuntime runtime)
    {
        _consoleUI = consoleUI;
        _changeSource = changeSource;
        _reductionPipeline = reductionPipeline;
        _indexer = indexer;
        _runtime = runtime;
    }

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>The git base ref to diff against.</summary>
    [CliOption(Name = "--changed-since", Description = "The git base ref to diff against (branch, commit, or HEAD~N).")]
    public string ChangedSince { get; set; } = "HEAD";

    /// <summary>The token budget.</summary>
    [CliOption(Name = "--max-tokens", Required = false, Description = "Token budget.")]
    public int? MaxTokens { get; set; }

    /// <summary>Whether to include related test files.</summary>
    [CliOption(Name = "--include-tests", Description = "Include related test files.")]
    public bool IncludeTests { get; set; } = true;

    /// <summary>The output format: xml (default), markdown, or json.</summary>
    [CliOption(Description = "Output format: xml (default), markdown, or json.")]
    public string Format { get; set; } = "xml";

    /// <summary>Print only the review preamble and plan, without rendering source bodies.</summary>
    [CliOption(Name = "--plan-only", Description = "Print the preamble and plan without rendering source bodies.")]
    public bool PlanOnly { get; set; }

    /// <summary>Produce a paste-ready PR handoff packet instead of the review context.</summary>
    [CliOption(Name = "--handoff", Description = "Produce a paste-ready PR handoff packet (changed files, API delta, compiler-gate status, residual risk) instead of the review context.")]
    public bool Handoff { get; set; }

    /// <summary>The fuse_check session id the handoff red-gate consults.</summary>
    [CliOption(Name = "--check-session", Description = "For --handoff: the fuse_check session id to gate on (refuses while it has unresolved introduced errors).")]
    public string CheckSession { get; set; } = string.Empty;

    /// <summary>
    ///     Runs the review command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the review has been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);

        // U3 parity: --handoff produces the paste-ready PR packet (the same builder fuse_review --handoff uses),
        // gated by the check session's red state. It opens the index itself, so it does not need a prior index.
        if (Handoff)
        {
            var handoff = await FuseTools.BuildHandoffAsync(
                _indexer,
                _changeSource,
                root,
                ChangedSince,
                CheckSession,
                context.CancellationToken,
                runtime: _runtime);
            _consoleUI.WriteResult(handoff);
            return;
        }

        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
        {
            _consoleUI.WriteError($"No index found at {databasePath}. Run 'fuse index' first.");
            return;
        }

        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(context.CancellationToken);

        var request = new ReviewRequest(root, ChangedSince, MaxTokens: MaxTokens, IncludeTests: IncludeTests);
        var plan = await new SemanticRetrievalEngine(store, _changeSource).ReviewAsync(request, context.CancellationToken);

        if (PlanOnly)
        {
            var summary = new StringBuilder();
            summary.Append(ReviewPreambleBuilder.Build(plan, ChangedSince));
            summary.AppendLine();
            summary.Append(PlanFormatter.Format(plan));
            _consoleUI.WriteResult(summary.ToString());
            return;
        }

        var renderer = new SemanticContextRenderer(_reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, context.CancellationToken);
        var output = SemanticContextEmitter.Emit(plan, rendered, PlanFormatter.ParseFormat(Format), root, ChangedSince);
        _consoleUI.WriteResult(output);
    }
}
