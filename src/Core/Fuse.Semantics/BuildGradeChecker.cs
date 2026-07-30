using System.Diagnostics;
using System.Text.RegularExpressions;
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
///         Tree safety (Decision D2): the working tree is never written. The owning project of the changed file is
///         copied to a temporary directory (build outputs excluded), the proposed content replaces the one file in
///         that copy, and the copy's <c>&lt;ProjectReference&gt;</c> includes are rewritten to absolute paths
///         pointing at the untouched original sibling projects, so the temp build compiles the edited project
///         against the real dependency closure without ever writing the real file.
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

    private static readonly Regex ProjectReferenceInclude = new(
        "(?<attr>Include\\s*=\\s*\")(?<rel>[^\"]+?\\.csproj)(\")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        var owningDir = Path.GetDirectoryName(owningProject)!;
        var fileRelativeToProject = Path.GetRelativePath(owningDir, fileAbsolute);

        var tempRoot = Path.Combine(Path.GetTempPath(), "fuse-build-grade", Guid.NewGuid().ToString("N"));
        try
        {
            var tempProjectFile = MirrorProject(owningProject, owningDir, tempRoot);

            // Apply the proposed content in the copy (creating the file if the edit adds it).
            var tempChangedFile = Path.GetFullPath(Path.Combine(tempRoot, fileRelativeToProject));
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

    // Copies the owning project directory to the temp root, skipping build/tooling output directories, and
    // rewrites the copied .csproj's <ProjectReference> includes to absolute paths pointing at the untouched
    // originals, so the temp build sees the real dependency closure without touching the tree. Returns the path
    // to the copied project file.
    private static string MirrorProject(string owningProject, string owningDir, string tempRoot)
    {
        Directory.CreateDirectory(tempRoot);
        foreach (var source in Directory.EnumerateFiles(owningDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(owningDir, source);
            if (IsExcludedPath(relative))
                continue;
            var destination = Path.Combine(tempRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }

        var tempProjectFile = Path.Combine(tempRoot, Path.GetFileName(owningProject));
        var projectText = File.ReadAllText(tempProjectFile);
        var rewritten = ProjectReferenceInclude.Replace(projectText, match =>
        {
            var rel = match.Groups["rel"].Value;
            var absolute = Path.GetFullPath(Path.Combine(owningDir, rel));
            return $"{match.Groups["attr"].Value}{absolute}\"";
        });
        if (!string.Equals(rewritten, projectText, StringComparison.Ordinal))
            File.WriteAllText(tempProjectFile, rewritten);

        return tempProjectFile;
    }

    private static bool IsExcludedPath(string relativePath)
    {
        foreach (var segment in relativePath.Split('/', '\\'))
        {
            if (segment is "bin" or "obj" or ".vs" or ".git")
                return true;
        }

        return false;
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
}
