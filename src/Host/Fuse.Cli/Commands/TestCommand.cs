using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;
using Fuse.Semantics;
using Fuse.Workspace;

namespace Fuse.Cli.Commands;

/// <summary>
///     Runs the covering tests for a symbol (T1): the tests that reach it through the persisted <c>tests</c> edges,
///     run at build grade (<c>dotnet test</c> scoped by filter to just those test types, the whole suite never
///     run), with per-test verdicts. The CLI counterpart of the <c>fuse_test</c> MCP tool. Reads the persistent
///     index; run <c>fuse index</c> first.
/// </summary>
[CliCommand(
    Name = "test",
    Description = "Run the covering tests for a symbol (the tests that reach it via the persisted tests edges) at build grade, scoped so the whole suite never runs.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class TestCommand
{
    private readonly IConsoleUI _consoleUI;

    /// <summary>Initializes a new instance of the <see cref="TestCommand" /> class for CLI option binding only.</summary>
    public TestCommand() : this(null!)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TestCommand" /> class.</summary>
    /// <param name="consoleUI">The console UI for output.</param>
    public TestCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

    /// <summary>The symbol whose covering tests to run.</summary>
    [CliArgument(Description = "The symbol whose covering tests to run.")]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The workspace directory.</summary>
    [CliOption(Required = false, Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>The maximum covering test types to run.</summary>
    [CliOption(Required = false, Description = "Maximum covering test types to run.")]
    public int Limit { get; set; } = 20;

    /// <summary>
    ///     Runs the covering tests for the symbol.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the run finishes.</returns>
    public async Task RunAsync(CliContext context)
    {
        if (string.IsNullOrWhiteSpace(Symbol))
        {
            _consoleUI.WriteError("Specify a symbol whose covering tests to run.");
            return;
        }

        var root = WorkspacePathResolver.ResolveRepositoryRoot(Path);
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
        {
            _consoleUI.WriteError($"No index found at {databasePath}. Run 'fuse index' first.");
            return;
        }

        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(context.CancellationToken);

        var covering = await new GraphNeighborhoodExplorer(store).CoveringTestsAsync(Symbol, Limit, context.CancellationToken);
        if (covering.Count == 0)
        {
            _consoleUI.WriteResult($"covering tests for {Symbol}: none (no tests edge reaches it; selection-only floor, nothing to run).");
            return;
        }

        var scopedRun = await new ProjectScopedCoveringTestRunner().RunAsync(
            root,
            covering
                .Where(item => !string.IsNullOrWhiteSpace(item.Symbol))
                .Select(item => new CoveredTestSelection(item.Path, item.Symbol!))
                .ToList(),
            TimeSpan.FromMinutes(10),
            context.CancellationToken);
        _consoleUI.WriteResult(Render(Symbol, root, scopedRun));
    }

    private static string Render(string symbol, string root, ProjectScopedTestRun scopedRun)
    {
        var builder = new StringBuilder();
        if (scopedRun.ProjectRuns.Count == 0)
        {
            var unowned = scopedRun.UnownedTestFiles.Count == 0
                ? "no selected test type had an owning project"
                : $"{scopedRun.UnownedTestFiles.Count} selected test file(s) had no owning project: {string.Join(", ", scopedRun.UnownedTestFiles)}";
            builder.AppendLine($"covering tests for {symbol}: {scopedRun.SelectedTestTypes.Count} test type(s) selected, but {unowned} (selection-only).");
            return builder.ToString().TrimEnd();
        }

        var results = scopedRun.ProjectRuns.SelectMany(run => run.Result.Verdicts).ToList();
        var passed = results.Count(verdict => verdict.Outcome == "passed");
        var failed = results.Count(verdict => verdict.Outcome == "failed");
        var notRun = results.Count(verdict => verdict.Outcome == "not-run");
        builder.AppendLine($"verification grade: build (ran dotnet test scoped to the covering tests in {scopedRun.ProjectRuns.Count} owning project(s); the emit fast path is future work)");
        foreach (var run in scopedRun.ProjectRuns)
        {
            var project = System.IO.Path.GetRelativePath(root, run.ProjectPath).Replace('\\', '/');
            if (run.Result.TimedOut)
                builder.AppendLine($"  {project}: timed out and the test host was killed.");
            else if (run.Result.Diagnostics is not null)
                builder.AppendLine($"  {project}: {run.Result.Diagnostics}");
        }

        if (scopedRun.UnownedTestFiles.Count > 0)
            builder.AppendLine($"selection-only: {scopedRun.UnownedTestFiles.Count} selected test file(s) had no owning project: {string.Join(", ", scopedRun.UnownedTestFiles)}");

        builder.AppendLine($"covering tests for {symbol}: {scopedRun.SelectedTestTypes.Count} test type(s), {results.Count} test(s) run - {passed} passed, {failed} failed, {notRun} not-run");
        foreach (var verdict in results.OrderBy(verdict => verdict.Outcome == "failed" ? 0 : 1).ThenBy(verdict => verdict.Name, StringComparer.Ordinal))
            builder.AppendLine($"  {verdict.Outcome} {verdict.Name}");

        var notRunnable = CoveringRunAnalysis.NotRunnableTypes(scopedRun.SelectedTestTypes, results);
        if (notRunnable.Count > 0)
        {
            builder.AppendLine($"not-runnable ({notRunnable.Count}; selected but produced no result):");
            foreach (var type in notRunnable)
                builder.AppendLine($"  {type}");
        }

        return builder.ToString().TrimEnd();
    }
}
