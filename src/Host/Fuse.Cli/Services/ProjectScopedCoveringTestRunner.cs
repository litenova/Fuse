using Fuse.Semantics;
using Fuse.Workspace;

namespace Fuse.Cli.Services;

internal sealed record CoveredTestSelection(string FilePath, string TypeName);

internal sealed record ProjectTestRun(string ProjectPath, TestRunResult Result);

internal sealed record ProjectScopedTestRun(
    IReadOnlyList<string> SelectedTestTypes,
    IReadOnlyList<string> UnownedTestFiles,
    IReadOnlyList<ProjectTestRun> ProjectRuns);

internal sealed class ProjectScopedCoveringTestRunner
{
    private readonly ProjectOwnershipResolver _ownershipResolver;
    private readonly Func<string, string, string, TimeSpan, CancellationToken, Task<TestRunResult>> _runTests;

    public ProjectScopedCoveringTestRunner(
        ProjectOwnershipResolver? ownershipResolver = null,
        Func<string, string, string, TimeSpan, CancellationToken, Task<TestRunResult>>? runTests = null)
    {
        _ownershipResolver = ownershipResolver ?? new ProjectOwnershipResolver();
        _runTests = runTests ?? BuildGradeTestRunner.RunAsync;
    }

    public async Task<ProjectScopedTestRun> RunAsync(
        string root,
        IReadOnlyList<CoveredTestSelection> selections,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var selected = selections
            .Where(selection => !string.IsNullOrWhiteSpace(selection.FilePath)
                && !string.IsNullOrWhiteSpace(selection.TypeName))
            .Distinct()
            .ToList();
        var projects = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unowned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var selection in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ownership = await _ownershipResolver.ResolveAsync(root, selection.FilePath, cancellationToken);
            if (!ownership.IsResolved)
            {
                unowned.Add(selection.FilePath);
                continue;
            }

            foreach (var project in ownership.ProjectPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!projects.TryGetValue(project, out var types))
                    projects[project] = types = [];
                if (!types.Contains(selection.TypeName, StringComparer.Ordinal))
                    types.Add(selection.TypeName);
            }
        }

        var runs = new List<ProjectTestRun>();
        foreach (var (project, types) in projects.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scratch = Path.Combine(Path.GetTempPath(), "fuse-test", Guid.NewGuid().ToString("N"));
            try
            {
                var result = await _runTests(
                    project,
                    TestFilterBuilder.BuildContains(types),
                    scratch,
                    timeout,
                    cancellationToken);
                runs.Add(new ProjectTestRun(project, result));
            }
            finally
            {
                try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        return new ProjectScopedTestRun(
            selected.Select(selection => selection.TypeName).Distinct(StringComparer.Ordinal).ToList(),
            unowned.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            runs);
    }
}
