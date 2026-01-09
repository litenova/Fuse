// -----------------------------------------------------------------------
// <copyright file="IConfigurationResolver.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Fuse.Core;

namespace Fuse.Engine.Services;

/// <summary>
/// Represents the resolved configuration settings for a fusion operation.
/// </summary>
/// <remarks>
/// This record contains the final, computed values after merging user options
/// with template defaults and applying any overrides.
/// </remarks>
/// <param name="Extensions">The final list of file extensions to process.</param>
/// <param name="ExcludeDirectories">The final list of directory names to exclude.</param>
/// <param name="ExcludePatterns">The final list of file patterns to exclude.</param>
public record ResolvedConfiguration(
    IReadOnlyCollection<string> Extensions,
    IReadOnlyCollection<string> ExcludeDirectories,
    IReadOnlyCollection<string> ExcludePatterns
);

/// <summary>
/// Defines the contract for resolving and merging configuration options.
/// </summary>
/// <remarks>
/// <para>
/// The configuration resolver is responsible for:
/// </para>
/// <list type="bullet">
///     <item><description>Merging user-specified options with template defaults</description></item>
///     <item><description>Handling the --only-extensions override</description></item>
///     <item><description>Computing the final list of extensions, excluded directories, and patterns</description></item>
/// </list>
/// <para>
/// This follows the Single Responsibility Principle by extracting configuration
/// logic from the main engine class.
/// </para>
/// </remarks>
public interface IConfigurationResolver
{
    /// <summary>
    /// Resolves the final configuration by merging user options with template defaults.
    /// </summary>
    /// <param name="options">The user-provided fusion options.</param>
    /// <returns>
    /// A <see cref="ResolvedConfiguration"/> containing the final computed values
    /// for extensions, excluded directories, and excluded patterns.
    /// </returns>
    /// <example>
    /// <code>
    /// var options = new FuseOptions { Template = ProjectTemplate.DotNet };
    /// var config = resolver.Resolve(options);
    /// // config.Extensions will contain .NET file extensions
    /// // config.ExcludeDirectories will contain ["bin", "obj", ".vs", ".git", ".idea"]
    /// </code>
    /// </example>
    ResolvedConfiguration Resolve(FuseOptions options);
}
