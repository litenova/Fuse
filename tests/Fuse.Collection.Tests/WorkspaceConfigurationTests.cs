using Fuse.Collection;
using Xunit;

namespace Fuse.Collection.Tests;

public sealed class WorkspaceConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-workspace-config", Guid.NewGuid().ToString("N"));

    public WorkspaceConfigurationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LoadReadsOnlyRootFuseJson()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        File.WriteAllText(Path.Combine(_root, "nested", "fuse.json"), "{ \"workspace\": \"missing.sln\" }");

        var result = WorkspaceConfiguration.Load(_root);

        Assert.Null(result.Path);
        Assert.Null(result.Error);
        Assert.Null(result.Options.Workspace);
    }

    [Fact]
    public void LoadReportsUnknownPropertiesWithoutRejectingConfiguration()
    {
        File.WriteAllText(Path.Combine(_root, "App.sln"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "fuse.json"), "{ \"workspace\": \"App.sln\", \"legacy\": true }");

        var result = WorkspaceConfiguration.Load(_root);

        Assert.Null(result.Error);
        Assert.Single(result.Warnings);
        Assert.Equal("App.sln", result.Options.Workspace);
    }

    [Theory]
    [InlineData("{ \"workspace\": 1 }")]
    [InlineData("{ \"ignore\": \"generated\" }")]
    [InlineData("{ \"ignore\": [1] }")]
    public void LoadReportsPropertyTypeErrors(string json)
    {
        File.WriteAllText(Path.Combine(_root, "fuse.json"), json);

        var result = WorkspaceConfiguration.Load(_root);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void LoadRejectsWorkspaceOutsideRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "fuse-outside.sln");
        File.WriteAllText(outside, string.Empty);
        try
        {
            File.WriteAllText(Path.Combine(_root, "fuse.json"), "{ \"workspace\": \"../fuse-outside.sln\" }");

            var result = WorkspaceConfiguration.Load(_root);

            Assert.NotNull(result.Error);
            Assert.Contains("workspace", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(outside))
                File.Delete(outside);
        }
    }

    [Fact]
    public void TemplateUsesTheV44SchemaAndWorkspace()
    {
        var template = WorkspaceConfiguration.CreateTemplate("src/App.slnf");

        Assert.Contains(WorkspaceConfiguration.SchemaUrl, template, StringComparison.Ordinal);
        Assert.Contains("src/App.slnf", template, StringComparison.Ordinal);
    }

    // fuse.json is committed, so a workspace path written on Windows has to resolve on Linux and macOS, where a
    // backslash is an ordinary filename character rather than a separator.
    [Fact]
    public void TemplateWritesThePortableSeparator()
    {
        var template = WorkspaceConfiguration.CreateTemplate(Path.Combine("src", "App", "App.csproj"));

        Assert.Contains("src/App/App.csproj", template, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', template);
    }

    [Fact]
    public void LoadNormalizesTheWorkspaceSeparator()
    {
        var projectDirectory = Path.Combine(_root, "src", "App");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "App.csproj"), "<Project />");
        File.WriteAllText(
            Path.Combine(_root, WorkspaceConfiguration.FileName),
            """{ "workspace": "src/App/App.csproj" }""");

        var result = WorkspaceConfiguration.Load(_root);

        Assert.Null(result.Error);
        Assert.Equal("src/App/App.csproj", result.Options.Workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
