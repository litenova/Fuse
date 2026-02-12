// -----------------------------------------------------------------------
// <copyright file="FileCollector.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using DotNet.Globbing;
using Fuse.Core;
using Fuse.Engine.FileSystem;
using Fuse.Engine.Git;

namespace Fuse.Engine.Services;

/// <summary>
///     Collects and filters files from the file system based on configuration settings.
/// </summary>
/// <remarks>
///     <para>
///         This service is responsible for enumerating files and applying multiple filter criteria:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>File extension matching</description>
///         </item>
///         <item>
///             <description>Directory exclusion</description>
///         </item>
///         <item>
///             <description>.gitignore pattern matching</description>
///         </item>
///         <item>
///             <description>File size limits</description>
///         </item>
///         <item>
///             <description>Binary file detection</description>
///         </item>
///         <item>
///             <description>Test project exclusion</description>
///         </item>
///         <item>
///             <description>File pattern exclusion</description>
///         </item>
///     </list>
///     <para>
///         The collection process uses parallel processing for improved performance
///         when scanning large directory trees.
///     </para>
/// </remarks>
public sealed class FileCollector : IFileCollector
{
    /// <summary>
    ///     Unit test project directory suffixes (excludes integration/e2e/benchmarks).
    /// </summary>
    private static readonly string[] UnitTestProjectSuffixes =
    [
        "UnitTests", "UnitTest", "Tests", "Test", "Testing",
        "TestProject", "TestSuite", "TestLib", "TestData", "TestFramework",
        "TestUtils", "TestUtilities", "TestHelper", "TestHelpers", "TestCommon",
        "TestShared", "TestSupport"
    ];

    /// <summary>
    ///     Common test project directory suffixes to identify all test projects.
    /// </summary>
    private static readonly string[] TestProjectSuffixes =
    [
        "UnitTests", "Tests", "IntegrationTests", "Specs", "Test", "Testing",
        "FunctionalTests", "AcceptanceTests", "EndToEndTests", "E2ETests",
        "TestProject", "TestSuite", "TestLib", "TestData", "TestFramework",
        "TestUtils", "TestUtilities", "TestHelper", "TestHelpers", "TestCommon",
        "TestShared", "TestSupport", "Benchmark", "Benchmarks", "Performance",
        "PerformanceTests", "LoadTests", "StressTests"
    ];

    /// <summary>
    ///     The file system abstraction for file operations.
    /// </summary>
    private readonly PhysicalFileSystem _fileSystem;

    /// <summary>
    ///     Parser for .gitignore files and patterns.
    /// </summary>
    private readonly GitIgnoreParser _gitIgnoreParser;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FileCollector" /> class.
    /// </summary>
    /// <param name="fileSystem">The file system implementation.</param>
    /// <param name="gitIgnoreParser">The .gitignore parser.</param>
    public FileCollector(PhysicalFileSystem fileSystem, GitIgnoreParser gitIgnoreParser)
    {
        _fileSystem = fileSystem;
        _gitIgnoreParser = gitIgnoreParser;
    }

