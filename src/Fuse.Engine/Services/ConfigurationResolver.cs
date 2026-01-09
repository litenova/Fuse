// -----------------------------------------------------------------------
// <copyright file="ConfigurationResolver.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Fuse.Core;

namespace Fuse.Engine.Services;

/// <summary>
/// Resolves and merges configuration options from user input and template defaults.
/// </summary>
/// <remarks>
/// <para>
/// This service implements the configuration resolution logic following these rules:
/// </para>
/// <list type="number">
///     <item><description>If <see cref="FuseOptions.OnlyExtensions"/> is set, it overrides all template/include settings</description></item>
///     <item><description>If a template is specified, its defaults are used as a baseline</description></item>
///     <item><description>User-specified includes/excludes are merged with template defaults</description></item>
///     <item><description>If no template is specified, defaults to all files (*.*)</description></item>
/// </list>
/// </remarks>
public sealed class ConfigurationResolver : IConfigurationResolver
{
    /// <inheritdoc />
    /// <summary>
    /// Resolves the final configuration by applying the configuration resolution rules.
    /// </summary>
    public ResolvedConfiguration Resolve(FuseOptions options)
    {
        // Rule 1: If --only-extensions is used, it overrides everything else
        // This provides a way to bypass template defaults entirely
        if (options.OnlyExtensions?.Any() == true)
        {
            return new ResolvedConfiguration(
                options.OnlyExtensions,
                options.ExcludeDirectories ?? [],
                []
            );
        }

        // Rule 2: If no template is specified, use generic defaults
        if (!options.Template.HasValue)
        {
            return new ResolvedConfiguration(
                options.IncludeExtensions ?? ["*.*"], // Default to all files if no template
                options.ExcludeDirectories ?? [],
                []
            );
        }

        // Rule 3: Template is specified - get its defaults
        var template = ProjectTemplateRegistry.GetTemplate(options.Template.Value);
        var patterns = ProjectTemplateRegistry.GetExcludedPatterns(options.Template.Value);

        // Start with template's default extensions
        var extensions = template.Extensions.ToList();
        
        // Start with template's default excluded directories
        var excludeDirectories = template.ExcludeFolders.ToList();

        // Remove any user-specified exclusions from extensions
        if (options.ExcludeExtensions != null)
        {
            extensions = extensions.Except(options.ExcludeExtensions).ToList();
        }

        // Add any user-specified additional extensions
        if (options.IncludeExtensions != null)
        {
            extensions.AddRange(options.IncludeExtensions);
        }

        // Add any user-specified additional directories to exclude
        if (options.ExcludeDirectories != null)
        {
            excludeDirectories.AddRange(options.ExcludeDirectories);
        }

        // Return the merged configuration with duplicates removed
        return new ResolvedConfiguration(
            extensions.Distinct().ToArray(),
            excludeDirectories.Distinct().ToArray(),
            patterns.ToArray()
        );
    }
}
