// -----------------------------------------------------------------------
// <copyright file="IProjectTemplate.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Fuse.Core.Abstractions;

/// <summary>
/// Defines the contract for a project template configuration.
/// </summary>
/// <remarks>
/// <para>
/// Project templates provide predefined settings for file extensions, excluded directories,
/// and excluded file patterns based on the type of project being processed.
/// </para>
/// <para>
/// Implementations of this interface should be immutable and thread-safe.
/// </para>
/// </remarks>
public interface IProjectTemplate
{
    /// <summary>
    /// Gets the collection of file extensions to include in the fusion operation.
    /// </summary>
    /// <value>
    /// A read-only collection of file extensions including the leading dot (e.g., ".cs", ".md").
    /// </value>
    /// <example>
    /// <code>
    /// public IReadOnlyCollection&lt;string&gt; Extensions => new[] { ".cs", ".csproj", ".json" };
    /// </code>
    /// </example>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>
    /// Gets the collection of directory names to exclude from scanning.
    /// </summary>
    /// <value>
    /// A read-only collection of directory names without path separators (e.g., "bin", "obj").
    /// </value>
    /// <example>
    /// <code>
    /// public IReadOnlyCollection&lt;string&gt; ExcludeFolders => new[] { "bin", "obj", ".git" };
    /// </code>
    /// </example>
    IReadOnlyCollection<string> ExcludeFolders { get; }

    /// <summary>
    /// Gets the collection of file patterns to exclude from processing.
    /// </summary>
    /// <value>
    /// A read-only collection of glob patterns (e.g., "*.g.cs", "*.Designer.cs").
    /// </value>
    /// <remarks>
    /// Patterns support standard glob syntax including wildcards (*) and character classes.
    /// </remarks>
    /// <example>
    /// <code>
    /// public IReadOnlyCollection&lt;string&gt; ExcludePatterns => new[] { "*.g.cs", "*.generated.cs" };
    /// </code>
    /// </example>
    IReadOnlyCollection<string> ExcludePatterns { get; }
}
