using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Implements <c>fuse_reduce</c>: compact a named set of files or raw content. The one utility outside the
///     loop, because it operates on arbitrary files and raw content rather than the indexed workspace.
/// </summary>
internal static class ReduceToolOperations
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
    internal static Task<string> ExecuteAsync(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        string path = ".",
        string[]? files = null,
        string? content = null,
        string extension = ".cs",
        ReductionLevel level = ReductionLevel.Standard,
        int maxTokens = 0,
        CancellationToken cancellationToken = default) =>
        FuseOperationalErrors.ExecuteMcpAsync(() => ReduceCoreAsync(
            orchestrator, templateRegistry, path, files, content, extension, level, maxTokens, cancellationToken));

    private static Task<string> ReduceCoreAsync(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        string path,
        string[]? files,
        string? content,
        string extension,
        ReductionLevel level,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        int? tokenLimit = maxTokens > 0 ? maxTokens : null;

        if (!string.IsNullOrEmpty(content))
            return ReduceRunner.ReduceContentAsync(orchestrator, templateRegistry, content, extension, level, tokenLimit, cancellationToken);

        if (files is { Length: > 0 })
            return ReduceRunner.ReduceFilesAsync(orchestrator, templateRegistry, path, files, level, tokenLimit, cancellationToken);

        return Task.FromResult(FuseOperationalErrors.Format(
            FuseOperationalErrors.ValidationErrorPrefix,
            "provide either files (paths) or content to reduce."));
    }
}
