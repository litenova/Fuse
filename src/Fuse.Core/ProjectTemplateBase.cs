// -----------------------------------------------------------------------
// <copyright file="ProjectTemplateBase.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Fuse.Core.Abstractions;

namespace Fuse.Core;

/// <summary>
/// Provides a base implementation for project templates with common functionality.
/// </summary>
/// <remarks>
/// <para>
/// This abstract class implements <see cref="IProjectTemplate"/> and provides
/// sensible defaults while allowing derived classes to customize behavior.
/// </para>
/// <para>
/// Derived classes must implement <see cref="Extensions"/> and <see cref="ExcludeFolders"/>.
/// The <see cref="ExcludePatterns"/> property returns an empty collection by default
/// but can be overridden for templates with specific pattern exclusions.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class DotNetTemplate : ProjectTemplateBase
/// {
///     public override IReadOnlyCollection&lt;string&gt; Extensions => 
///         new[] { ".cs", ".csproj", ".json" };
///     
///     public override IReadOnlyCollection&lt;string&gt; ExcludeFolders => 
///         new[] { "bin", "obj", ".vs" };
///     
///     public override IReadOnlyCollection&lt;string&gt; ExcludePatterns => 
///         new[] { "*.g.cs", "*.Designer.cs" };
/// }
/// </code>
/// </example>
public abstract class ProjectTemplateBase : IProjectTemplate
{
    /// <inheritdoc />
    /// <summary>
    /// Gets the file extensions to include for this template.
    /// </summary>
    public abstract IReadOnlyCollection<string> Extensions { get; }

    /// <inheritdoc />
    /// <summary>
    /// Gets the directory names to exclude for this template.
    /// </summary>
    public abstract IReadOnlyCollection<string> ExcludeFolders { get; }

    /// <inheritdoc />
    /// <summary>
    /// Gets the file patterns to exclude for this template.
    /// </summary>
    /// <remarks>
    /// Returns an empty collection by default. Override this property
    /// in derived classes to specify template-specific exclusion patterns.
    /// </remarks>
    public virtual IReadOnlyCollection<string> ExcludePatterns => [];
}
