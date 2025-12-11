namespace Fuse.Core.Abstractions;

public interface IFileSystem
{
    bool DirectoryExists(string path);

    void CreateDirectory(string path);

    IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption);

    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    FileInfo GetFileInfo(string path);

    bool IsBinaryFile(string filePath);

    string GetRelativePath(string relativeTo, string path);
}