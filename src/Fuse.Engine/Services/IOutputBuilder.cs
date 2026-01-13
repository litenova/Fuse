// -----------------------------------------------------------------------
// <copyright file="IOutputBuilder.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Fuse.Core;

namespace Fuse.Engine.Services;

/// <summary>
///     Defines the contract for building the final fused output file.
/// </summary>
/// <remarks>
///     <para>
///         The output builder is responsible for:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>Creating the output file stream</description>
///         </item>
///         <item>
///             <description>Processing each file and writing formatted content</description>
///         </item>
///         <item>
///             <description>Adding file markers and optional metadata</description>
///         </item>
///         <item>
///             <description>Tracking and optionally limiting token count</description>
///         </item>
///         <item>
///             <description>Displaying progress and final statistics</description>
///         </item>
///     </list>
///     <para>
///         This follows the Single Responsibility Principle by extracting output
///         generation logic from the main engine class.
///     </para>
/// </remarks>
public interface IOutputBuilder
{
    /// <summary>
    ///     Builds the fused output file from the collected files.
    /// </summary>
    /// <param name="files">The list of files to include in the output.</param>
    /// <param name="options">The fusion options controlling output generation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous build operation.</returns>
    /// <remarks>
    ///     <para>
    ///         The output format for each file is:
    ///     </para>
    ///     <code>
    /// &lt;|relative/path/to/file.ext|&gt;
    /// [Size: 1234 bytes | Modified: 2024-01-15 10:30:00]  (if IncludeMetadata is true)
    /// ... file content ...
    /// &lt;|relative/path/to/file.ext|&gt;
    /// </code>
    ///     <para>
    ///         If <see cref="FuseOptions.MaxTokens" /> is set, processing will stop
    ///         when the token limit is reached.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    /// var files = collector.CollectFiles(options, config);
    /// await outputBuilder.BuildOutputAsync(files, options, cancellationToken);
    /// </code>
    /// </example>
    Task BuildOutputAsync(List<FileProcessingInfo> files, FuseOptions options, CancellationToken cancellationToken);
}