// -----------------------------------------------------------------------
// <copyright file="GitIgnoreParser.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using DotNet.Globbing;
using Fuse.Engine.FileSystem;

namespace Fuse.Engine.Git;

/// <summary>
///     Parses .gitignore files and provides glob patterns for file exclusion.
/// </summary>
/// <remarks>
///     <para>
///         This parser walks up the directory tree from a starting directory, collecting
///         all .gitignore patterns found along the way. Patterns from child directories
///         take precedence over patterns from parent directories.
///     </para>
///     <para>
///         The parser stops traversing when it reaches:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>The repository root (directory containing .git folder)</description>
///         </item>
///         <item>
///             <description>The file system root</description>
///         </item>
///     </list>
/// </remarks>
public class GitIgnoreParser
{
    /// <summary>
    ///     The file system abstraction used for file operations.
    /// </summary>
    private readonly PhysicalFileSystem _fileSystem;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GitIgnoreParser" /> class.
    /// </summary>
    /// <param name="fileSystem">The file system implementation to use for file operations.</param>
    public GitIgnoreParser(PhysicalFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    ///     Parses all .gitignore files from the starting directory up to the repository root.
    /// </summary>
    /// <param name="startDirectory">The directory to start parsing from.</param>
    /// <returns>
    ///     A list of compiled glob patterns that can be used to match files against
    ///     .gitignore rules. Returns an empty list if no .gitignore files are found.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         Each pattern is converted to an absolute path glob pattern based on the
    ///         location of the .gitignore file that contained it. This allows patterns
    ///         to be matched against absolute file paths.
    ///     </para>
    ///     <para>
    ///         Lines starting with '#' are treated as comments and ignored.
    ///         Empty lines are also ignored.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    /// var parser = new GitIgnoreParser(fileSystem);
    /// var patterns = parser.Parse(@"C:\Projects\MyApp\src");
    /// 
    /// // Check if a file should be ignored
    /// var filePath = @"C:\Projects\MyApp\src\bin\Debug\app.dll";
    /// var isIgnored = patterns.Any(p => p.IsMatch(filePath.Replace('\\', '/')));
    /// </code>
    /// </example>
    public List<Glob> Parse(string startDirectory)
    {
        var patterns = new List<Glob>();
        var currentDirectory = startDirectory;

        // Walk up the directory tree looking for .gitignore files
        while (!string.IsNullOrEmpty(currentDirectory))
        {
            // Check if a .gitignore file exists in the current directory
            var gitIgnorePath = Path.Combine(currentDirectory, ".gitignore");
            if (_fileSystem.GetFileInfo(gitIgnorePath).Exists)
            {
                // Read and parse each line of the .gitignore file
                var lines = _fileSystem.ReadAllTextAsync(gitIgnorePath).GetAwaiter().GetResult().Split('\n');
                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    // Skip empty lines and comments
                    if (!string.IsNullOrEmpty(trimmedLine) && !trimmedLine.StartsWith('#'))
                    {
                        // Convert the pattern to an absolute glob pattern
                        // The pattern is relative to the .gitignore file's directory
                        var globPattern = Path.Combine(currentDirectory, trimmedLine)
                            .Replace(Path.DirectorySeparatorChar, '/');

                        patterns.Add(Glob.Parse(globPattern));
                    }
                }
            }

            // Move to the parent directory
            var parent = Directory.GetParent(currentDirectory);

            // Stop if we've reached the repository root (contains .git) or filesystem root
            if (parent == null || parent.GetDirectories(".git").Length > 0)
                break;

            currentDirectory = parent.FullName;
        }

        return patterns;
    }
}