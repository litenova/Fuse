// -----------------------------------------------------------------------
// <copyright file="IFileCollector.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Fuse.Core;

namespace Fuse.Engine.Services;

/// <summary>
/// Represents information about a file to be processed during fusion.
/// </summary>
/// <remarks>
/// This record provides all the metadata needed to process a file,
/// avoiding repeated file system calls during processing.
/// </remarks>
/// <param name="FullPath">The absolute path to the file.</param>
/// <param name="RelativePath">The path relative to the source directory.</param>
/// <param name="Info">The file's metadata (size, dates, etc.).</param>
public record FileProcessingInfo(string FullPath, string RelativePath, FileInfo Info);

/// <summary>
/// Defines the contract for collecting files to be processed during fusion.
/// </summary>
/// <remarks>
/// <para>
/// The file collector is responsible for:
/// </para>
/// <list type="bullet">
///     <item><description>Enumerating files in the source directory</description></item>
///     <item><description>Filtering files by extension, size, and other criteria</description></item>
///     <item><description>Applying .gitignore rules when enabled</description></item>
///     <item><description>Excluding test project directories when requested</description></item>
///     <item><description>Filtering out binary files when configured</description></item>
/// </list>
/// <para>
/// This follows the Single Responsibility Principle by extracting file
/// enumeration and filtering logic from the main engine class.
/// </para>
/// </remarks>
public interface IFileCollector
{
    /// <summary>
    /// Collects all files that match the specified options and configuration.
    /// </summary>
    /// <param name="options">The user-provided fusion options.</param>
    /// <param name="config">The resolved configuration containing extensions and exclusions.</param>
    /// <returns>
    /// A list of <see cref="FileProcessingInfo"/> objects representing the files
    /// to be processed. The list may be empty if no matching files are found.
    /// </returns>
    /// <remarks>
    /// This method uses parallel processing for improved performance when
    /// scanning large directory trees.
    /// </remarks>
    /// <example>
    /// <code>
    /// var options = new FuseOptions { SourceDirectory = @"C:\Projects\MyApp" };
    /// var config = resolver.Resolve(options);
    /// var files = collector.CollectFiles(options, config);
    /// Console.WriteLine($"Found {files.Count} files to process");
    /// </code>
    /// </example>
    List<FileProcessingInfo> CollectFiles(FuseOptions options, ResolvedConfiguration config);
}
