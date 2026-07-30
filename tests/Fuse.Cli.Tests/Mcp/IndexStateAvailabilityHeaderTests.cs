using System.Text.RegularExpressions;
using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

/// <summary>
///     R20: parameterized availability header shape for each <c>index_state</c> value and golden snapshots.
/// </summary>
[Collection("FuseToolsResidentProvider")]
public sealed class IndexStateAvailabilityHeaderTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-index-state-root", Guid.NewGuid().ToString("N"));
    private SemanticIndexer Indexer => _provider.GetRequiredService<SemanticIndexer>();
    private IChangeSource ChangeSource => _provider.GetRequiredService<IChangeSource>();

    // Isolate the header root's store to {_root}/.fuse so root-derived lookups never read the shared machine-wide
    // ~/.fuse. The golden encodes the store-backed header.
    public IndexStateAvailabilityHeaderTests()
    {
        _root.AsIsolatedRepo();
    }

    public static TheoryData<string, Action<WorkspaceIndexStore>, int?> IndexStateCases => new()
    {
        { "not_indexed", _ => { }, null },
        {
            "building_syntax", store =>
            {
                store.SetMetaAsync("index_mode", "syntax", CancellationToken.None).GetAwaiter().GetResult();
                store.SetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, "1", CancellationToken.None).GetAwaiter().GetResult();
                store.UpsertFilesAsync(
                    [new IndexedFileRecord("App.cs", "App.cs", ".cs", 10, DateTime.UtcNow.Ticks, "hash", Language: "csharp")],
                    CancellationToken.None).GetAwaiter().GetResult();
            },
            1
        },
        {
            "upgrade_pending", store =>
            {
                store.SetMetaAsync("index_mode", "partial", CancellationToken.None).GetAwaiter().GetResult();
                store.SetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, "1", CancellationToken.None).GetAwaiter().GetResult();
                store.UpsertFilesAsync(
                    [new IndexedFileRecord("Svc.cs", "Svc.cs", ".cs", 10, DateTime.UtcNow.Ticks, "hash", Language: "csharp")],
                    CancellationToken.None).GetAwaiter().GetResult();
            },
            1
        },
        {
            "ready", store =>
            {
                store.SetMetaAsync("index_mode", "semantic", CancellationToken.None).GetAwaiter().GetResult();
                store.SetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, "0", CancellationToken.None).GetAwaiter().GetResult();
                store.UpsertFilesAsync(
                    [new IndexedFileRecord("Ready.cs", "Ready.cs", ".cs", 10, DateTime.UtcNow.Ticks, "hash", Language: "csharp")],
                    CancellationToken.None).GetAwaiter().GetResult();
            },
            1
        },
        {
            "stale_as_of", store =>
            {
                store.SetMetaAsync("index_mode", "semantic", CancellationToken.None).GetAwaiter().GetResult();
                store.SetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, "4", CancellationToken.None).GetAwaiter().GetResult();
                store.UpsertFilesAsync(
                    [new IndexedFileRecord("Stale.cs", "Stale.cs", ".cs", 10, DateTime.UtcNow.Ticks, "hash", Language: "csharp")],
                    CancellationToken.None).GetAwaiter().GetResult();
            },
            1
        },
        {
            "index_busy", store =>
            {
                store.SetMetaAsync("index_mode", "syntax", CancellationToken.None).GetAwaiter().GetResult();
                store.UpsertFilesAsync(
                    [new IndexedFileRecord("Busy.cs", "Busy.cs", ".cs", 10, DateTime.UtcNow.Ticks, "hash", Language: "csharp")],
                    CancellationToken.None).GetAwaiter().GetResult();
            },
            1
        },
    };

    [Theory]
    [MemberData(nameof(IndexStateCases))]
    public async Task Header_includes_index_state_and_files_indexed_for_each_state(
        string expectedState,
        Action<WorkspaceIndexStore> seed,
        int? expectedFiles)
    {
        if (expectedState == "not_indexed")
        {
            var header = await FuseTools.FormatNotIndexedAvailabilityHeaderAsync(_root, CancellationToken.None);
            AssertHeaderShape(header, expectedState, 0);
            AvailabilityHeaderGoldenAssert.AssertMatches($"availability-header-{expectedState}", NormalizeForGolden(header));
            return;
        }

        var databasePath = Path.Combine(
            Path.GetTempPath(), "fuse-index-state-case", Guid.NewGuid().ToString("N"), "fuse.db");
        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        seed(store);
        var hashes = await store.GetAllFileHashesAsync(CancellationToken.None);
        var inventory = hashes
            .Select(pair => new IndexedFileRecord(
                pair.Key,
                pair.Key,
                Path.GetExtension(pair.Key),
                0,
                0,
                pair.Value))
            .ToList();
        await WorkspaceIndexManifest.CompleteAsync(_root, store, inventory, CancellationToken.None);

        var headerFromStore = expectedState == "index_busy"
            ? await FuseTools.OracleAvailabilityHeaderAsync(store, _root, CancellationToken.None, indexStateOverride: "index_busy")
            : await FuseTools.OracleAvailabilityHeaderAsync(store, _root, CancellationToken.None);

        AssertHeaderShape(headerFromStore, expectedState, expectedFiles!.Value);
        AvailabilityHeaderGoldenAssert.AssertMatches($"availability-header-{expectedState}", NormalizeForGolden(headerFromStore));

    }

    [Fact]
    public async Task Blocked_find_returns_building_header_within_configured_deadline()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-blocked-find", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        root.AsIsolatedRepo();
        var databasePath = Fuse.Reduction.Caching.FuseStorePaths.ResolveDatabasePath(root);
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await FuseTools.FuseFindAsync(
            Indexer,
            ChangeSource,
            "App",
            path: root,
            kind: "symbol",
            cancellationToken: cts.Token);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"find blocked for {stopwatch.Elapsed.TotalSeconds:F1}s");
        Assert.DoesNotContain(FuseOperationalErrors.IndexBusyPrefix, result);
        Assert.DoesNotContain(FuseOperationalErrors.InternalErrorPrefix, result);
        Assert.StartsWith("index_state: building_syntax", result);
        Assert.Contains("grade: deferred", result);
        Assert.Contains("availability:", result);

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _provider.Dispose();
    }

    private static void AssertHeaderShape(string header, string expectedState, int expectedFiles)
    {
        Assert.StartsWith($"index_state: {expectedState}", header);
        Assert.Contains($"files_indexed: {expectedFiles}", header);
        Assert.Contains("availability:", header);
        Assert.Matches(new Regex(@"^index_state: \w+", RegexOptions.Multiline), header);
    }

    private static string NormalizeForGolden(string header) =>
        header
            .Replace("tier-1 build capture configured", "tier-1 build capture {tier1}", StringComparison.Ordinal)
            .Replace("tier-1 build capture not configured", "tier-1 build capture {tier1}", StringComparison.Ordinal)
            .Replace("verify serves oracle-grade", "verify serves {verify-grade}", StringComparison.Ordinal)
            .Replace("verify serves build-grade (fuse_check runs a scoped dotnet build)", "verify serves {verify-grade}", StringComparison.Ordinal);
}

internal static class AvailabilityHeaderGoldenAssert
{
    private static string ExpectedDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Mcp", "expected");

    public static void AssertMatches(string scenarioName, string actual)
    {
        var expectedPath = Path.Combine(ExpectedDirectory, scenarioName + ".golden");
        if (string.Equals(Environment.GetEnvironmentVariable("UPDATE_GOLDEN_FILES"), "1", StringComparison.Ordinal))
        {
            var sourceExpected = Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..",
                "Mcp", "expected",
                scenarioName + ".golden");
            sourceExpected = Path.GetFullPath(sourceExpected);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceExpected)!);
            File.WriteAllText(sourceExpected, actual, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Directory.CreateDirectory(ExpectedDirectory);
            File.WriteAllText(expectedPath, actual, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        Assert.True(File.Exists(expectedPath), $"Golden file missing: {expectedPath}. Set UPDATE_GOLDEN_FILES=1 to generate.");
        var expected = File.ReadAllText(expectedPath);
        Assert.Equal(NormalizeLineEndings(expected), NormalizeLineEndings(actual));
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
}
