using Fuse.Indexing;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

/// <summary>
///     Guards the workspace map against depending on the aggregate SQLite store for read-only rendering.
/// </summary>
public sealed class FocusedStoreDependencyTests
{
    [Fact]
    public void Workspace_map_renderer_uses_lifecycle_and_query_ports()
    {
        var constructor = Assert.Single(typeof(WorkspaceMapRenderer).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Collection(
            parameters,
            parameter => Assert.Equal(typeof(IWorkspaceIndexLifecycleStore), parameter.ParameterType),
            parameter => Assert.Equal(typeof(IWorkspaceIndexQueryStore), parameter.ParameterType));
    }
}
