// -----------------------------------------------------------------------
// <copyright file="FuseTools.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.ComponentModel;
using Fuse.Core;
using Fuse.Engine;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP tool definitions for Fuse.
/// </summary>
/// <remarks>
///     <para>
///         These tools are exposed to AI agents via the Model Context Protocol,
///         allowing them to request optimized, minified context from a codebase
///         with granular control over filtering and processing options.
///     </para>
/// </remarks>
[McpServerToolType]
public sealed class FuseTools
{
    /// <summary>
    ///     Gets optimized, minified context from a codebase for AI consumption.
    /// </summary>
    /// <param name="engine">The fusion engine (injected via DI).</param>
    /// <param name="path">Absolute or relative path to the directory to scan.</param>
    /// <param name="template">
    ///     Project template to use for default extensions and exclusions.
    ///     Valid values: DotNet, Python, Node, Generic, AzureDevOpsWiki. Defaults to null (generic).
    /// </param>
    /// <param name="maxTokens">
    ///     Hard limit on the number of tokens in the output.
    ///     When reached, processing stops. Defaults to no limit.
    /// </param>
    /// <param name="includeExtensions">
    ///     Comma-separated list of file extensions to process exclusively (e.g., ".cs,.razor,.json").
    ///     When specified, overrides template defaults.
    /// </param>
    /// <param name="aggressiveMinification">
    ///     When true, applies aggressive C# reduction (removes attributes, compresses auto-properties, etc.).
    ///     Only effective with the DotNet template.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fused, minified content as a string.</returns>
    [McpServerTool(Name = "get_optimized_context", ReadOnly = true)]
    [Description("Generates optimized, minified context from a codebase directory. " +
                 "Returns XML-formatted file contents with paths, suitable for AI consumption. " +
                 "Supports filtering by template, extensions, and token limits.")]
    public static async Task<string> GetOptimizedContextAsync(
        FuseEngine engine,
        [Description("Absolute or relative path to the directory to scan.")] string path,
        [Description("Project template: DotNet, Python, Node, Generic, or AzureDevOpsWiki. Leave empty for generic.")] string? template = null,
        [Description("Maximum token count for the output. Omit for no limit.")] int? maxTokens = null,
        [Description("Comma-separated file extensions to include exclusively (e.g. '.cs,.razor'). Overrides template defaults.")] string? includeExtensions = null,
        [Description("Apply aggressive C# reduction (remove attributes, compress properties). Only for DotNet template.")] bool aggressiveMinification = false,
        CancellationToken cancellationToken = default)
    {
        // Resolve the path to an absolute path
        var resolvedPath = Path.GetFullPath(path);

        if (!Directory.Exists(resolvedPath))
        {
            return $"Error: Directory not found: {resolvedPath}";
        }

        // Parse template
        ProjectTemplate? parsedTemplate = null;
        if (!string.IsNullOrWhiteSpace(template))
        {
            if (Enum.TryParse<ProjectTemplate>(template, ignoreCase: true, out var t))
            {
                parsedTemplate = t;
            }
            else
            {
                return $"Error: Unknown template '{template}'. Valid values: {string.Join(", ", Enum.GetNames<ProjectTemplate>())}";
            }
        }

        // Parse extensions
        string[]? onlyExtensions = null;
        if (!string.IsNullOrWhiteSpace(includeExtensions))
        {
            onlyExtensions = includeExtensions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.StartsWith('.') ? e : $".{e}")
                .ToArray();
        }

        var options = new FuseOptions
        {
            SourceDirectory = resolvedPath,
            Template = parsedTemplate,
            OnlyExtensions = onlyExtensions,
            MaxTokens = maxTokens,
            AggressiveCSharpReduction = aggressiveMinification,

            // Sensible defaults for MCP
            Recursive = true,
            TrimContent = true,
            UseCondensing = true,
            MinifyXmlFiles = true,
            MinifyHtmlAndRazor = true,
            RespectGitIgnore = true,
            IgnoreBinaryFiles = true,
            ShowTokenCount = false,
            TrackTopTokenFiles = true
        };

        try
        {
            var result = await engine.FuseInMemoryAsync(options, cancellationToken);

            if (result.GeneratedPaths.Count == 0)
            {
                return "No files found matching the criteria.";
            }

            // The in-memory builder stores content as the first GeneratedPaths entry
            var content = result.GeneratedPaths[0];

            // Append a brief stats footer
            var statsLine = $"\n<!-- Fuse: {result.ProcessedFileCount}/{result.TotalFileCount} files | ~{result.TotalTokens} tokens | {result.Duration.TotalSeconds:F1}s -->";
            return content + statsLine;
        }
        catch (Exception ex)
        {
            return $"Error during fusion: {ex.Message}";
        }
    }
}
