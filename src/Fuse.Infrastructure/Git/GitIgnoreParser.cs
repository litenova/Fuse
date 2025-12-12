using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotNet.Globbing;
using Fuse.Core.Abstractions;

namespace Fuse.Infrastructure.Git;

public class GitIgnoreParser
{
    private readonly IFileSystem _fileSystem;

    public GitIgnoreParser(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public List<Glob> Parse(string startDirectory)
    {
        var patterns = new List<Glob>();
        var currentDirectory = startDirectory;

        while (!string.IsNullOrEmpty(currentDirectory))
        {
            var gitIgnorePath = Path.Combine(currentDirectory, ".gitignore");
            if (_fileSystem.GetFileInfo(gitIgnorePath).Exists)
            {
                var lines = _fileSystem.ReadAllTextAsync(gitIgnorePath).GetAwaiter().GetResult().Split('\n');
                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    if (!string.IsNullOrEmpty(trimmedLine) && !trimmedLine.StartsWith("#"))
                    {
                        // Globs are relative to the .gitignore file's directory
                        var globPattern = Path.Combine(currentDirectory, trimmedLine).Replace(Path.DirectorySeparatorChar, '/');
                        patterns.Add(Glob.Parse(globPattern));
                    }
                }
            }

            var parent = Directory.GetParent(currentDirectory);
            if (parent == null || parent.GetDirectories(".git").Length > 0)
            {
                break; // Stop if we've reached the repo root or filesystem root
            }

            currentDirectory = parent.FullName;
        }

        return patterns;
    }
}