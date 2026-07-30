using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Semantics;

namespace Fuse.Cli.Commands;

/// <summary>
///     Computes the blast radius for a symbol before an edit: the callers, implementers, consumers, and referencing
///     types a change would touch, from the persisted semantic graph. The CLI counterpart of the <c>fuse_impact</c>
///     MCP tool (U3 parity). Package-upgrade mode diffs two cached NuGet package versions' public API. Builds the
///     index on first use.
/// </summary>
[CliCommand(
    Name = "impact",
    Description = "Blast radius for a symbol before you edit it (callers, implementers, consumers, referencing types), from the persisted graph. Package-upgrade mode diffs two cached NuGet versions.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class ImpactCommand
{
    private readonly IConsoleUI _consoleUI;
    private readonly SemanticIndexer _indexer;
    private readonly FuseMcpRuntime _runtime;

    /// <summary>Initializes a new instance of the <see cref="ImpactCommand" /> class for CLI option binding only.</summary>
    public ImpactCommand() : this(null!, null!, null!)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ImpactCommand" /> class.</summary>
    /// <param name="consoleUI">The console UI for output.</param>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="runtime">The process-owned MCP runtime used for shared index access.</param>
    public ImpactCommand(IConsoleUI consoleUI, SemanticIndexer indexer, FuseMcpRuntime runtime)
    {
        _consoleUI = consoleUI;
        _indexer = indexer;
        _runtime = runtime;
    }

    /// <summary>The symbol whose blast radius to compute.</summary>
    [CliArgument(Description = "The symbol (simple or qualified name) whose blast radius to compute.")]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliOption(Required = false, Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>The maximum impacted items to return.</summary>
    [CliOption(Required = false, Description = "Maximum impacted items to return.")]
    public int Limit { get; set; } = 50;

    /// <summary>Package-upgrade mode: the NuGet package id whose bump to analyze.</summary>
    [CliOption(Name = "--package", Required = false, Description = "Package-upgrade mode: the NuGet package id whose bump to analyze.")]
    public string Package { get; set; } = string.Empty;

    /// <summary>Package-upgrade mode: the currently referenced version.</summary>
    [CliOption(Name = "--from-version", Required = false, Description = "Package-upgrade mode: the currently referenced version.")]
    public string FromVersion { get; set; } = string.Empty;

    /// <summary>Package-upgrade mode: the target (upgrade) version.</summary>
    [CliOption(Name = "--to-version", Required = false, Description = "Package-upgrade mode: the target (upgrade) version.")]
    public string ToVersion { get; set; } = string.Empty;

    /// <summary>
    ///     Runs the impact command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the blast radius has been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        if (string.IsNullOrWhiteSpace(Symbol) && string.IsNullOrWhiteSpace(Package))
        {
            _consoleUI.WriteError("Specify a symbol whose blast radius to compute, or --package with --from-version and --to-version.");
            return;
        }

        var output = await FuseTools.FuseImpactAsync(
            _indexer, Symbol, Path, Limit, Package, FromVersion, ToVersion, session: "", context.CancellationToken,
            runtime: _runtime);
        _consoleUI.WriteResult(output);
    }
}
