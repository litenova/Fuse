using Fuse.Core.Abstractions;

namespace Fuse.Infrastructure.FileSystem;

// Implement all methods of IFileSystem using System.IO equivalents.
public sealed class PhysicalFileSystem : IFileSystem
{
    public bool DirectoryExists(string path)
    {
        return Directory.Exists(path);
    }

    public void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
    }

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
    {
        return Directory.EnumerateFiles(path, searchPattern, searchOption);
    }

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        return File.ReadAllTextAsync(path, cancellationToken);
    }

    public FileInfo GetFileInfo(string path)
    {
        return new FileInfo(path);
    }

    public string GetRelativePath(string relativeTo, string path)
    {
        return Path.GetRelativePath(relativeTo, path);
    }

    public bool IsBinaryFile(string filePath)
    {
        const int charsToCheck = 8000;
        const double threshold = 0.1;

        using var streamReader = new StreamReader(filePath);
        var buffer = new char[charsToCheck];
        var bytesRead = streamReader.ReadBlock(buffer, 0, charsToCheck);

        if (bytesRead == 0)
        {
            return false;
        }

        var nonAsciiChars = 0;
        for (var i = 0; i < bytesRead; i++)
            if (buffer[i] > 255)
            {
                nonAsciiChars++;
            }

        return (double)nonAsciiChars / bytesRead > threshold;
    }
}