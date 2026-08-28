using System.Diagnostics;
using System.Text.RegularExpressions;
using Fuse.Collection;
using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     The build-grade rung of the verification-grade ladder (T0, Decision D11): when no oracle-grade substrate is
///     available (tier-1 build capture not configured, or the capture could not verify), verify a proposed
///     single-file edit by running the real <c>dotnet build</c> toolchain and parsing its diagnostics into the
///     same <see cref="CheckResult" /> shape a speculative check returns. This is ground truth (the compiler
///     itself answered) at the cost of build latency (tens of seconds), so the verify verb never shrugs where the
///     toolchain can run.
/// </summary>
/// <remarks>
///     <para>
///         Tree safety (Decision D2): the working tree is never written. The repository source tree is copied to a
///         temporary directory (build outputs excluded), then the proposed content replaces one file in that copy.
///         Keeping the original relative layout preserves SDK default items, project references, and linked
///         <c>Compile</c> items while the selected project builds without writing the real file.
///     </para>
///     <para>
///         Scope is the owning project only, a correct lower bound: a break the edit introduces in the edited file
///         surfaces, matching the single-file scope of the speculative <c>fuse_check</c>. A break that lands only
///         in a dependent project is out of this rung's scope (it is the whole-solution oracle's job) and is a
///         named follow-up.
///     </para>
/// </remarks>
public sealed class BuildGradeChecker
{
    // The canonical MSBuild diagnostic line (T0 precondition A, confirmed on this SDK):
    //   <fullpath>(line,col): error CS####: message [projectpath]
    // Multiline so each build-output line is matched independently; the trailing "[project]" is optional.
    private static readonly Regex DiagnosticLine = new(
        @"^(?<path>.+?)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+(?<id>[A-Za-z]+\d+):\s+(?<msg>.*?)(\s+\[[^\]]+\])?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly TimeSpan _timeout;
    private readonly IProcessRunner _processRunner;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BuildGradeChecker" /> class.
    /// </summary>
    /// <param name="timeout">
    ///     The maximum time to allow the scoped build; a build that outruns it is classified as an abstention with
    ///     a timeout reason (a build-grade verify never blocks forever). Defaults to 240 seconds.
    /// </param>
    /// <param name="processRunner">The host-owned runner that kills the build process tree when its token is cancelled.</param>
    public BuildGradeChecker(TimeSpan? timeout = null, IProcessRunner? processRunner = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(240);
        _processRunner = processRunner ?? new OwnedProcessRunner();
    }

    /// <summary>
    ///     Runs a build-grade check of a proposed single-file edit.
    /// </summary>
    /// <param name="rootDirectory">The absolute workspace root the changed path is relative to.</param>
    /// <param name="projectPaths">The discovered project file paths (absolute) used to attribute the file to a project.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>
    ///     A build-grade <see cref="CheckResult" /> (clean or with the changed-document diagnostics), or an
    ///     abstention naming why the toolchain could not verify (no owning project, timeout, or a build failure
    ///     with no parseable diagnostics such as a restore error).
    /// </returns>
    public async Task<CheckResult> CheckAsync(
        string rootDirectory,
        IReadOnlyList<string> projectPaths,
        string relativeFilePath,
        string newContent,
        CancellationToken cancellationToken)
    {
        var fileAbsolute = Path.GetFullPath(Path.Combine(rootDirectory, relativeFilePath));
        var owningProject = LongestOwningProject(fileAbsolute, projectPaths);
        if (owningProject is null)
            return CheckResult.Abstain(
                $"cannot build-verify: '{relativeFilePath}' is not under any discovered project directory");

        return await CheckProjectsAsync(
            rootDirectory,
            [owningProject],
            relativeFilePath,
            newContent,
            cancellationToken);
    }

    /// <summary>
    ///     Runs a build-grade check against every project that explicitly owns the edited source file.
    /// </summary>
    /// <param name="rootDirectory">The repository root the source path is relative to.</param>
    /// <param name="ownership">The project ownership resolved from SDK defaults and explicit compile items.</param>
    /// <param name="relativeFilePath">The repository-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full replacement content.</param>
    /// <param name="cancellationToken">A token to cancel the scoped builds.</param>
    /// <returns>A merged build-grade result, or an abstention when no owner can build the edit.</returns>
    public Task<CheckResult> CheckAsync(
        string rootDirectory,
        ProjectOwnership ownership,
        string relativeFilePath,
        string newContent,
        CancellationToken cancellationToken) =>
        ownership.IsResolved
            ? CheckProjectsAsync(rootDirectory, ownership.ProjectPaths, relativeFilePath, newContent, cancellationToken)
            : Task.FromResult(CheckResult.Abstain(
                $"cannot build-verify: '{relativeFilePath}' is not included by any discovered project"));

    private async Task<CheckResult> CheckProjectsAsync(
        string rootDirectory,
        IReadOnlyList<string> owningProjects,
        string relativeFilePath,
        string newContent,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<CheckDiagnostic>();
        foreach (var owningProject in owningProjects.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await CheckProjectAsync(
                rootDirectory,
                owningProject,
                relativeFilePath,
                newContent,
                cancellationToken);
            if (!result.Verified)
                return result;
            diagnostics.AddRange(result.Diagnostics);
        }

        var distinctDiagnostics = diagnostics
            .GroupBy(diagnostic =>
                $"{diagnostic.Id}\u001f{diagnostic.Severity}\u001f{diagnostic.FilePath}\u001f{diagnostic.Line}\u001f{diagnostic.Message}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        return CheckResult.BuildGraded(distinctDiagnostics);
    }

    private async Task<CheckResult> CheckProjectAsync(
        string rootDirectory,
        string owningProject,
        string relativeFilePath,
        string newContent,
        CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fuse-build-grade", Guid.NewGuid().ToString("N"));
        try
        {
            var tempProjectFile = MirrorWorkspace(rootDirectory, owningProject, tempRoot, cancellationToken);

            // Apply the proposed content in the copy (creating the file if the edit adds it).
            var tempChangedFile = Path.GetFullPath(Path.Combine(tempRoot, relativeFilePath));
            Directory.CreateDirectory(Path.GetDirectoryName(tempChangedFile)!);
            await File.WriteAllTextAsync(tempChangedFile, newContent, cancellationToken);

            var (exitCode, timedOut, output) = await RunBuildAsync(tempProjectFile, cancellationToken);
            if (timedOut)
                return CheckResult.Abstain($"cannot build-verify: dotnet build exceeded {_timeout.TotalSeconds:F0}s");

            var diagnostics = ParseChangedFileDiagnostics(output, tempChangedFile, relativeFilePath);

            // A nonzero exit with no diagnostics attributed to the changed file means either the build failed
            // outside this file (restore error, a break in another file) or the toolchain could not run. If there
            // are no diagnostics anywhere in the output, the toolchain itself did not produce compiler diagnostics
            // (for example a restore/NU error), so abstain honestly rather than reporting a false green.
            if (exitCode != 0 && diagnostics.Count == 0 && !DiagnosticLine.IsMatch(output))
                return CheckResult.Abstain(
                    $"cannot build-verify: dotnet build failed with no parseable compiler diagnostics ({FirstNonEmptyLine(output)})");

            return CheckResult.BuildGraded(diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CheckResult.Abstain($"cannot build-verify: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Copies the repository source layout to a temporary root, skipping build/tooling output directories. A linked
    // Compile item can point outside its owning project directory, so copying only that project would silently
    // build the original linked file rather than the proposed edit. The preserved relative layout keeps project
    // references and linked source paths inside the disposable mirror.
    private static string MirrorWorkspace(
        string rootDirectory,
        string owningProject,
        string tempRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(tempRoot);
        var excludedDirectories = new HashSet<string>(
            WorkspaceExclusions.LoadDirectoryNames(rootDirectory),
            StringComparer.OrdinalIgnoreCase);
        foreach (var source in EnumerateMirrorFiles(rootDirectory, excludedDirectories, cancellationToken))
        {
            var relative = Path.GetRelativePath(rootDirectory, source);
            var destination = Path.Combine(tempRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }

        return Path.Combine(tempRoot, Path.GetRelativePath(rootDirectory, owningProject));
    }

    private static IEnumerable<string> EnumerateMirrorFiles(
        string rootDirectory,
        IReadOnlySet<string> excludedDirectories,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(rootDirectory);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(child);
                if (excludedDirectories.Contains(name)
                    || (!PathsEqual(child, rootDirectory) && WorkspaceExclusions.IsVcsRoot(child)))
                    continue;
                pending.Push(child);
            }
        }
    }

    // The owning project is the project whose directory is the longest ancestor of the file, so a file in a nested
    // folder is attributed to its closest enclosing project rather than an ancestor one.
    private static string? LongestOwningProject(string fileAbsolute, IReadOnlyList<string> projectPaths)
    {
        string? best = null;
        var bestLength = -1;
        foreach (var project in projectPaths)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(project));
            if (dir is null)
                continue;
            var prefix = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
            if (fileAbsolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && dir.Length > bestLength)
            {
                best = project;
                bestLength = dir.Length;
            }
        }

        return best;
    }

    private static IReadOnlyList<CheckDiagnostic> ParseChangedFileDiagnostics(
        string output, string tempChangedFile, string reportedPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<CheckDiagnostic>();
        foreach (Match match in DiagnosticLine.Matches(output))
        {
            var path = match.Groups["path"].Value.Trim();
            if (!PathsEqual(path, tempChangedFile))
                continue;

            var line = int.Parse(match.Groups["line"].Value);
            var col = match.Groups["col"].Value;
            var id = match.Groups["id"].Value;
            if (!seen.Add($"{line}:{col}:{id}"))
                continue;

            var severity = match.Groups["sev"].Value == "error" ? "Error" : "Warning";
            // Report the diagnostic against the repo-relative path the caller passed, not the temp mirror.
            diagnostics.Add(new CheckDiagnostic(id, severity, match.Groups["msg"].Value.Trim(), reportedPath, line));
        }

        return diagnostics;
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string FirstNonEmptyLine(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length > 0)
                return line.Length > 200 ? line[..200] : line;
        }

        return "no output";
    }

    // Runs `dotnet build <tempProject> -nologo -v:minimal` with a fixed, bounded argument list (never a
    // variable-length list, per the change-safety invariant) and the configured timeout. Minimal verbosity emits
    // per-diagnostic error/warning lines (quiet suppresses them); normal is not needed.
    private async Task<(int ExitCode, bool TimedOut, string Output)> RunBuildAsync(
        string tempProjectFile, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(tempProjectFile)!,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(tempProjectFile);
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v:minimal");

        var execution = await _processRunner.RunAsync(psi, _timeout, cancellationToken);
        return (
            execution.ExitCode ?? -1,
            execution.TimedOut,
            string.Concat(execution.StandardOutput, Environment.NewLine, execution.StandardError));
    }

    /// <summary>
    ///     Runs a scoped <c>dotnet build</c> of one project (in a temporary mirror, tree-safe) and returns the
    ///     number of error-severity diagnostics the real toolchain reports, or null when the build could not run
    ///     (timeout, a failure with no parseable compiler diagnostics, or a missing toolchain). This is the
    ///     ground truth the load-tier reconciliation uses: a project whose in-process design-time compilation is
    ///     incomplete (for example a Razor/Blazor project whose generator did not load in-process) can still build
    ///     clean with the real SDK, and that clean build is the honest basis for its tier.
    /// </summary>
    /// <param name="rootDirectory">The repository root the project path is relative to.</param>
    /// <param name="projectPath">The absolute path to the project file to build.</param>
    /// <param name="cancellationToken">Cancels the scoped build.</param>
    /// <returns>The error count of the scoped build, or null when the toolchain could not verify.</returns>
    internal async Task<int?> CountProjectErrorsAsync(string rootDirectory, string projectPath, CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fuse-build-grade", Guid.NewGuid().ToString("N"));
        try
        {
            var tempProjectFile = MirrorWorkspace(rootDirectory, projectPath, tempRoot, cancellationToken);
            var (exitCode, timedOut, output) = await RunBuildAsync(tempProjectFile, cancellationToken);
            if (timedOut)
                return null;

            var errorCount = DiagnosticLine.Matches(output).Count(m => m.Groups["sev"].Value == "error");
            // A nonzero exit with no compiler diagnostics anywhere means the toolchain itself did not produce
            // compiler output (a restore/NU failure), so the tier cannot be verified from this build.
            if (exitCode != 0 && !DiagnosticLine.IsMatch(output))
                return null;

            return errorCount;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
