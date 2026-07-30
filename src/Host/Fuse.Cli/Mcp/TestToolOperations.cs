using System.Text;
using System.Text.Json;
using Fuse.Cli.Serialization;
using Fuse.Cli.Services;
using Fuse.Retrieval;
using Fuse.Semantics;
using Fuse.Workspace;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Implements <c>fuse_test</c>: run the covering tests for a symbol (T1), or race bounded candidate edits over
///     a resident compilation (F2).
/// </summary>
internal static class TestToolOperations
{
    private static readonly TimeSpan CoveringRunBudget = TimeSpan.FromMinutes(10);

    /// <summary>Runs the test tool and maps a not-ready index to its availability header.</summary>
    /// <param name="indexer">The semantic indexer (opens the store for covering selection).</param>
    /// <param name="symbol">The symbol whose covering tests to run.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="limit">The maximum covering test types to run.</param>
    /// <param name="candidates">A JSON array of single-file edits to race, each <c>{id?, file, content}</c>.</param>
    /// <param name="maxCandidates">The bound on the number of candidates a race accepts.</param>
    /// <param name="analyzers">Whether a race also runs the repository's configured analyzers.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <param name="runtime">The host-owned index, compiler, and job services.</param>
    /// <returns>The per-test verdicts plus the grade, or the selection-only floor when nothing covers the symbol.</returns>
    internal static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        string symbol = "",
        string path = ".",
        int limit = 20,
        string candidates = "",
        int maxCandidates = 4,
        bool analyzers = true,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        IndexedStoreAccess.ExecuteReadMcpAsync(() => DispatchAsync(
            IndexedStoreAccess.ResolveRuntime(runtime, indexer),
            indexer, symbol, path, limit, candidates, maxCandidates, analyzers, cancellationToken));

