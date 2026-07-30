using Fuse.Collection;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// P3.1: workspace discovery order (solution > projects > syntax-only) and ignore rules.
public sealed class DotNetWorkspaceDiscovererTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-discover-tests", Guid.NewGuid().ToString("N"));
    private readonly DotNetWorkspaceDiscoverer _discoverer = new();

    public DotNetWorkspaceDiscovererTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task PrefersSingleSolution()
    {
        Write("App.sln", "");
        Write("src/App/App.csproj", "<Project/>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.NotNull(result.SolutionPath);
        Assert.EndsWith("App.sln", result.SolutionPath);
        Assert.Single(result.ProjectPaths);
    }

    [Fact]
    public async Task PrefersSlnOverSlnx()
    {
        Write("App.sln", "");
        Write("App.slnx", "");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.EndsWith("App.sln", result.SolutionPath);
    }

    [Fact]
    public async Task UsesSlnxWhenNoSln()
    {
        Write("App.slnx", "");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.EndsWith("App.slnx", result.SolutionPath);
    }

    [Fact]
    public async Task MultipleRootSolutions_RequiresExplicitWorkspace()
    {
        // R24: several distinct root-level solutions are resolved by a documented rule (name order) and the choice
        // is surfaced, rather than silently dropping to projects mode.
        Write("A.sln", "aaa");
        Write("B.sln", "bbb");
        Write("src/App/App.csproj", "<Project/>");

        var exception = await Assert.ThrowsAsync<WorkspaceConfigurationException>(
            () => _discoverer.DiscoverAsync(_root, CancellationToken.None));

        Assert.Contains("workspace selection is ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workspace", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrefersRootSolutionOverNestedFixtureSolution()
    {
        // R24 reproduction: the Fuse repo has a root Fuse.slnx and a nested tests/.../SampleShop.sln; discovery
        // must bind the root solution, not the fixture one.
        Write("Repo.slnx", "root");
        Write("tests/Fixture/Sample.sln", "fixture");
        Write("src/App/App.csproj", "<Project/>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.EndsWith("Repo.slnx", result.SolutionPath);
        Assert.Null(result.SelectionNote); // an unambiguous root solution needs no warning.
    }

    [Fact]
    public async Task FuseJsonWorkspaceOverride_PinsTarget()
    {
        Write("App.sln", "app");
        Write("Custom.sln", "custom");
        Write("fuse.json", "{ \"workspace\": \"Custom.sln\" }");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.EndsWith("Custom.sln", result.SolutionPath);
        Assert.Contains("workspace pinned by fuse.json", result.SelectionNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrefersSingleRootSolutionFilter()
    {
        Write("App.sln", "solution");
        Write("App.slnf", "{ }");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.EndsWith("App.slnf", result.SolutionPath);
    }

    [Fact]
    public async Task PinnedProjectLoadsProjectWorkspace()
    {
        Write("src/App/App.csproj", "<Project/>");
        Write("fuse.json", "{ \"workspace\": \"src/App/App.csproj\" }");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Projects, result.Kind);
        Assert.Single(result.ProjectPaths);
        Assert.EndsWith("App.csproj", result.ProjectPaths[0]);
    }

    [Fact]
    public async Task AllSolutionsUnderFixtureDirs_LoadsNonFixtureProjectsInstead()
    {
        // R24: never silently load a fixture solution as the repo's semantic tier when the repo has real projects.
        Write("tests/Fixture/Sample.sln", "fixture");
        Write("src/App/App.csproj", "<Project/>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Projects, result.Kind);
        Assert.Null(result.SolutionPath);
        Assert.Contains(result.ProjectPaths, p => p.Contains("App.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(result.SelectionNote);
    }

    [Fact]
    public async Task FallsBackToSyntaxOnlyWhenNoProjectOrSolution()
    {
        Write("src/Loose.cs", "class Loose { }");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.SyntaxOnly, result.Kind);
        Assert.Empty(result.ProjectPaths);
    }

    [Fact]
    public async Task IgnoresBuildDirectories()
    {
        Write("src/App/App.csproj", "<Project/>");
        Write("src/App/bin/Debug/Stale.csproj", "<Project/>");
        Write("obj/Generated.csproj", "<Project/>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Projects, result.Kind);
        Assert.Single(result.ProjectPaths);
        Assert.DoesNotContain(result.ProjectPaths, p => p.Contains("bin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.ProjectPaths, p => p.Contains("obj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IgnoresClaudeWorktrees()
    {
        // Claude Code creates full duplicate checkouts under .claude/worktrees; discovery must not descend into
        // them, or a single-solution repo looks like a multi-solution one and the projects are indexed in copies.
        Write("App.sln", "");
        Write("src/App/App.csproj", "<Project/>");
        Write(".claude/worktrees/wf_1/App.sln", "");
        Write(".claude/worktrees/wf_1/src/App/App.csproj", "<Project/>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.EndsWith("App.sln", result.SolutionPath);
        Assert.Single(result.ProjectPaths);
        Assert.DoesNotContain(result.ProjectPaths, p => p.Contains(".claude", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IgnoresNestedRepositoryRoots()
    {
        // A nested checkout anywhere (not just under .claude) is a separate repo; its .git marks it, and its
        // projects must not be discovered even though the directory has no excluded name.
        Write("App.sln", "");
        Write("src/App/App.csproj", "<Project/>");
        Write("external/Lib/.git", "gitdir: /elsewhere");
        Write("external/Lib/Lib.csproj", "<Project/>");
        Write("external/Lib/Lib.sln", "");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.Single(result.ProjectPaths);
        Assert.DoesNotContain(result.ProjectPaths, p => p.Contains("external", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CollapsesIdenticalSolutionCopies()
    {
        // Two byte-identical .sln copies (a duplicated tree not under a VCS root) must not flip the single-solution
        // decision into projects mode; they collapse to one canonical solution.
        Write("App.sln", "Microsoft Visual Studio Solution File, Format Version 12.00");
        Write("backup/App.sln", "Microsoft Visual Studio Solution File, Format Version 12.00");
        Write("src/App/App.csproj", "<Project/>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.NotNull(result.SolutionPath);
    }

    [Fact]
    public async Task PrefersProductSolutionOverBuildToolingSolution()
    {
        // Reproduces the NodaTime case: a build/ tooling solution sorts alphabetically before the src/ product
        // solution ("build" < "src"), but the product solution must win. Distinct non-empty content so neither is
        // collapsed as a copy.
        Write("build/Tools.slnx", "<Solution>tools</Solution>");
        Write("src/NodaTime.slnx", "<Solution>product</Solution>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.EndsWith("NodaTime.slnx", result.SolutionPath);
        Assert.Null(result.SelectionNote); // A single product solution is unambiguous; the tooling one is silently deprioritized.
    }

    [Fact]
    public async Task DeprioritizesEngAndScriptsAndToolsDirectories()
    {
        Write("eng/Eng.slnx", "<Solution>eng</Solution>");
        Write("scripts/Scripts.slnx", "<Solution>scripts</Solution>");
        Write("tools/Tools.slnx", "<Solution>tools</Solution>");
        Write("App.slnx", "<Solution>app</Solution>"); // root product solution

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.EndsWith("App.slnx", result.SolutionPath);
    }

    [Fact]
    public async Task BuildOnlySolutionLoadsProductProjectsInstead()
    {
        // Only a build/ tooling solution exists, but the repo has product projects under src/: load those projects
        // rather than binding the tooling solution as the repo's semantic tier.
        Write("build/Tools.slnx", "<Solution>tools</Solution>");
        Write("src/App/App.csproj", "<Project/>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Projects, result.Kind);
        Assert.Contains(result.ProjectPaths, p => p.EndsWith("App.csproj"));
        Assert.NotNull(result.SelectionNote);
        Assert.Contains("build/tooling", result.SelectionNote!);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
