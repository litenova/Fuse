using System.ComponentModel;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The <c>fuse_test</c> MCP tool: run a symbol's covering tests, or race bounded candidate edits.
/// </summary>
[McpServerToolType]
internal sealed class FuseTestHandler
{
    /// <summary>Runs the covering tests for a symbol, or races candidate edits.</summary>
    /// <param name="indexer">The semantic indexer (opens the store for covering selection).</param>
    /// <param name="symbol">The symbol whose covering tests to run.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="limit">The maximum covering test types to run.</param>
    /// <param name="candidates">A JSON array of single-file edits to race, each <c>{id?, file, content}</c>.</param>
    /// <param name="maxCandidates">The bound on the number of candidates a race accepts.</param>
    /// <param name="analyzers">Whether a race also runs the repository's configured analyzers.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The per-test verdicts plus the grade, or the selection-only floor when nothing covers the symbol.</returns>
    [McpServerTool(Name = "fuse_test", ReadOnly = true)]
    [Description("Run the covering tests for a symbol: the tests that reach it through the persisted tests edges, run at build grade (dotnet test scoped by filter to just those test types, the whole suite never run), with per-test verdicts. Selection-only when no tests edge reaches the symbol. Build-grade runs the real build; the emit fast path is future work. Candidate racing (F2): pass candidates (a JSON array of {id?, file, content} single-file edits, bounded k) to speculatively typecheck all of them over the live resident compilation and get per-candidate diagnostics plus a winner by strict dominance (a lone clean candidate beats any with errors; ties reported); each candidate reuses the shared held compilation (only its own changed file rebinds), racing needs a resident workspace (FUSE_RESIDENT=1) and never applies a candidate.")]
    public static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        [Description("The symbol whose covering tests to run.")] string symbol = "",
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Maximum covering test types to run.")] int limit = 20,
        [Description("Candidate racing (F2): a JSON array of single-file edits to race, each {id?, file, content}. When set, races them through the speculative typecheck instead of running the covering tests.")] string candidates = "",
        [Description("Candidate racing: the maximum number of candidates accepted (the bound on k; default 4).")] int maxCandidates = 4,
        [Description("Candidate racing: also run the repo's configured analyzers against each candidate overlay (CI parity). Default on.")] bool analyzers = true,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        TestToolOperations.ExecuteAsync(
            indexer, symbol, path, limit, candidates, maxCandidates, analyzers, cancellationToken, runtime);
}