    private static async Task<string> DispatchAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        string symbol,
        string path,
        int limit,
        string candidates,
        int maxCandidates,
        bool analyzers,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);

        // Candidate racing (F2): when candidates are supplied, race their speculative typechecks over the shared
        // resident compilation and return per-candidate diagnostics plus a strict-dominance winner. This is a
        // distinct verb from the symbol-covering-test run below (no symbol needed).
        if (!string.IsNullOrWhiteSpace(candidates))
            return await RaceAsync(runtime, root, candidates, maxCandidates, analyzers, cancellationToken);

        if (string.IsNullOrWhiteSpace(symbol))
            return "Error: provide a symbol name whose covering tests to run (or candidates to race).";

        return await RunCoveringTestsAsync(runtime, indexer, root, symbol, limit, cancellationToken);
    }

    private static async Task<string> RunCoveringTestsAsync(
        FuseMcpRuntime runtime,
        SemanticIndexer indexer,
        string root,
        string symbol,
        int limit,
        CancellationToken cancellationToken)
    {
        // Covering selection is indexed-tier (R5 tests edges). Validate or warm the complete repository index
        // before selecting tests, using the same bounded cold-start and contention behavior as other indexed reads.
        await using var store = await IndexedStoreAccess.OpenIndexedAsync(runtime, indexer, root, cancellationToken);
        var covering = await new GraphNeighborhoodExplorer(store).CoveringTestsAsync(symbol, limit, cancellationToken);
        if (covering.Count == 0)
        {
            return $"covering tests for {symbol}: none (no tests edge reaches it, so there is nothing to run). "
                + "This is the selection-only floor; a test reached only by reflection has no edge and is not selected.";
        }

        var scopedRun = await new ProjectScopedCoveringTestRunner().RunAsync(
            root,
            covering
                .Where(item => !string.IsNullOrWhiteSpace(item.Symbol))
                .Select(item => new CoveredTestSelection(item.Path, item.Symbol!))
                .ToList(),
            CoveringRunBudget,
            cancellationToken);
        if (scopedRun.ProjectRuns.Count == 0)
        {
            var unowned = scopedRun.UnownedTestFiles.Count == 0
                ? "no selected test type had an owning project"
                : $"{scopedRun.UnownedTestFiles.Count} selected test file(s) had no owning project: {string.Join(", ", scopedRun.UnownedTestFiles)}";
            return $"covering tests for {symbol}: {scopedRun.SelectedTestTypes.Count} test type(s) selected, but {unowned} (selection-only).";
        }

        var results = scopedRun.ProjectRuns.SelectMany(run => run.Result.Verdicts).ToList();
        var passed = results.Count(verdict => verdict.Outcome == "passed");
        var failed = results.Count(verdict => verdict.Outcome == "failed");
        var notRun = results.Count(verdict => verdict.Outcome == "not-run");

        var builder = new StringBuilder();
        builder.AppendLine(
            $"verification grade: build (ran dotnet test scoped to the covering tests in {scopedRun.ProjectRuns.Count} "
            + "owning project(s); the emit fast path is future work)");
        foreach (var run in scopedRun.ProjectRuns)
        {
            var project = Path.GetRelativePath(root, run.ProjectPath).Replace('\\', '/');
            if (run.Result.TimedOut)
                builder.AppendLine($"  {project}: timed out and the test host was killed. Narrow the change or raise the budget.");
            else if (run.Result.Diagnostics is not null)
                builder.AppendLine($"  {project}: {run.Result.Diagnostics}");
        }

        if (scopedRun.UnownedTestFiles.Count > 0)
        {
            builder.AppendLine(
                $"selection-only: {scopedRun.UnownedTestFiles.Count} selected test file(s) had no owning project: "
                + string.Join(", ", scopedRun.UnownedTestFiles));
        }

        builder.AppendLine(
            $"covering tests for {symbol}: {scopedRun.SelectedTestTypes.Count} test type(s), {results.Count} test(s) run "
            + $"- {passed} passed, {failed} failed, {notRun} not-run");
        foreach (var verdict in results
            .OrderBy(verdict => verdict.Outcome == "failed" ? 0 : 1)
            .ThenBy(verdict => verdict.Name, StringComparer.Ordinal))
        {
            builder.AppendLine($"  {verdict.Outcome} {verdict.Name}");
        }

        // Covering types that produced no verdict are reported not-runnable by name, never counted green.
        var notRunnable = CoveringRunAnalysis.NotRunnableTypes(scopedRun.SelectedTestTypes, results);
        if (notRunnable.Count > 0)
        {
            builder.AppendLine($"not-runnable ({notRunnable.Count}; selected but produced no result - a collection error or no runnable test):");
            foreach (var type in notRunnable)
                builder.AppendLine($"  {type}");
        }

        // The graded claims block (U2): a test verdict is compiler/test-grade truth (the real dotnet test ran),
        // so this claim is verified - the strongest grade, distinct from the graph-grade impact claims.
        builder.AppendLine();
        builder.AppendLine(ClaimLedger.Render(
        [
            Claim.FromCompiler(
                $"{passed} of {results.Count} covering test(s) passed ({failed} failed) for {symbol}",
                "test: build-grade dotnet test run over the covering set"),
        ]));

        return builder.ToString().TrimEnd();
    }

    // Candidate racing (F2): parse the candidates argument, verify their speculative typechecks over the live
    // resident compilation (each candidate forks the immutable base and rebinds only its own changed tree, so k
    // candidates cost far less than k full verifies), and render per-candidate diagnostics plus a winner by strict
    // dominance. Candidates are evaluated one fork at a time (F2's Fallback: measured, concurrent Roslyn binding
    // over shared-base forks serializes, so parallelism buys no wall-clock and only multiplies fork memory).
    // Racing is the fork-cheap typecheck primitive; per-candidate test execution needs the emit path (T1's
    // descoped follow-up), so a race reports diagnostics, not test verdicts. Abstains when no resident workspace
    // serves the root (a build per candidate would not be a race), naming the requirement.
    private static async Task<string> RaceAsync(
        FuseMcpRuntime runtime,
        string root,
        string candidatesJson,
        int maxCandidates,
        bool analyzers,
        CancellationToken cancellationToken)
    {
        RaceCandidateInput[]? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(candidatesJson, FuseCliJsonContext.Default.RaceCandidateInputArray);
        }
        catch (JsonException ex)
        {
            return $"Error: candidates must be a JSON array of {{id?, file, content}} objects ({ex.Message}).";
        }

        if (parsed is null || parsed.Length == 0)
            return "Error: candidates is empty. Provide a JSON array of at least two {id?, file, content} edits to race.";
        if (parsed.Length < 2)
            return "Error: racing needs at least two candidates (one candidate is a single fuse_check, not a race).";

        var bound = Math.Max(2, maxCandidates);
        if (parsed.Length > bound)
        {
            return $"Error: {parsed.Length} candidates exceed the bound k={bound} (racing is bounded to protect memory "
                + "under k forks; raise maxCandidates deliberately).";
        }

        // Build the candidate list, filling a blank id with the 1-based position so every verdict is attributable.
        var candidates = new List<RaceCandidate>(parsed.Length);
        for (var index = 0; index < parsed.Length; index++)
        {
            var input = parsed[index];
            if (string.IsNullOrWhiteSpace(input.File) || input.Content is null)
                return $"Error: candidate {index + 1} needs a file and content.";
            var (resolved, _, error) = WorkspacePathResolver.ResolveWorkspacePath(root, input.File, "race");
            if (!resolved)
                return error!;
            candidates.Add(new RaceCandidate(
                string.IsNullOrWhiteSpace(input.Id) ? $"#{index + 1}" : input.Id!,
                input.File,
                input.Content));
        }

        // The per-candidate check is the resident overlay typecheck: the fork-cheap primitive racing depends on. A
        // null result means no held compilation covers the file, surfaced as not-applicable. When no resident
        // workspace serves the root every candidate is not-applicable, so abstain rather than pretend a race ran.
        var report = await CandidateRacer.RaceAsync(
            (candidate, ct) => runtime.ResidentWorkspaces.TryCheckOverlayAsync(root, candidate.File, candidate.Content, analyzers, ct),
            candidates,
            cancellationToken);

        if (report.Verdicts.All(verdict => !verdict.Applicable))
        {
            return "cannot race (abstain): no resident workspace serves this root, so the speculative overlay could not "
                + "typecheck any candidate. Start the server with FUSE_RESIDENT=1 for candidate racing (a build per "
                + "candidate would not be a race).";
        }

        return RenderRace(report, candidates.Count);
    }

    private static string RenderRace(RaceReport report, int candidateCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"verification grade: oracle (speculative typecheck over the resident compilation; k={candidateCount} "
            + "candidates verified over the shared held compilation, no build, no disk write)");
        builder.AppendLine($"race of {candidateCount} candidate(s):");
        foreach (var verdict in report.Verdicts)
        {
            if (!verdict.Applicable)
            {
                builder.AppendLine($"  [{verdict.Id}] not-applicable (no held compilation covers {verdict.File})");
                continue;
            }

            var warnings = verdict.WarningCount > 0 ? $", {verdict.WarningCount} warning(s)" : string.Empty;
            var status = verdict.IsClean
                ? $"clean (0 errors{warnings})"
                : $"{verdict.ErrorCount} error(s){warnings}";
            builder.AppendLine($"  [{verdict.Id}] {status}  {verdict.File}");
            foreach (var diagnostic in verdict.Diagnostics.Where(diagnostic => diagnostic.Severity == "Error"))
                builder.AppendLine($"      {diagnostic.Severity} {diagnostic.Id} at line {diagnostic.Line}: {diagnostic.Message}");
        }

        var notClean = report.Verdicts.Count(verdict => !verdict.IsClean);
        builder.AppendLine();
        if (report.WinnerId is not null)
            builder.AppendLine($"winner: {report.WinnerId} (the only clean candidate; it strictly dominates the {notClean} with errors or not applicable).");
        else if (report.Tie)
            builder.AppendLine($"winner: none - {report.Clean.Count} candidates are equally clean (a tie; strict dominance cannot choose a green over a green). Pick by another axis (tests, diff size, style).");
        else
            builder.AppendLine("winner: none - no candidate is clean (every candidate introduced an error). Fix the errors above; the repair packets from fuse_check name the specific fixes.");

        // The graded claims block (U2): each candidate verdict rests on the resident compiler overlay, so the race
        // outcome is compiler-grade truth (verified), the strongest grade - the same overlay fuse_check uses.
        builder.AppendLine();
        builder.AppendLine(ClaimLedger.Render(
        [
            Claim.FromCompiler(
                report.WinnerId is not null
                    ? $"candidate {report.WinnerId} typechecks clean and the other {notClean} do not"
                    : $"{report.Clean.Count} of {report.Verdicts.Count} raced candidates typecheck clean",
                "race: speculative overlay typecheck per candidate over the resident compilation"),
        ]));

        return builder.ToString().TrimEnd();
    }
}
