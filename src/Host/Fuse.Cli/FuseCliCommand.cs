using DotMake.CommandLine;
using Fuse.Cli.Services;

namespace Fuse.Cli;

/// <summary>
///     Root Fuse CLI command. Fuse connects AI coding agents to local compiler-backed verification and typed
///     .NET wiring; the root prints connection guidance and the primary verified-edit outcomes.
/// </summary>
[CliCommand(Description = "Fuse: local compiler-backed verification and typed .NET wiring for AI agents.")]
public class FuseCliCommand
{
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseCliCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the console UI is null, so this instance must not run.</remarks>
    public FuseCliCommand() : this(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseCliCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for guidance output.</param>
    public FuseCliCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

    /// <summary>
    ///     Prints first-run connection guidance and the verified-edit workflow when no subcommand is given.
    /// </summary>
    /// <param name="context">The CLI invocation context.</param>
    public void Run(CliContext context)
    {
        _consoleUI.WriteResult(
            """
            Fuse connects AI coding agents to the local .NET compiler and typed wiring graph.

            Install and connect:
              dotnet tool install -g Fuse                 Install the global tool.
              fuse mcp install --client codex             Connect Codex for this project.
              fuse mcp install --client all               Connect all supported clients.

            Agent outcomes:
              fuse_check       Typecheck a proposed edit: oracle, local build, or abstain.
              fuse_impact      Compute callers and typed .NET wiring affected by a change.
              fuse_test        Run only the covering tests for a symbol.
              fuse_review      Pack changed code and semantic impact for review.
              fuse_refactor    Stage a compiler-executed refactor only when it verifies.

            CLI shortcuts:
              fuse check --delta [path]          Read edit diagnostics from a resident host.
              fuse impact <symbol>               Compute a symbol's blast radius.
              fuse test <symbol>                 Run a symbol's covering tests.
              fuse review --changed-since <ref>  Review a change since a git ref.

            Context infrastructure:
              fuse index [path]                  Build the persistent syntax index.
              fuse find <query> --kind <kind>    Find symbols, wiring, paths, or tasks.
              fuse context --seed <symbol>       Emit scoped, reduced source context.
              fuse map [path]                    Print symbols, routes, and counts.

            Run 'fuse <command> --help' for options. Connected agents launch 'fuse mcp serve'.
            """);
    }
}
