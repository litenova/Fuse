// -----------------------------------------------------------------------
// <copyright file="IContentProcessor.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Fuse.Core;

namespace Fuse.Engine.Services;

/// <summary>
///     Defines the contract for processing and transforming file content.
/// </summary>
/// <remarks>
///     <para>
///         The content processor is responsible for:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>Reading file content from disk</description>
///         </item>
///         <item>
///             <description>Applying content transformations (trimming, condensing)</description>
///         </item>
///         <item>
///             <description>Applying file-type-specific minification</description>
///         </item>
///     </list>
///     <para>
///         This follows the Single Responsibility Principle by extracting content
///         transformation logic from the main engine class.
///     </para>
/// </remarks>
public interface IContentProcessor
{
    /// <summary>
    ///     Processes the content of a file, applying transformations and minification.
    /// </summary>
    /// <param name="fileInfo">Information about the file to process.</param>
    /// <param name="options">The fusion options controlling transformations.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     A task representing the asynchronous operation, containing the processed
    ///     file content as a string.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         The processing steps include:
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             <description>Read file content from disk</description>
    ///         </item>
    ///         <item>
    ///             <description>Trim whitespace if <see cref="FuseOptions.TrimContent" /> is enabled</description>
    ///         </item>
    ///         <item>
    ///             <description>Condense empty lines if <see cref="FuseOptions.UseCondensing" /> is enabled</description>
    ///         </item>
    ///         <item>
    ///             <description>Apply file-type-specific minification based on extension</description>
    ///         </item>
    ///     </list>
    /// </remarks>
    /// <example>
    ///     <code>
    /// var fileInfo = new FileProcessingInfo(fullPath, relativePath, info);
    /// var content = await processor.ProcessContentAsync(fileInfo, options, cancellationToken);
    /// </code>
    /// </example>
    Task<string> ProcessContentAsync(FileProcessingInfo fileInfo, FuseOptions options, CancellationToken cancellationToken);
}