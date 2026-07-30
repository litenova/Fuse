using System.ComponentModel;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The <c>fuse_reduce</c> MCP tool: compact named files or raw content without using the workspace index.
/// </summary>
[McpServerToolType]
internal sealed class FuseReduceHandler
{
    /// <summary>Reduces the requested files or raw content.</summary>
    /// <param name="orchestrator">The fusion orchestrator.</param>
    /// <param name="templateRegistry">The project template registry.</param>
    /// <param name="path">Base directory for resolving relative file paths; ignored in content mode.</param>
    /// <param name="files">Explicit file paths to reduce.</param>
    /// <param name="content">Raw content to reduce instead of files.</param>
    /// <param name="extension">The extension selecting the reducer for content.</param>
    /// <param name="level">The reduction level.</param>
    /// <param name="maxTokens">The token ceiling, or zero for none.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>The reduced output, or a descriptive error.</returns>
    [McpServerTool(Name = "fuse_reduce", ReadOnly = true)]
    [Description("Compact a specific set of files (or raw content) by running Fuse's reduction, without collecting a whole directory. Pass `files` or `content` (+ `extension`).")]
    public static Task<string> ExecuteAsync(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        [Description("Base directory for resolving relative file paths. Ignored in content mode.")] string path = ".",
        [Description("File paths to reduce, absolute or relative to path.")] string[]? files = null,
        [Description("Raw content to reduce instead of files. Provide extension to select the reducer.")] string? content = null,
        [Description("Extension that selects the reducer for content (for example .cs, .ts, .py). Defaults to .cs.")] string extension = ".cs",
        [Description("Reduction level: none, standard, aggressive, skeleton, publicApi. Defaults to standard.")] ReductionLevel level = ReductionLevel.Standard,
        [Description("Maximum tokens the reduced output may use, or 0 for no limit.")] int maxTokens = 0,
        CancellationToken cancellationToken = default) =>
        ReduceToolOperations.ExecuteAsync(
            orchestrator, templateRegistry, path, files, content, extension, level, maxTokens, cancellationToken);
}
