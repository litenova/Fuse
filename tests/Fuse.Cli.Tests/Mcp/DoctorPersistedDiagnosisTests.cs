using System.Text.Json;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// R43: fuse_workspace action=doctor reports the load diagnosis stamped in the warm index without a live MSBuild
// load; refresh=true forces the live load. The seeded root has no solution, so a live load can only report the
// syntax tier - seeing the seeded "oracle-grade" proves the persisted stamp was read, not a live load.
public sealed class DoctorPersistedDiagnosisTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
    private SemanticIndexer Indexer => _provider.GetRequiredService<SemanticIndexer>();
    private IndexJobClient Jobs => new(_provider.GetRequiredService<IWorkspaceIndexJobManager>(), daemonEnabled: false);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-r43", Guid.NewGuid().ToString("N"));

    private async Task SeedPersistedDiagnosisAsync()
    {
        Directory.CreateDirectory(_root);
        _root.AsIsolatedRepo();
        var persisted = new PersistedLoadDiagnosis(
            "oracle-grade (all projects loaded clean)",
            ProjectsLoaded: 1,
            ProjectsTotal: 1,
            Projects: [new PersistedProjectReport("SeededProject", "/x/SeededProject.csproj", true, "loaded")],
            SelectedSolution: "/x/Seeded.sln",
            SelectionNote: null);
        var json = JsonSerializer.Serialize(persisted, PersistedLoadDiagnosisJsonContext.Default.PersistedLoadDiagnosis);

        var dbPath = FuseStorePaths.ResolveDatabasePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using var store = new WorkspaceIndexStore(dbPath);
        await store.InitializeAsync(CancellationToken.None);
        await store.SetMetaAsync(WorkspaceIndexStore.LoadDiagnosisMetaKey, json, CancellationToken.None);
        await store.SetMetaAsync("index_mode", "syntax", CancellationToken.None);
        await WorkspaceIndexManifest.CompleteAsync(_root, store, [], CancellationToken.None);
    }

    [Fact]
    public async Task Doctor_reports_the_persisted_diagnosis_without_a_live_load()
    {
        await SeedPersistedDiagnosisAsync();

        var result = await WorkspaceToolOperations.ExecuteAsync(Indexer, Jobs, action: "doctor", path: _root, refresh: false);

        Assert.Contains("diagnosis source: warm index", result);
        Assert.Contains("oracle-grade (all projects loaded clean)", result); // A live load of a solution-less root could not report this.
        Assert.Contains("SeededProject", result);
    }

    [Fact]
    public async Task Doctor_refresh_forces_a_live_load_and_bypasses_the_stamp()
    {
        await SeedPersistedDiagnosisAsync();

        var result = await WorkspaceToolOperations.ExecuteAsync(Indexer, Jobs, action: "doctor", path: _root, refresh: true);

        Assert.Contains("diagnosis source: live MSBuild load", result);
        Assert.DoesNotContain("oracle-grade (all projects loaded clean)", result); // The live load sees no solution.
        Assert.Contains("syntax", result);
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
