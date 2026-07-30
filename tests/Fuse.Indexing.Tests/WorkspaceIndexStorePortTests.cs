using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

/// <summary>
///     Guards the focused store ports used to keep index stages from depending on an all-purpose SQLite surface.
/// </summary>
public sealed class WorkspaceIndexStorePortTests
{
    [Fact]
    public void Aggregate_store_exposes_each_focused_port()
    {
        var aggregate = typeof(IWorkspaceIndexStore);

        Assert.True(typeof(IWorkspaceIndexLifecycleStore).IsAssignableFrom(aggregate));
        Assert.True(typeof(IWorkspaceIndexWriteStore).IsAssignableFrom(aggregate));
        Assert.True(typeof(IWorkspaceIndexQueryStore).IsAssignableFrom(aggregate));
        Assert.True(typeof(IWorkspaceIndexGraphStore).IsAssignableFrom(aggregate));
        Assert.True(typeof(IWorkspaceIndexMetadataStore).IsAssignableFrom(aggregate));
        Assert.True(typeof(IWorkspaceIndexVerificationSessionStore).IsAssignableFrom(aggregate));
    }

    [Fact]
    public void Sqlite_store_implements_the_aggregate_contract()
    {
        Assert.True(typeof(IWorkspaceIndexStore).IsAssignableFrom(typeof(WorkspaceIndexStore)));
    }
}
