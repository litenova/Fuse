using System.Diagnostics;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

public sealed class GitFileEnumeratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-git-inventory", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CleanTrackedFilesExposeBlobIdsAndDirtyFilesRequireDiskHashing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await RunGitAsync("init", "-q");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "Catalog.cs"), "public sealed class Catalog { }");
        await RunGitAsync("add", "src/Catalog.cs");

        var enumerator = new GitFileEnumerator();
        var clean = await enumerator.TryDescribeAsync(_root, CancellationToken.None);

        Assert.NotNull(clean);
        Assert.Contains("src/Catalog.cs", clean!.Paths);
        Assert.NotNull(clean.GetCleanBlobId("src/Catalog.cs"));

        await File.WriteAllTextAsync(Path.Combine(_root, "src", "Catalog.cs"), "public sealed class Catalog { public int Count => 1; }");
        var dirty = await enumerator.TryDescribeAsync(_root, CancellationToken.None);

        Assert.NotNull(dirty);
        Assert.Contains("src/Catalog.cs", dirty!.DirtyPaths);
        Assert.Null(dirty.GetCleanBlobId("src/Catalog.cs"));
    }

    private async Task RunGitAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _root,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        await process!.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
