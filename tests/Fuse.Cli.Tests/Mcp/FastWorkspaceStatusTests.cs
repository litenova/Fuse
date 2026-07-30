using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Commands;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

/// <summary>
///     Status reads retained index and job state without creating a competing index pass.
/// </summary>
[Collection("FuseToolsResidentProvider")]
public sealed class FastWorkspaceStatusTests : IAsyncLifetime, IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-fast-status", Guid.NewGuid().ToString("N"));
    private SemanticIndexer Indexer => _provider.GetRequiredService<SemanticIndexer>();
    private IChangeSource ChangeSource => _provider.GetRequiredService<IChangeSource>();
    private IndexJobClient Jobs => new(_provider.GetRequiredService<IWorkspaceIndexJobManager>(), daemonEnabled: false);

    public Task InitializeAsync()
    {
        _root.AsIsolatedRepo();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Status_on_cold_repo_reports_not_indexed_without_creating_a_database()
    {
        var databasePath = Fuse.Reduction.Caching.FuseStorePaths.ResolveDatabasePath(_root);
        Assert.False(File.Exists(databasePath));

        var status = await FuseTools.FuseWorkspaceAsync(Indexer, Jobs, action: "status", path: _root);

        Assert.False(File.Exists(databasePath));
        Assert.Contains("index_state: not_indexed", status);
        Assert.Contains("index mode: not_indexed", status);
        Assert.Contains("files indexed: 0", status);
        Assert.Contains("index job: none", status);
    }

    [Fact]
    public async Task Status_on_warm_db_reports_index_state_under_two_seconds()
    {
        var databasePath = Fuse.Reduction.Caching.FuseStorePaths.ResolveDatabasePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var sourcePath = Path.Combine(_root, "App.cs");
        await File.WriteAllTextAsync(sourcePath, "public class App { }");
        var hash = new FileHashService().ComputeHash(await File.ReadAllBytesAsync(sourcePath));
        var file = new IndexedFileRecord("App.cs", "App.cs", ".cs", new FileInfo(sourcePath).Length, File.GetLastWriteTimeUtc(sourcePath).Ticks, hash, Language: "csharp");
        await using (var seed = new WorkspaceIndexStore(databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.SetMetaAsync("index_mode", "syntax", CancellationToken.None);
            await seed.SetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, "1", CancellationToken.None);
            await seed.UpsertFilesAsync(
                [file],
                CancellationToken.None);
            await WorkspaceIndexManifest.CompleteAsync(_root, seed, [file], CancellationToken.None);
        }

        var stopwatch = Stopwatch.StartNew();
        var status = await FuseTools.FuseWorkspaceAsync(Indexer, Jobs, action: "status", path: _root);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"status took {stopwatch.Elapsed.TotalSeconds:F1}s");
        Assert.Contains("index_state: building_syntax", status);
        Assert.Contains("semantic upgrade in progress", status);
        Assert.Contains("files indexed: 1", status);
    }

    [Fact]
    public async Task Doctor_warms_cold_repo_before_reporting_diagnosis()
    {
        var databasePath = Fuse.Reduction.Caching.FuseStorePaths.ResolveDatabasePath(_root);
        Assert.False(File.Exists(databasePath));

        var doctor = await FuseTools.FuseWorkspaceAsync(Indexer, Jobs, action: "doctor", path: _root);

        Assert.True(File.Exists(databasePath));
        Assert.StartsWith("index_state: ready", doctor);
        Assert.Contains("load tier:", doctor);
    }

    [Fact]
    public async Task FuseFind_returns_index_busy_when_database_is_locked()
    {
        var databasePath = Fuse.Reduction.Caching.FuseStorePaths.ResolveDatabasePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var seed = new WorkspaceIndexStore(databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.UpsertFilesAsync(
                [new IndexedFileRecord("App.cs", "App.cs", ".cs", 10, DateTime.UtcNow.Ticks, "hash", Language: "csharp")],
                CancellationToken.None);
        }

        await using var lockConnection = new SqliteConnection($"Data Source={databasePath}");
        await lockConnection.OpenAsync();
        await using var lockCommand = lockConnection.CreateCommand();
        lockCommand.CommandText = "BEGIN EXCLUSIVE;";
        await lockCommand.ExecuteNonQueryAsync();

        var result = await FuseTools.FuseFindAsync(
            Indexer, ChangeSource, "App", path: _root, kind: "symbol");

        Assert.StartsWith("index_state: index_busy", result);
        Assert.Contains("files_indexed: 1", result);
        Assert.Contains("availability:", result);
        Assert.DoesNotContain(FuseOperationalErrors.IndexBusyPrefix, result);
    }

    public Task DisposeAsync()
    {
        var databasePath = Fuse.Reduction.Caching.FuseStorePaths.ResolveDatabasePath(_root);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _provider.Dispose();
}

/// <summary>
///     R15: CLI commands that open the store mirror the prefixed operational errors (exit 1, one stderr line).
/// </summary>
public sealed class CliOperationalErrorTests
{
    [Fact]
    public async Task Find_without_index_writes_index_not_built_to_stderr_and_sets_exit_code()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-cli-find", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        root.AsIsolatedRepo();
        var console = new CapturingConsoleUI();
        var command = new FindCommand(console) { Path = root, Query = "Widget" };

        Environment.ExitCode = 0;
        await command.RunAsync(TestCliContext());

        Assert.Equal(1, Environment.ExitCode);
        Assert.Single(console.Errors);
        Assert.StartsWith(FuseOperationalErrors.IndexNotBuiltPrefix, console.Errors[0]);
    }

    [Fact]
    public async Task Diagnostics_on_missing_workspace_writes_workspace_not_found()
    {
        var console = new CapturingConsoleUI();
        var command = new DiagnosticsCommand(console) { Path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing") };

        Environment.ExitCode = 0;
        await command.RunAsync(TestCliContext());

        Assert.Equal(1, Environment.ExitCode);
        Assert.Single(console.Errors);
        Assert.StartsWith(FuseOperationalErrors.WorkspaceNotFoundPrefix, console.Errors[0]);
    }

    [Fact]
    public async Task Diagnostics_on_cold_repo_reports_not_indexed_without_creating_db()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-cli-diag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        root.AsIsolatedRepo();
        var databasePath = Fuse.Reduction.Caching.FuseStorePaths.ResolveDatabasePath(root);
        var console = new CapturingConsoleUI();
        var command = new DiagnosticsCommand(console) { Path = root };

        await command.RunAsync(TestCliContext());

        Assert.False(File.Exists(databasePath));
        Assert.Contains("index_state: not_indexed", console.Results.ToString());
    }

    private sealed class CapturingConsoleUI : IConsoleUI
    {
        public List<string> Errors { get; } = [];
        public StringBuilder Results { get; } = new();

        public void WriteError(string message) => Errors.Add(message);
        public void WriteResult(string message) => Results.AppendLine(message);
        public void WriteStep(string message) { }
        public void WriteSuccess(string message) { }
    }

    private static CliContext TestCliContext() =>
        (CliContext)RuntimeHelpers.GetUninitializedObject(typeof(CliContext));
}
