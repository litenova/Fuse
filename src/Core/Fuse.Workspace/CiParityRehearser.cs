namespace Fuse.Workspace;

/// <summary>
///     CI parity rehearsal (G8): scans a workspace's GitHub Actions workflows, extracts the dotnet command
///     sequence each runs, optionally runs the rehearsable ones locally through the same executor T0 uses, and
///     reports the steps that cannot be rehearsed locally as named classes. The contract is classification, not
///     emulation: nothing is silently skipped, so a "local green, CI red" surprise has a named reason here first.
/// </summary>
/// <remarks>
///     Execution is opt-in (<c>run</c>) because a real CI build sequence costs minutes; the report
///     (what will run, what cannot be rehearsed) is always produced and is the primary deliverable. Only a clean
///     leading-<c>dotnet</c> command is executed; a dotnet invocation embedded in another tool (a coverage
///     wrapper) is reported as rehearsable but not auto-run, so the executor is never handed a non-dotnet program.
/// </remarks>
public static class CiParityRehearser
{
    /// <summary>
    ///     Rehearses the CI dotnet steps for a workspace and returns the parity report.
    /// </summary>
    /// <param name="root">The workspace root (its <c>.github/workflows</c> directory is scanned).</param>
    /// <param name="run">When true, executes each clean leading-dotnet command through the executor.</param>
    /// <param name="perCommandTimeout">The timeout applied to each executed command.</param>
    /// <param name="cancellationToken">A token to cancel the rehearsal.</param>
    /// <returns>The parity report: workflows scanned, the command sequence, non-rehearsable steps, and any run results.</returns>
    public static async Task<CiParityReport> RehearseAsync(
        string root, bool run, TimeSpan perCommandTimeout, CancellationToken cancellationToken)
    {
        var workflowsDir = Path.Combine(root, ".github", "workflows");
        if (!Directory.Exists(workflowsDir))
            return new CiParityReport([], [], [], [], "no .github/workflows directory found; nothing to rehearse");

        var files = Directory.EnumerateFiles(workflowsDir, "*.yml")
            .Concat(Directory.EnumerateFiles(workflowsDir, "*.yaml"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var rehearsable = new List<string>();
        var nonRehearsable = new List<string>();
        var scanned = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned.Add(Path.GetFileName(file));
            var parse = CiWorkflowParser.Parse(await File.ReadAllTextAsync(file, cancellationToken));
            foreach (var c in parse.RehearsableCommands)
                if (!rehearsable.Contains(c))
                    rehearsable.Add(c);
            foreach (var s in parse.NonRehearsableSteps)
                if (!nonRehearsable.Contains(s))
                    nonRehearsable.Add(s);
        }

        var results = new List<CiCommandResult>();
        if (run)
        {
            foreach (var command in rehearsable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!command.StartsWith("dotnet ", StringComparison.Ordinal))
                {
                    results.Add(new CiCommandResult(command, null, "skipped: not a clean leading-dotnet command (a wrapped invocation is not auto-run)"));
                    continue;
                }

                var args = SplitArguments(command["dotnet ".Length..]);
                var run1 = await TimedProcess.RunAsync("dotnet", args, root, null, perCommandTimeout, cancellationToken);
                var status = run1.TimedOut ? "timed out" : run1.ExitCode == 0 ? "ok" : $"exit {run1.ExitCode}";
                results.Add(new CiCommandResult(command, run1.ExitCode, status));
            }
        }

        return new CiParityReport(scanned, rehearsable, nonRehearsable, results, null);
    }

    // A minimal argument split honoring double quotes, sufficient for the dotnet CLI lines CI workflows use.
    private static IReadOnlyList<string> SplitArguments(string commandTail)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in commandTail)
        {
            if (ch == '"')
                inQuotes = !inQuotes;
            else if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0) { args.Add(current.ToString()); current.Clear(); }
            }
            else
                current.Append(ch);
        }

        if (current.Length > 0)
            args.Add(current.ToString());
        return args;
    }
}

/// <summary>The result of running one rehearsable CI command (G8).</summary>
/// <param name="Command">The command that was run.</param>
/// <param name="ExitCode">The process exit code, or null when it was skipped or timed out.</param>
/// <param name="Status">A short status: <c>ok</c>, <c>exit N</c>, <c>timed out</c>, or a skip reason.</param>
public sealed record CiCommandResult(string Command, int? ExitCode, string Status);

/// <summary>The CI parity rehearsal report (G8).</summary>
/// <param name="WorkflowsScanned">The workflow file names scanned.</param>
/// <param name="RehearsableCommands">The dotnet commands that can be rehearsed locally, in source order.</param>
/// <param name="NonRehearsableSteps">The steps that cannot be rehearsed locally, each with its named reason.</param>
/// <param name="ExecutionResults">The per-command results when execution was requested; empty otherwise.</param>
/// <param name="Note">A note when there was nothing to rehearse (no workflows), else null.</param>
public sealed record CiParityReport(
    IReadOnlyList<string> WorkflowsScanned,
    IReadOnlyList<string> RehearsableCommands,
    IReadOnlyList<string> NonRehearsableSteps,
    IReadOnlyList<CiCommandResult> ExecutionResults,
    string? Note);
