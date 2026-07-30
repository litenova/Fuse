using Fuse.Cli.Services;
using Fuse.Workspace;
using Xunit;

namespace Fuse.Cli.Tests.Services;

public sealed class ProjectScopedCoveringTestRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-project-tests", Guid.NewGuid().ToString("N"));

    public ProjectScopedCoveringTestRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Write("tests/One/One.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("tests/One/OneTests.cs", "public sealed class OneTests { }");
        Write("tests/Two/Two.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("tests/Two/TwoTests.cs", "public sealed class TwoTests { }");
    }

    [Fact]
    public async Task Runs_each_owning_project_with_only_its_covering_types()
    {
        var calls = new List<(string Project, string Filter)>();
        var runner = new ProjectScopedCoveringTestRunner(
            runTests: (project, filter, _, _, _) =>
            {
                calls.Add((project, filter));
                return Task.FromResult(new TestRunResult([], TimedOut: false, RanNothing: false, Diagnostics: null));
            });

        var result = await runner.RunAsync(
            _root,
            [
                new CoveredTestSelection("tests/One/OneTests.cs", "OneTests"),
                new CoveredTestSelection("tests/Two/TwoTests.cs", "TwoTests"),
            ],
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.Empty(result.UnownedTestFiles);
        Assert.Equal(2, result.ProjectRuns.Count);
        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, call => call.Project.EndsWith("tests\\One\\One.csproj", StringComparison.OrdinalIgnoreCase)
            && call.Filter.Contains("OneTests", StringComparison.Ordinal)
            && !call.Filter.Contains("TwoTests", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.Project.EndsWith("tests\\Two\\Two.csproj", StringComparison.OrdinalIgnoreCase)
            && call.Filter.Contains("TwoTests", StringComparison.Ordinal)
            && !call.Filter.Contains("OneTests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reports_selected_test_files_without_an_owner()
    {
        var runner = new ProjectScopedCoveringTestRunner(
            runTests: (_, _, _, _, _) => throw new Xunit.Sdk.XunitException("No process should start for an unowned file."));

        var result = await runner.RunAsync(
            _root,
            [new CoveredTestSelection("tests/LooseTests.cs", "LooseTests")],
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.Empty(result.ProjectRuns);
        Assert.Equal(["tests/LooseTests.cs"], result.UnownedTestFiles);
    }

    [Fact]
    public async Task Runs_a_linked_test_source_in_each_owning_project()
    {
        Write("tests/Linked/Linked.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="../One/OneTests.cs" Link="OneTests.cs" />
              </ItemGroup>
            </Project>
            """);
        var calls = new List<string>();
        var runner = new ProjectScopedCoveringTestRunner(
            runTests: (project, _, _, _, _) =>
            {
                calls.Add(project);
                return Task.FromResult(new TestRunResult([], TimedOut: false, RanNothing: false, Diagnostics: null));
            });

        var result = await runner.RunAsync(
            _root,
            [new CoveredTestSelection("tests/One/OneTests.cs", "OneTests")],
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.Equal(2, result.ProjectRuns.Count);
        Assert.Contains(calls, project => project.EndsWith("tests\\One\\One.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(calls, project => project.EndsWith("tests\\Linked\\Linked.csproj", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
