using System.ComponentModel;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The <c>fuse_impact</c> MCP tool: the blast radius for a symbol, and the NuGet package-upgrade break set.
/// </summary>
[McpServerToolType]
internal sealed class FuseImpactHandler
{
    /// <summary>Computes the blast radius for a symbol, or a package-upgrade break set.</summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="symbol">The symbol whose blast radius to compute.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="limit">The maximum impacted items to return.</param>
    /// <param name="package">The optional NuGet package id for package-upgrade analysis.</param>
    /// <param name="fromVersion">The installed NuGet package version for package-upgrade analysis.</param>
    /// <param name="toVersion">The target NuGet package version for package-upgrade analysis.</param>
    /// <param name="session">The optional claim-ledger session id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The impacted files and symbols with the edge that connects them, plus an availability note.</returns>
    [McpServerTool(Name = "fuse_impact", ReadOnly = true)]
    [Description("Blast radius for a symbol before you edit it: the callers, implementers, consumers, and referencing types a change would touch, from the persisted semantic graph. No bodies. The exact signature-change break set (which call sites would no longer bind) needs an oracle-grade (tier-1) load and is reported unavailable otherwise, rather than guessed. Package-upgrade mode (F3): pass package + fromVersion + toVersion to get the public-API break set between two cached NuGet package versions (removed/changed public members), so a bump's risk is knowable before the lockfile changes; it abstains when a version is not in the local cache and names its blind spots.")]
    public static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        [Description("The symbol (simple or qualified name) whose blast radius to compute.")] string symbol = "",
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Maximum impacted items to return.")] int limit = 50,
        [Description("Package-upgrade mode: the NuGet package id whose bump to analyze.")] string package = "",
        [Description("Package-upgrade mode: the currently referenced version.")] string fromVersion = "",
        [Description("Package-upgrade mode: the target (upgrade) version.")] string toVersion = "",
        [Description("Optional session id: when set, this call's graded claims are appended to the session's claim ledger (U2).")] string session = "",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        ImpactToolOperations.ExecuteAsync(
            indexer, symbol, path, limit, package, fromVersion, toVersion, session, cancellationToken, runtime);
}
