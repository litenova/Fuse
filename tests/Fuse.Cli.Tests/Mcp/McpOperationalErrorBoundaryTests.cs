using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

/// <summary>
///     R15: MCP tool entry points return prefixed strings instead of throwing on operational failures.
/// </summary>
public sealed class McpOperationalErrorBoundaryTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
    private SemanticIndexer Indexer => _provider.GetRequiredService<SemanticIndexer>();
    private IndexJobClient Jobs => new(_provider.GetRequiredService<IWorkspaceIndexJobManager>(), daemonEnabled: false);
    private IChangeSource ChangeSource => _provider.GetRequiredService<IChangeSource>();

    [Fact]
    public async Task FuseWorkspace_status_returns_workspace_not_found_instead_of_throwing()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing");
        var result = await WorkspaceToolOperations.ExecuteAsync(Indexer, Jobs, action: "status", path: missingRoot);
        Assert.StartsWith(FuseOperationalErrors.WorkspaceNotFoundPrefix, result);
    }

    [Fact]
    public async Task FuseFind_returns_validation_error_for_empty_query_instead_of_throwing()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))!.FullName;
        var result = await FindToolOperations.ExecuteAsync(Indexer, ChangeSource, "", path: root);
        Assert.StartsWith(FuseOperationalErrors.ValidationErrorPrefix, result);
    }

    [Fact]
    public async Task FuseReduce_returns_validation_error_for_empty_input_instead_of_throwing()
    {
        var result = await ReduceToolOperations.ExecuteAsync(null!, null!);
        Assert.StartsWith(FuseOperationalErrors.ValidationErrorPrefix, result);
    }

    [Fact]
    public async Task Unknown_exception_becomes_internal_error_not_throw()
    {
        var result = await FuseOperationalErrors.ExecuteMcpAsync(() =>
            throw new InvalidOperationException("boom"));
        Assert.StartsWith(FuseOperationalErrors.InternalErrorPrefix, result);
    }

    public void Dispose() => _provider.Dispose();
}
