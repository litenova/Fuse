// -----------------------------------------------------------------------
// <copyright file="PhysicalFileSystem.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Fuse.Core.Abstractions;

namespace Fuse.Engine.FileSystem;

/// <summary>
///     Provides a concrete implementation of <see cref="IFileSystem" /> that operates
///     on the physical file system using <see cref="System.IO" /> classes.
/// </summary>
/// <remarks>
///     <para>
///         This class serves as the production implementation of the file system abstraction.
///         All file operations delegate directly to the corresponding <see cref="System.IO" /> methods.
///     </para>
///     <para>
///         The class is designed to be registered as a singleton in the DI container,
///         as it maintains no state and all operations are thread-safe.
///     </para>
/// </remarks>
public sealed class PhysicalFileSystem : IFileSystem
{
    /// <inheritdoc />
    /// <summary>
    ///     Determines whether the specified directory exists on disk.
    /// </summary>
    public bool DirectoryExists(string path)
    {
        return Directory.Exists(path);
    }

    /// <inheritdoc />
    /// <summary>
    ///     Creates all directories and subdirectories in the specified path.
    /// </summary>
    public void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
    }

    /// <inheritdoc />
    /// <summary>
    ///     Returns an enumerable collection of file paths that match a search pattern.
    /// </summary>
    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
    {
        return Directory.EnumerateFiles(path, searchPattern, searchOption);
    }

    /// <inheritdoc />
    /// <summary>
    ///     Asynchronously reads all text from a file using UTF-8 encoding.
    /// </summary>
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        return File.ReadAllTextAsync(path, cancellationToken);
    }

    /// <inheritdoc />
    /// <summary>
    ///     Gets a <see cref="FileInfo" /> object for the specified file path.
    /// </summary>
    public FileInfo GetFileInfo(string path)
    {
        return new FileInfo(path);
    }

    /// <inheritdoc />
    /// <summary>
    ///     Gets the relative path from one path to another.
    /// </summary>
    public string GetRelativePath(string relativeTo, string path)
    {
        return Path.GetRelativePath(relativeTo, path);
    }

    /// <inheritdoc />
    /// <summary>
    ///     Determines whether a file is binary by analyzing its content.
    /// </summary>
    /// <remarks>
    ///     This method samples the first 8000 characters of the file and checks
    ///     if more than 10% are non-ASCII characters, which typically indicates
    ///     binary content such as images, executables, or compiled files.
    /// </remarks>
    public bool IsBinaryFile(string filePath)
    {
        // Number of characters to sample from the file
        const int charsToCheck = 8000;

        // Threshold percentage of non-ASCII characters to consider file as binary
        const double threshold = 0.1;

        // Open the file and read a sample of characters
        using var streamReader = new StreamReader(filePath);
        var buffer = new char[charsToCheck];
        var bytesRead = streamReader.ReadBlock(buffer, 0, charsToCheck);

        // Empty files are not considered binary
        if (bytesRead == 0)
            return false;

        // Count characters that are outside the ASCII range
        var nonAsciiChars = 0;
        for (var i = 0; i < bytesRead; i++)

            // Characters above 255 are definitely non-ASCII
            if (buffer[i] > 255)
                nonAsciiChars++;

        // If more than threshold percentage are non-ASCII, it's likely binary
        return (double)nonAsciiChars / bytesRead > threshold;
    }
}