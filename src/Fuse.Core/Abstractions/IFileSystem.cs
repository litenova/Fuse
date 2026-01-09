// -----------------------------------------------------------------------
// <copyright file="IFileSystem.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Fuse.Core.Abstractions;

/// <summary>
/// Defines an abstraction for file system operations.
/// </summary>
/// <remarks>
/// <para>
/// This interface provides a testable abstraction over <see cref="System.IO"/> operations.
/// It enables unit testing of file-dependent code without touching the actual file system.
/// </para>
/// <para>
/// The primary implementation is <c>PhysicalFileSystem</c> which delegates to the real file system.
/// </para>
/// </remarks>
public interface IFileSystem
{
    /// <summary>
    /// Determines whether the specified directory exists.
    /// </summary>
    /// <param name="path">The path to the directory to check.</param>
    /// <returns><c>true</c> if the directory exists; otherwise, <c>false</c>.</returns>
    bool DirectoryExists(string path);

    /// <summary>
    /// Creates all directories and subdirectories in the specified path.
    /// </summary>
    /// <param name="path">The directory path to create.</param>
    /// <remarks>
    /// This method creates all intermediate directories if they do not exist,
    /// similar to <see cref="Directory.CreateDirectory(string)"/>.
    /// </remarks>
    void CreateDirectory(string path);

    /// <summary>
    /// Returns an enumerable collection of file paths matching a search pattern.
    /// </summary>
    /// <param name="path">The path to the directory to search.</param>
    /// <param name="searchPattern">
    /// The search pattern to match file names. Supports wildcards (* and ?).
    /// </param>
    /// <param name="searchOption">
    /// Specifies whether to search only the current directory or all subdirectories.
    /// </param>
    /// <returns>
    /// An enumerable collection of full file paths matching the search criteria.
    /// </returns>
    IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption);

    /// <summary>
    /// Asynchronously reads all text from a file.
    /// </summary>
    /// <param name="path">The path to the file to read.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the file content as a string.</returns>
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets information about a file.
    /// </summary>
    /// <param name="path">The path to the file.</param>
    /// <returns>A <see cref="FileInfo"/> object containing file metadata.</returns>
    FileInfo GetFileInfo(string path);

    /// <summary>
    /// Determines whether a file is binary (non-text).
    /// </summary>
    /// <param name="filePath">The path to the file to check.</param>
    /// <returns><c>true</c> if the file appears to be binary; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// This method uses heuristics to detect binary content by checking for
    /// non-ASCII characters exceeding a threshold percentage.
    /// </remarks>
    bool IsBinaryFile(string filePath);

    /// <summary>
    /// Gets the relative path from one path to another.
    /// </summary>
    /// <param name="relativeTo">The source path to calculate the relative path from.</param>
    /// <param name="path">The target path.</param>
    /// <returns>The relative path from <paramref name="relativeTo"/> to <paramref name="path"/>.</returns>
    string GetRelativePath(string relativeTo, string path);
}
