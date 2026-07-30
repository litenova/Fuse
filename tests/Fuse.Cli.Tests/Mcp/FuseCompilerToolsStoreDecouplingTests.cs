using System.Security.Cryptography;
using System.Text;
using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using Fuse.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// R18: compiler-tier fuse_check must not block on a mandatory index open. Repair-packet enrichment is best-effort.
// fuse_test covering selection is indexed-tier, so it requires a validated warm index and returns a bounded
// availability header when that index is contended.
public sealed class FuseCompilerToolsStoreDecouplingTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();

    public void Dispose()
    {
        _provider.Dispose();
    }

    [Fact]
    public async Task FuseCheck_runs_build_grade_when_index_writer_mutex_held_on_cold_workspace()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = CreateBuildableWorkspace();

        try
        {
            using var foreignLock = AcquireWriterMutex(work);

            var output = await CheckToolOperations.ExecuteAsync(
                indexer,
                work,
                "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => Nope; }",
                cancellationToken: CancellationToken.None);

            Assert.Contains("verification grade: build", output);
            Assert.DoesNotContain(FuseOperationalErrors.IndexBusyPrefix, output);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task FuseCheck_omits_repair_packets_with_note_when_index_is_unavailable()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = CreateBuildableWorkspace();

        try
        {
            var output = await CheckToolOperations.ExecuteAsync(
                indexer,
                work,
                "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => Nope; }",
                cancellationToken: CancellationToken.None);

            Assert.Contains("verification grade: build", output);
            Assert.Contains(
                "repair packets: omitted (index unavailable for symbol enrichment; the verification verdict is unchanged).",
                output);
            Assert.DoesNotContain("apply: replace", output);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task FuseCheck_uses_the_owning_project_without_loading_an_ambiguous_solution()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = CreateBuildableWorkspace();
        File.WriteAllText(Path.Combine(work, "A.sln"), "Microsoft Visual Studio Solution File, Format Version 12.00");
        File.WriteAllText(Path.Combine(work, "B.sln"), "Microsoft Visual Studio Solution File, Format Version 12.00");

        try
        {
            var output = await CheckToolOperations.ExecuteAsync(
                indexer,
                work,
                "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => Nope; }",
                cancellationToken: CancellationToken.None);

            Assert.Contains("verification grade: build", output);
            Assert.DoesNotContain("workspace selection is ambiguous", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task FuseTest_returns_building_header_when_covering_selection_store_is_locked()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = Path.Combine(Path.GetTempPath(), "fuse-test-store-busy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        work.AsIsolatedRepo();

        var databasePath = FuseStorePaths.ResolveDatabasePath(work);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await SeedCoveringTestsIndexAsync(databasePath);

        await using var lockHolder = await HoldExclusiveSqliteLockAsync(databasePath);
        try
        {
            var output = await TestToolOperations.ExecuteAsync(
                indexer,
                symbol: "OrderService",
                path: work,
                cancellationToken: CancellationToken.None);

            Assert.StartsWith("index_state: building_syntax", output);
            Assert.Contains("grade: deferred", output);
            Assert.DoesNotContain(FuseOperationalErrors.InternalErrorPrefix, output);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static string CreateBuildableWorkspace()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-check-decouple", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        work.AsIsolatedRepo();
        File.WriteAllText(Path.Combine(work, "Widget.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(work, "Widget.cs"),
            "namespace Sample; public sealed class Widget { public int Spin() => 42; }");
        return work;
    }

    private static async Task SeedCoveringTestsIndexAsync(string databasePath)
    {
        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        await store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/Orders/OrderService.cs", "src/Orders/OrderService.cs", ".cs", 10, 1, "h1"),
                new IndexedFileRecord("tests/OrderServiceTests.cs", "tests/OrderServiceTests.cs", ".cs", 10, 1, "h2"),
            ],
            CancellationToken.None);
        await store.UpsertNodesAsync(
            [
                new NodeRecord("type:OrderService", "class", "OrderService", "App.OrderService", "src/Orders/OrderService.cs"),
                new NodeRecord("type:OrderServiceTests", "class", "OrderServiceTests", "App.Tests.OrderServiceTests", "tests/OrderServiceTests.cs"),
            ],
            CancellationToken.None);
        await store.UpsertEdgesAsync(
            [new SemanticEdgeRecord("type:OrderServiceTests", "type:OrderService", "tests", 0.9, 1.0)],
            CancellationToken.None);
    }

    private static async Task<SqliteConnection> HoldExclusiveSqliteLockAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // locking_mode=EXCLUSIVE keeps the WAL/-shm locks held after the first write, so the covering-selection read
        // connection cannot open. A plain BEGIN IMMEDIATE would only take the write lock and still let WAL readers
        // through, so the test would not exercise the bounded building response.
        command.CommandText =
            "PRAGMA locking_mode=EXCLUSIVE; BEGIN IMMEDIATE; CREATE TABLE IF NOT EXISTS _fuse_lock_probe(x);";
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static Mutex AcquireWriterMutex(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var name = "fuse-index-writer-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
        var mutex = new Mutex(initiallyOwned: false, name);
        Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(5)));
        return mutex;
    }
}
