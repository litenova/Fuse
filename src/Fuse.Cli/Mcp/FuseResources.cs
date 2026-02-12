// -----------------------------------------------------------------------
// <copyright file="FuseResources.cs" company="Fuse">
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
///     MCP resource definitions for Fuse.
/// </summary>
/// <remarks>
///     <para>
///         Resources expose the codebase as readable URIs to AI agents.
///         The <c>fuse://</c> URI scheme allows agents to request the full
///         optimized context of a project or specific subdirectory.
///     </para>
///     <para>
///         URI pattern: <c>fuse://{template}/{path}</c>
///     </para>
///     <example>
///         <list type="bullet">
///             <item><c>fuse://dotnet/src</c> — Fuse the <c>src</c> folder using the DotNet template</item>
///             <item><c>fuse://generic/.</c> — Fuse the current directory with no template</item>
///         </list>
///     </example>
/// </remarks>
[McpServerResourceType]
public sealed class FuseResources
{
    /// <summary>
    ///     Reads the fused content for a given template and path.
    /// </summary>
    /// <param name="engine">The fusion engine (injected via DI).</param>
    /// <param name="template">The project template name (e.g., dotnet, python, generic).</param>
    /// <param name="path">The relative path to scan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fused XML content.</returns>
    [McpServerResource(
        UriTemplate = "fuse://{template}/{path}",
        Name = "Fused Codebase Context",
        MimeType = "text/plain")]
    [System.ComponentModel.Description("Returns the optimized, minified content of a codebase directory. " +
                      "Use template 'dotnet', 'python', 'node', 'generic', or 'azuredevopswiki'. " +
                      "Path is relative to the working directory.")]
    public static async Task<string> ReadFuseResourceAsync(
        FuseEngine engine,
        [System.ComponentModel.Description("The project template (dotnet, python, node, generic, azuredevopswiki).")] string template,
        [System.ComponentModel.Description("Relative path to the directory to fuse.")] string path,
        CancellationToken cancellationToken = default)
    {
        // Resolve path
        var resolvedPath = Path.GetFullPath(path);

        if (!Directory.Exists(resolvedPath))
        {
            return $"Error: Directory not found: {resolvedPath}";
        }

        // Parse template
        ProjectTemplate? parsedTemplate = null;
        if (!string.Equals(template, "generic", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(template))
        {
            if (Enum.TryParse<ProjectTemplate>(template, ignoreCase: true, out var t))
            {
                parsedTemplate = t;
            }
            else
            {
                return $"Error: Unknown template '{template}'. Valid values: generic, {string.Join(", ", Enum.GetNames<ProjectTemplate>())}";
            }
        }

        var options = new FuseOptions
        {
            SourceDirectory = resolvedPath,
            Template = parsedTemplate,
            Recursive = true,
            TrimContent = true,
            UseCondensing = true,
            MinifyXmlFiles = true,
            MinifyHtmlAndRazor = true,
            RespectGitIgnore = true,
            IgnoreBinaryFiles = true,
            ShowTokenCount = false
        };

        try
        {
            var result = await engine.FuseInMemoryAsync(options, cancellationToken);

            if (result.GeneratedPaths.Count == 0)
            {
                return "No files found matching the criteria.";
            }

            return result.GeneratedPaths[0];
        }
        catch (Exception ex)
        {
            return $"Error during fusion: {ex.Message}";
        }
    }
}
