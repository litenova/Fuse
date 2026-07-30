using System.Runtime.CompilerServices;
using DotMake.CommandLine;
using Fuse.Cli.Commands;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Reduction.Caching;
using Fuse.Semantics;

namespace Fuse.Cli.Tests.Commands;

/// <summary>
///     Verifies that index cleanup deletes the closed set of derived files after cancellation settles.
/// </summary>
public sealed class IndexCleanCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-index-clean", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Clean_yes_deletes_only_known_derived_files_created_before_or_during_cancellation()
    {
        _root.AsIsolatedRepo();
        var fuseDirectory = Path.Combine(_root, ".fuse");
        Directory.CreateDirectory(Path.Combine(fuseDirectory, "captures"));
        var database = FuseStorePaths.ResolveDatabasePath(_root);
        var knownFiles = new[]
        {
            database,
            database + "-wal",
            database + "-shm",
            Path.Combine(fuseDirectory, "fuse-cache.db"),
            Path.Combine(fuseDirectory, "fuse-cache.db-wal"),
            Path.Combine(fuseDirectory, "fuse-cache.db-shm"),
            Path.Combine(fuseDirectory, "fuse-cache.db-journal"),
            Path.Combine(fuseDirectory, "r60-semantics.json"),
        };
        foreach (var path in knownFiles)
            await File.WriteAllTextAsync(path, "derived");

        var config = Path.Combine(_root, "fuse.json");
        var ignore = Path.Combine(_root, ".fuseignore");
        var capture = Path.Combine(fuseDirectory, "captures", "capture.json");
        var unknown = Path.Combine(fuseDirectory, "keep.txt");
        await File.WriteAllTextAsync(config, "{\"ignore\":[]}");
        await File.WriteAllTextAsync(ignore, "vendor/");
        await File.WriteAllTextAsync(capture, "capture");
        await File.WriteAllTextAsync(unknown, "keep");

        var jobs = new CleanupJobManager(root => File.WriteAllText(database + "-journal", $"late:{root}"));
        var console = new CapturingConsoleUI();
        var command = new IndexCleanCommand(new IndexJobClient(jobs, daemonEnabled: false), console)
        {
            Path = _root,
            Yes = true,
        };

        await command.RunAsync(TestCliContext());

        Assert.All(knownFiles, path => Assert.False(File.Exists(path), $"Expected derived file to be removed: {path}"));
        Assert.False(File.Exists(database + "-journal"));
        Assert.True(File.Exists(config));
        Assert.True(File.Exists(ignore));
        Assert.True(File.Exists(capture));
        Assert.True(File.Exists(unknown));
        Assert.Contains("Removed 9 Fuse-derived file(s).", console.Successes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static CliContext TestCliContext() =>
        (CliContext)RuntimeHelpers.GetUninitializedObject(typeof(CliContext));

    private sealed class CleanupJobManager(Action<string> onCancel) : IWorkspaceIndexJobManager
    {
        public Task<IndexJobStartResult> StartOrJoinAsync(IndexJobRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IndexJobSnapshot? GetStatus(string root) => null;

        public Task<IndexJobSnapshot?> CancelAsync(string root, CancellationToken cancellationToken)
        {
            onCancel(root);
            return Task.FromResult<IndexJobSnapshot?>(null);
        }

        public Task<IndexJobSnapshot?> WaitForCompletionAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult<IndexJobSnapshot?>(null);

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingConsoleUI : IConsoleUI
    {
        public List<string> Successes { get; } = [];

        public void WriteError(string message)
        {
        }

        public void WriteResult(string message)
        {
        }

        public void WriteStep(string message)
        {
        }

        public void WriteSuccess(string message) => Successes.Add(message);
    }
}
