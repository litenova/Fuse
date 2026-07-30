using Fuse.Cli.Mcp;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// F-021: every file-accepting MCP tool resolves paths through WorkspacePathResolver and refuses ../ escape.
public sealed class WorkspacePathResolverTests
{
    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-path-resolver-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        return dir;
    }

    [Fact]
    public void ResolveWorkspacePath_accepts_a_path_inside_the_root()
    {
        var root = TempRoot();
        try
        {
            var inside = Path.Combine(root, "src", "A.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(inside)!);

            var (success, absolute, error) = WorkspacePathResolver.ResolveWorkspacePath(root, "src/A.cs", "check");

            Assert.True(success);
            Assert.Null(error);
            Assert.Equal(Path.GetFullPath(inside), absolute);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolveWorkspacePath_refuses_parent_segment_escape()
    {
        var root = TempRoot();
        try
        {
            var (success, absolute, error) = WorkspacePathResolver.ResolveWorkspacePath(root, "../escaped.txt", "check");

            Assert.False(success);
            Assert.Null(absolute);
            Assert.NotNull(error);
            Assert.Contains("refusing to check", error);
            Assert.Contains("outside the workspace root", error);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolveWorkspacePath_refuses_an_absolute_path_outside_the_root()
    {
        var root = TempRoot();
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside.txt");
        try
        {
            var (success, absolute, error) = WorkspacePathResolver.ResolveWorkspacePath(root, outside, "reduce");

            Assert.False(success);
            Assert.Null(absolute);
            Assert.NotNull(error);
            Assert.Contains("refusing to reduce", error);
            Assert.Contains("outside the workspace root", error);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FuseCheckAsync_refuses_a_path_escaping_the_root()
    {
        using var provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
        var indexer = provider.GetRequiredService<SemanticIndexer>();
        var root = TempRoot();
        try
        {
            var output = await CheckToolOperations.ExecuteAsync(
                indexer, root, "../escaped.cs", "class X {}", cancellationToken: CancellationToken.None);

            Assert.Contains("refusing to check", output);
            Assert.Contains("outside the workspace root", output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FuseReduceAsync_refuses_a_path_escaping_the_root()
    {
        using var provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<Fuse.Fusion.FusionOrchestrator>();
        var templates = provider.GetRequiredService<Fuse.Collection.Templates.ProjectTemplateRegistry>();
        var root = TempRoot();
        try
        {
            var output = await ReduceToolOperations.ExecuteAsync(
                orchestrator, templates, root, files: ["../escaped.cs"], cancellationToken: CancellationToken.None);

            Assert.Contains("refusing to reduce", output);
            Assert.Contains("outside the workspace root", output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FuseTestAsync_race_refuses_a_candidate_path_escaping_the_root()
    {
        using var provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
        var indexer = provider.GetRequiredService<SemanticIndexer>();
        var root = TempRoot();
        try
        {
            const string candidates = """
                [
                  {"id":"a","file":"Widget.cs","content":"clean"},
                  {"id":"b","file":"../escaped.cs","content":"broken"}
                ]
                """;

            var output = await TestToolOperations.ExecuteAsync(
                indexer, path: root, candidates: candidates, cancellationToken: CancellationToken.None);

            Assert.Contains("refusing to race", output);
            Assert.Contains("outside the workspace root", output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
