using Fuse.Collection.Models;
using Fuse.Collection.Templates;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Shared fusion request builder defaults for CLI commands, MCP tools, and the VS Code host RPC surface.
/// </summary>
public static class FusionRequestComposer
{
    /// <summary>
    ///     Creates an in-memory .NET fusion request builder with MCP-friendly defaults.
    /// </summary>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="resolvedPath">Absolute path to the source directory.</param>
    /// <returns>A preconfigured builder; callers add scope, filters, and emission overrides.</returns>
    public static FusionRequestBuilder CreateDotNetInMemoryBuilder(
        ProjectTemplateRegistry templateRegistry,
        string resolvedPath) =>
        new FusionRequestBuilder(templateRegistry)
            .WithSourceDirectory(resolvedPath)
            .WithTemplate(ProjectTemplate.DotNet)
            .WithInMemory(true)
            .WithAnalysisCache(true)
            .WithEmissionOptions(new EmissionOptions
            {
                MaxTokens = null,
                ShowTokenCount = false,
                IncludeManifest = true,
            })
            .WithReductionOptions(new ReductionOptions(enableRedaction: true));
}