    /// <inheritdoc />
    /// <summary>
    ///     Collects all files matching the specified criteria using parallel processing.
    /// </summary>
    public List<FileProcessingInfo> CollectFiles(FuseOptions options, ResolvedConfiguration config)
    {
        // Determine search option based on recursive setting
        var searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // Parse .gitignore patterns if respecting gitignore is enabled
        var gitignorePatterns = options.RespectGitIgnore
            ? _gitIgnoreParser.Parse(options.SourceDirectory)
            : [];

        // Use parallel LINQ for better performance on large directories
        return _fileSystem.EnumerateFiles(options.SourceDirectory, "*.*", searchOption)
            .AsParallel()

            // Project to an anonymous type with all needed info to avoid repeated file system calls
            .Select(file => new
            {
                Path = file,
                Info = _fileSystem.GetFileInfo(file),
                RelativePath = _fileSystem.GetRelativePath(options.SourceDirectory, file)
            })

            // Filter 1: Apply .gitignore patterns
            .Where(f => !gitignorePatterns.Any(p => p.IsMatch(f.Path.Replace(Path.DirectorySeparatorChar, '/'))))

            // Filter 2: Match file extensions (or allow all if "*.*")
            .Where(f => config.Extensions.Contains("*.*") ||
                        config.Extensions.Any(ext => f.Info.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))

            // Filter 3: Exclude files in excluded directories
            .Where(f => !IsInExcludedFolder(f.RelativePath, config.ExcludeDirectories))

            // Filter 4: Exclude test projects if requested (all tests or just unit tests)
            .Where(f => !options.ExcludeTestProjects || !IsInTestProjectFolder(f.RelativePath))
            .Where(f => !options.ExcludeUnitTestProjects || !IsInUnitTestProjectFolder(f.RelativePath))

            // Filter 5: Apply file size limit if specified
            .Where(f => options.MaxFileSizeKB == 0 || f.Info.Length <= options.MaxFileSizeKB * 1024)

            // Filter 6: Skip binary files if requested
            .Where(f => !options.IgnoreBinaryFiles || !_fileSystem.IsBinaryFile(f.Path))

            // Filter 7: Apply file pattern exclusions
            .Where(f => !IsExcludedByPattern(f.Info.Name, config.ExcludePatterns))

            // Create the final FileProcessingInfo objects
            .Select(f => new FileProcessingInfo(f.Path, f.RelativePath, f.Info))
            .ToList();
    }

    /// <summary>
    ///     Determines if a file path contains any of the excluded folder names.
    /// </summary>
    /// <param name="relativePath">The relative path to check.</param>
    /// <param name="excludeFolders">The collection of folder names to exclude.</param>
    /// <returns><c>true</c> if the path contains an excluded folder; otherwise, <c>false</c>.</returns>
    private static bool IsInExcludedFolder(string relativePath, IReadOnlyCollection<string> excludeFolders)
    {
        // Split the path and check each directory component
        var pathParts = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return pathParts.Any(part => excludeFolders.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Determines if a file path is within a test project directory.
    /// </summary>
    /// <param name="relativePath">The relative path to check.</param>
    /// <returns><c>true</c> if the path is in a test project folder; otherwise, <c>false</c>.</returns>
    /// <remarks>
    ///     This method checks each directory component against common test project naming conventions.
    /// </remarks>
    private static bool IsInTestProjectFolder(string relativePath)
    {
        var pathParts = relativePath.Split(Path.DirectorySeparatorChar);
        return pathParts.Any(part =>
            TestProjectSuffixes.Any(suffix =>
                part.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    ///     Determines if a file path is within a unit test project directory.
    /// </summary>
    /// <param name="relativePath">The relative path to check.</param>
    /// <returns><c>true</c> if the path is in a unit test project folder; otherwise, <c>false</c>.</returns>
    /// <remarks>
    ///     This method checks each directory component against unit test project naming conventions only,
    ///     excluding integration tests, end-to-end tests, and benchmarks.
    /// </remarks>
    private static bool IsInUnitTestProjectFolder(string relativePath)
    {
        var pathParts = relativePath.Split(Path.DirectorySeparatorChar);
        return pathParts.Any(part =>
            UnitTestProjectSuffixes.Any(suffix =>
                part.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    ///     Determines if a file name matches any of the exclusion patterns.
    /// </summary>
    /// <param name="fileName">The file name to check.</param>
    /// <param name="excludePatterns">The collection of glob patterns to match against.</param>
    /// <returns><c>true</c> if the file matches an exclusion pattern; otherwise, <c>false</c>.</returns>
    private static bool IsExcludedByPattern(string fileName, IReadOnlyCollection<string> excludePatterns)
    {
        // If no patterns, nothing is excluded
        if (excludePatterns.Count == 0) return false;

        // Check each pattern for a match
        foreach (var pattern in excludePatterns)
        {
            var glob = Glob.Parse(pattern);
            if (glob.IsMatch(fileName))
                return true;
        }

        return false;
    }
}