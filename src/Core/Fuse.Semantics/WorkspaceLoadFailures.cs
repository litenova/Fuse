using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Fuse.Semantics;

/// <summary>
///     Tracks genuine MSBuild workspace load failures for the compiler-executed refactorers, distinguishing a
///     project that did not load (<see cref="WorkspaceDiagnosticKind.Failure" />) from the routine warnings a
///     healthy multi-project solution emits.
/// </summary>
/// <remarks>
///     A solution-wide refactor must abstain when a project failed to load (the change could be incomplete), but
///     <see cref="MSBuildWorkspace" /> reports many benign diagnostics through its workspace-failure handler:
///     conditions too: an analyzer assembly that cannot be loaded into the workspace, an SDK-resolver note, or a
///     missing optional targets file. Treating every event as a failure made the refactorers abstain on any real
///     solution. This helper collects only Failure-kind diagnostics, so a refactor proceeds through benign
///     warnings and refuses (naming the cause) only on an actual load failure.
/// </remarks>
public static class WorkspaceLoadFailures
{
    /// <summary>
    ///     Registers a workspace-failure handler and returns the live list that accumulates
    ///     only <see cref="WorkspaceDiagnosticKind.Failure" /> messages as the workspace loads.
    /// </summary>
    /// <param name="workspace">The workspace to observe.</param>
    /// <returns>A list that fills with failure messages during load; empty means every project loaded.</returns>
    public static List<string> Track(MSBuildWorkspace workspace)
    {
        var failures = new List<string>();
        _ = workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                failures.Add(e.Diagnostic.Message);
        });
        return failures;
    }
}
