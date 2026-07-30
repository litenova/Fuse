using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// R3: the ambient availability header. Every store-backed oracle read prepends a one-line grade so a client
// cannot mistake a syntax-tier or stale answer for an oracle-grade one. These tests pin the header wording
// against the three facts it reports: index mode, tier-1 build-capture availability, and the N6 freshness stamp.
//
public sealed class OracleAvailabilityHeaderTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-avail-header-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-avail-header-root", Guid.NewGuid().ToString("N"));
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Header_reports_index_mode_and_fresh_when_no_stale_count()
    {
        await _store.SetMetaAsync("index_mode", "semantic", CancellationToken.None);
        await _store.SetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, "0", CancellationToken.None);

        var header = await FuseTools.OracleAvailabilityHeaderAsync(_store, _root, CancellationToken.None);

        Assert.StartsWith("index_state:", header);
        Assert.Contains("files_indexed:", header);
        Assert.Contains("availability:", header);
        Assert.Contains("index mode semantic", header);
        Assert.Contains("up to date", header);
        Assert.DoesNotContain("may lag", header);
    }

    [Fact]
    public async Task Header_reports_a_stale_count_when_files_changed_since_index()
    {
        await _store.SetMetaAsync("index_mode", "partial", CancellationToken.None);
        await _store.SetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, "7", CancellationToken.None);

        var header = await FuseTools.OracleAvailabilityHeaderAsync(_store, _root, CancellationToken.None);

        Assert.Contains("index mode partial", header);
        Assert.Contains("7 known file(s) changed", header);
        Assert.Contains("may lag the working tree", header);
    }

    [Fact]
    public async Task Header_reports_unknown_mode_when_meta_absent()
    {
        var header = await FuseTools.OracleAvailabilityHeaderAsync(_store, _root, CancellationToken.None);

        Assert.Contains("index mode unknown", header);
        // Tier-1 build capture is reported either way; without FUSE_BUILD_CAPTURE_WORKER it is not configured.
        Assert.Contains("tier-1 build capture", header);
    }

    [Fact]
    public async Task Header_reports_store_backed_by_default_and_resident_when_a_workspace_is_live()
    {
        await _store.SetMetaAsync("index_mode", "semantic", CancellationToken.None);

        // Default seam: no resident workspace, so the header names the store as the truth source (S1/D8).
        var storeBacked = await FuseTools.OracleAvailabilityHeaderAsync(_store, _root, CancellationToken.None);
        Assert.Contains("workspace store-backed", storeBacked);

        // With a resident workspace wired for this root, the header names it resident with its stamp.
        var resident = await FuseTools.OracleAvailabilityHeaderAsync(
            _store,
            _root,
            CancellationToken.None,
            residentWorkspaces: new StubResidentProvider(
                _root,
                new Fuse.Workspace.ResidentStatus(3, "2026-07-08T00:00:00Z")));
        Assert.Contains("workspace resident (3 project(s), current as of 2026-07-08T00:00:00Z)", resident);
    }

    private sealed class StubResidentProvider(string root, Fuse.Workspace.ResidentStatus status)
        : Fuse.Workspace.IResidentWorkspaceProvider
    {
        public Fuse.Workspace.ResidentStatus? DescribeResident(string queried) =>
            string.Equals(queried, root, StringComparison.OrdinalIgnoreCase) ? status : null;

        public IReadOnlyList<Fuse.Indexing.CheckDiagnostic>? TryCheckOverlay(
            string queried, string relativeFilePath, string newContent, CancellationToken cancellationToken) => null;
    }

    [Fact]
    public async Task Header_names_the_build_grade_fallback_when_tier1_not_configured()
    {
        // T0/D11: with no tier-1 worker configured, fuse_check still verifies at build-grade (a scoped dotnet
        // build). The header names the grade the workspace can serve so a client knows the latency to expect and
        // never reads the missing oracle as "cannot verify".
        await _store.SetMetaAsync("index_mode", "syntax", CancellationToken.None);

        var header = await FuseTools.OracleAvailabilityHeaderAsync(_store, _root, CancellationToken.None);

        Assert.Contains("tier-1 build capture not configured", header);
        Assert.Contains("verify serves build-grade", header);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        var directory = Path.GetDirectoryName(_databasePath);
        try
        {
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
