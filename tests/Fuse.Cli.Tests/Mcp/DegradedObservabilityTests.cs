using Fuse.Cli;
using Fuse.Cli.Mcp;
using Fuse.Cli.Rpc;
using Fuse.Indexing;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// R37: degraded states are counted (queryable via FuseMetrics), the availability header carries an actionable
// wait hint on not-ready states, and the daemon log rotates. These make a fallback or not-ready read observable.
public sealed class DegradedObservabilityTests
{
    [Fact]
    public void FromException_IndexBusy_IncrementsDegradedCounter()
    {
        var before = FuseMetrics.GetDegradedCount(DegradedStateKind.IndexBusy);
        _ = FuseOperationalErrors.FromException(new IndexBusyException());
        Assert.True(FuseMetrics.GetDegradedCount(DegradedStateKind.IndexBusy) >= before + 1);
    }

    [Fact]
    public void FromException_IndexRebuilding_IncrementsDegradedCounter()
    {
        var before = FuseMetrics.GetDegradedCount(DegradedStateKind.IndexRebuilding);
        _ = FuseOperationalErrors.FromException(new IndexRebuildingException("rebuilding from source"));
        Assert.True(FuseMetrics.GetDegradedCount(DegradedStateKind.IndexRebuilding) >= before + 1);
    }

    [Fact]
    public void RecordDegraded_IsQueryable()
    {
        var before = FuseMetrics.GetDegradedCount(DegradedStateKind.Deferred);
        FuseMetrics.RecordDegraded(DegradedStateKind.Deferred);
        Assert.Equal(before + 1, FuseMetrics.GetDegradedCount(DegradedStateKind.Deferred));
    }

    [Fact]
    public async Task BuildingSyntaxHeader_CarriesAnActionableWaitHint()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-r37-hint", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        try
        {
            var header = await IndexAvailabilityReporter.BuildingSyntaxHeaderAsync(root, CancellationToken.None);
            Assert.Contains("index_state: building_syntax", header, StringComparison.Ordinal);
            Assert.Contains("grade: deferred (not semantic-ready)", header, StringComparison.Ordinal); // R30
            Assert.Contains("hint:", header, StringComparison.Ordinal);
            Assert.Contains("native search", header, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RollingFileLog_WritesAndRotatesOnSize()
    {
        var path = RollingFileLog.LogPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Pre-fill past the rotation threshold so the next write rotates the active file to .1.
            File.WriteAllBytes(path, new byte[RollingFileLog.RotateAtBytes + 1]);

            RollingFileLog.Write("host started");

            Assert.True(File.Exists(path + ".1")); // the oversized log was rotated.
            Assert.Contains("host started", File.ReadAllText(path)); // the new write landed in a fresh active log.
        }
        finally
        {
            foreach (var f in new[] { path, path + ".1" })
                try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
        }
    }
}
