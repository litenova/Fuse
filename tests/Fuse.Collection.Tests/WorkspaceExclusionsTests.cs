using Fuse.Collection;
using Xunit;

namespace Fuse.Collection.Tests;

// Shared exclusion set: sensible defaults, .fuseignore extension, and nested VCS-root detection.
public sealed class WorkspaceExclusionsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-exclusions-tests", Guid.NewGuid().ToString("N"));

    public WorkspaceExclusionsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DefaultsCoverBuildToolingAndAgentDirectories()
    {
        var names = WorkspaceExclusions.DefaultDirectoryNames;

        foreach (var expected in new[] { "bin", "obj", ".git", ".fuse", ".claude", "node_modules", ".vs" })
            Assert.Contains(expected, names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadMergesFuseIgnoreNames()
    {
        File.WriteAllText(Path.Combine(_root, ".fuseignore"), "# my extra excludes\ngenerated\nsome/legacy/\n\n");

        var names = WorkspaceExclusions.LoadDirectoryNames(_root);

        Assert.Contains("generated", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("legacy", names, StringComparer.OrdinalIgnoreCase); // path reduced to final segment
        Assert.DoesNotContain("# my extra excludes", names); // comment ignored
    }

    [Fact]
    public void LoadMergesFuseJsonIgnoreArray()
    {
        // R25: a fuse.json "ignore" array adds directory names to the exclusion set, alongside defaults.
        File.WriteAllText(Path.Combine(_root, "fuse.json"), "{ \"ignore\": [\"vendored\", \"gen/output/\"] }");

        var names = WorkspaceExclusions.LoadDirectoryNames(_root);

        Assert.Contains("vendored", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("output", names, StringComparer.OrdinalIgnoreCase); // path reduced to final segment
        Assert.Contains("node_modules", names, StringComparer.OrdinalIgnoreCase); // defaults still present
    }

    [Fact]
    public void LoadRejectsFuseJsonIgnoreSingleString()
    {
        File.WriteAllText(Path.Combine(_root, "fuse.json"), "{ \"ignore\": \"thirdparty\" }");

        var exception = Assert.Throws<WorkspaceConfigurationException>(
            () => WorkspaceExclusions.LoadDirectoryNames(_root));

        Assert.Contains("ignore", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadWithoutFuseIgnoreReturnsDefaults()
    {
        var names = WorkspaceExclusions.LoadDirectoryNames(_root);

        Assert.Equal(WorkspaceExclusions.DefaultDirectoryNames.Count, names.Count);
    }

    [Fact]
    public void IsVcsRootDetectsGitFileAndDirectory()
    {
        var worktree = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /somewhere/.git/worktrees/x");

        var clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(Path.Combine(clone, ".git"));

        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        Assert.True(WorkspaceExclusions.IsVcsRoot(worktree));
        Assert.True(WorkspaceExclusions.IsVcsRoot(clone));
        Assert.False(WorkspaceExclusions.IsVcsRoot(plain));
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
