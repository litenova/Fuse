using Fuse.Cli.Services;

namespace Fuse.Cli.Tests.Services;

public sealed class DebouncedFileWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-watch-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Changed_IsRaisedAfterDebounce()
    {
        Directory.CreateDirectory(_root);
        var filePath = Path.Combine(_root, "sample.txt");
        await File.WriteAllTextAsync(filePath, "initial");

        using var watcher = new DebouncedFileWatcher(_root, recursive: false, debounceMilliseconds: 200);
        var tcs = new TaskCompletionSource();

        watcher.Changed += _ =>
        {
            tcs.TrySetResult();
            return Task.CompletedTask;
        };

        await File.WriteAllTextAsync(filePath, "updated");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(tcs.Task, completed);
    }

    [Fact]
    public async Task FuseCacheDirectoryChanges_DoNotTriggerWatcher()
    {
        Directory.CreateDirectory(_root);
        var fuseCacheDir = Path.Combine(_root, ".fuse", "cache");
        Directory.CreateDirectory(fuseCacheDir);

        using var watcher = new DebouncedFileWatcher(_root, recursive: true, debounceMilliseconds: 100);
        var tcs = new TaskCompletionSource();

        watcher.Changed += _ =>
        {
            tcs.TrySetResult();
            return Task.CompletedTask;
        };

        var cacheFile = Path.Combine(fuseCacheDir, "entry.txt");
        await File.WriteAllTextAsync(cacheFile, "cached");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(tcs.Task, completed);
    }

    [Fact]
    public async Task GitMetadataChanges_DoNotTriggerWatcher()
    {
        Directory.CreateDirectory(_root);
        var gitDirectory = Path.Combine(_root, ".git");
        Directory.CreateDirectory(gitDirectory);

        using var watcher = new DebouncedFileWatcher(_root, recursive: true, debounceMilliseconds: 100);
        var tcs = new TaskCompletionSource();

        watcher.Changed += _ =>
        {
            tcs.TrySetResult();
            return Task.CompletedTask;
        };

        await File.WriteAllTextAsync(Path.Combine(gitDirectory, "index.lock"), "lock");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(tcs.Task, completed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
