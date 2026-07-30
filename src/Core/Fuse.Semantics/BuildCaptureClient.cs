using System.Diagnostics;
using System.Text.Json;
using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     The parent side of N4 tier-1 build capture: spawns the out-of-process <c>fuse-build-capture</c> worker,
///     which runs the repository build and rehydrates the semantic graph, and deserializes the graph bundle it
///     emits on stdout. The worker runs in its own process precisely so its Basic.CompilerLog Roslyn closure
///     never shares a process with this parent's MSBuildWorkspace; this client references neither, only the
///     shared <see cref="CaptureResult" /> contract.
/// </summary>
/// <remarks>
///     The worker is located by the <c>FUSE_BUILD_CAPTURE_WORKER</c> environment variable (an absolute path to
///     <c>fuse-build-capture.dll</c>) or an explicit path passed to <see cref="CaptureAsync" />; when neither is
///     set the client reports tier-1 as unavailable rather than guessing, so a deployment without the worker
///     degrades cleanly to the MSBuildWorkspace and syntax tiers.
/// </remarks>
public sealed class BuildCaptureClient
{
    private readonly string? _workerDllPath;
    private readonly IProcessRunner _processRunner;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BuildCaptureClient" /> class.
    /// </summary>
    /// <param name="workerDllPath">An explicit path to the worker dll; when null, the worker is discovered by <see cref="ResolveWorkerPath" />.</param>
    /// <param name="processRunner">The owned process runner used to start and cancel worker process trees.</param>
    public BuildCaptureClient(string? workerDllPath = null, IProcessRunner? processRunner = null)
    {
        _workerDllPath = workerDllPath ?? ResolveWorkerPath();
        _processRunner = processRunner ?? new OwnedProcessRunner();
    }

    /// <summary>Whether a worker dll is configured, so tier-1 build capture can be attempted.</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_workerDllPath) && File.Exists(_workerDllPath);

    /// <summary>
    ///     Resolves the build-capture worker dll: the <c>FUSE_BUILD_CAPTURE_WORKER</c> environment variable when
    ///     set (an explicit override), otherwise the worker published alongside the running tool (C3). The tool
    ///     package ships the worker in an isolated <c>build-capture/</c> subfolder next to <c>fuse.dll</c> so its
    ///     Basic.CompilerLog closure never lands in the parent's own assembly directory; a development build leaves
    ///     it beside the running assembly. Returns null when no worker is found, so tier-1 degrades cleanly.
    /// </summary>
    /// <returns>The absolute path to the worker dll, or null when none is discoverable.</returns>
    public static string? ResolveWorkerPath()
    {
        var configured = Environment.GetEnvironmentVariable("FUSE_BUILD_CAPTURE_WORKER");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, "build-capture", "fuse-build-capture.dll"),
                     Path.Combine(baseDir, "fuse-build-capture.dll"),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    ///     Runs the worker against a build target and returns the deserialized capture result.
    /// </summary>
    /// <param name="buildTarget">The absolute path to the solution or project to build and capture.</param>
    /// <param name="timeout">The maximum time to allow the worker (build plus rehydration) to run.</param>
    /// <param name="cancellationToken">A token to cancel the capture.</param>
    /// <param name="workspaceRoot">The repository root used to normalize captured source paths, or null.</param>
    /// <returns>
    ///     The capture result, or a failed result when the worker is unavailable, times out, or emits no parseable
    ///     output, so the caller falls back to a lower tier.
    /// </returns>
    public async Task<CaptureResult> CaptureAsync(string buildTarget, TimeSpan timeout, CancellationToken cancellationToken, string? workspaceRoot = null)
    {
        if (!IsAvailable)
            return CaptureResult.Failed("build-capture worker not configured (set FUSE_BUILD_CAPTURE_WORKER)");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Fixed, bounded argument list (worker dll, mode, target); never a variable-length list, per the invariant.
        psi.ArgumentList.Add(_workerDllPath!);
        psi.ArgumentList.Add("--build");
        psi.ArgumentList.Add(buildTarget);
        // Pass the workspace root so the worker keys extracted file paths to it (matching the store's root-relative
        // file rows). Two fixed tokens, so the argument list stays bounded.
        if (!string.IsNullOrEmpty(workspaceRoot))
        {
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(workspaceRoot);
        }

        return await RunCaptureAsync(psi, timeout, cancellationToken);
    }

    /// <summary>
    ///     Runs the worker to export a portable compiler log to <paramref name="complogOutPath" /> (C2): the worker
    ///     builds the target, converts the binary log to a complog (no environment block), fail-closed-scans it for
    ///     secrets, and emits the extracted graph on stdout. Returns the graph so the caller can package it in the
    ///     bundle alongside the complog. A failed result (worker unavailable, timeout, build or scan failure) means
    ///     no bundle should be written.
    /// </summary>
    /// <param name="buildTarget">The absolute path to the solution or project to build and capture.</param>
    /// <param name="complogOutPath">The absolute path the worker writes the portable compiler log to.</param>
    /// <param name="timeout">The maximum time to allow the worker to run.</param>
    /// <param name="cancellationToken">A token to cancel the capture.</param>
    /// <param name="workspaceRoot">The repository root used to normalize captured source paths, or null.</param>
    /// <returns>The capture result (the extracted graph) on success, or a failed result.</returns>
    public async Task<CaptureResult> CaptureBundleAsync(
        string buildTarget, string complogOutPath, TimeSpan timeout, CancellationToken cancellationToken, string? workspaceRoot = null)
    {
        if (!IsAvailable)
            return CaptureResult.Failed("build-capture worker not configured (set FUSE_BUILD_CAPTURE_WORKER)");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Fixed, bounded argument list (worker dll, mode, target, complog out); never a variable-length list.
        psi.ArgumentList.Add(_workerDllPath!);
        psi.ArgumentList.Add("--capture-bundle");
        psi.ArgumentList.Add(buildTarget);
        psi.ArgumentList.Add(complogOutPath);
        // Pass the workspace root so the bundle's extracted graph keys file paths to it (portable, root-relative).
        if (!string.IsNullOrEmpty(workspaceRoot))
        {
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(workspaceRoot);
        }

        return await RunCaptureAsync(psi, timeout, cancellationToken);
    }

    /// <summary>
    ///     Runs the worker to merge per-project fragment binary logs into a version-2 bundle's inputs (G4): the
    ///     worker converts each fragment binlog under <paramref name="fragmentsDir" /> to a fail-closed-scanned
    ///     per-project compiler log under <paramref name="complogOutDir" /> and emits the merged extracted graph on
    ///     stdout. Returns the graph so the caller can assemble the bundle from the written complogs.
    /// </summary>
    /// <param name="fragmentsDir">The directory holding per-project fragment binary logs.</param>
    /// <param name="complogOutDir">The directory the worker writes the per-project compiler logs to.</param>
    /// <param name="timeout">The maximum time to allow the worker to run.</param>
    /// <param name="cancellationToken">A token to cancel the merge.</param>
    /// <param name="workspaceRoot">The repository root used to normalize captured source paths, or null.</param>
    /// <returns>The merged graph on success, or a failed result (worker unavailable, timeout, or a secret finding).</returns>
    public async Task<CaptureResult> MergeFragmentsAsync(
        string fragmentsDir, string complogOutDir, TimeSpan timeout, CancellationToken cancellationToken, string? workspaceRoot = null)
    {
        if (!IsAvailable)
            return CaptureResult.Failed("build-capture worker not configured (set FUSE_BUILD_CAPTURE_WORKER)");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Fixed, bounded argument list (worker dll, mode, fragments dir, complog out dir); never a variable list.
        psi.ArgumentList.Add(_workerDllPath!);
        psi.ArgumentList.Add("--merge");
        psi.ArgumentList.Add(fragmentsDir);
        psi.ArgumentList.Add(complogOutDir);
        // Pass the workspace root so the merged graph keys file paths to it (portable, root-relative).
        if (!string.IsNullOrEmpty(workspaceRoot))
        {
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(workspaceRoot);
        }

        return await RunCaptureAsync(psi, timeout, cancellationToken);
    }

    /// <summary>
    ///     Speculatively typechecks a proposed single-file patch via the worker (R1 <c>fuse_check</c>): the worker
    ///     builds and rehydrates the compilation, applies the patch in memory, and returns the compiler
    ///     diagnostics for the changed document.
    /// </summary>
    /// <param name="buildTarget">The absolute path to the solution or project to build and capture.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="timeout">The maximum time to allow the worker to run.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The diagnostics, or an abstention when the worker is unavailable or cannot verify.</returns>
    public Task<CheckResult> CheckAsync(
        string buildTarget, string relativeFilePath, string newContent, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunCheckAsync("--check", buildTarget, relativeFilePath, newContent, timeout, cancellationToken);

    /// <summary>
    ///     Speculatively typechecks a proposed single-file patch against a captured compiler log WITHOUT building
    ///     (C2): the worker rehydrates the compilation from the bundle's portable compiler log and applies the patch
    ///     in memory, returning the compiler diagnostics for the changed document. This is the oracle-grade check
    ///     answer on a machine that cannot restore or build the repository.
    /// </summary>
    /// <param name="complogPath">The absolute path to the bundle's portable compiler log.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="timeout">The maximum time to allow the worker to run.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The diagnostics, or an abstention when the worker is unavailable or the file is not in the log.</returns>
    public Task<CheckResult> CheckFromComplogAsync(
        string complogPath, string relativeFilePath, string newContent, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunCheckAsync("--check-complog", complogPath, relativeFilePath, newContent, timeout, cancellationToken);

    // Spawns the worker's check mode (--check builds the target; --check-complog rehydrates a captured log without
    // building) with a fixed, bounded argument list; the unbounded new content is passed via a temp file, never an
    // argument. Both modes return a CheckResult JSON object on stdout.
    private async Task<CheckResult> RunCheckAsync(
        string mode, string firstArg, string relativeFilePath, string newContent, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return CheckResult.Abstain("build-capture worker not configured (set FUSE_BUILD_CAPTURE_WORKER)");

        var contentFile = Path.Combine(Path.GetTempPath(), $"fuse-check-content-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(contentFile, newContent, cancellationToken);
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // Fixed, bounded argument list; the (unbounded) new content is passed via the temp file, not an arg.
            psi.ArgumentList.Add(_workerDllPath!);
            psi.ArgumentList.Add(mode);
            psi.ArgumentList.Add(firstArg);
            psi.ArgumentList.Add(relativeFilePath);
            psi.ArgumentList.Add(contentFile);

            return await RunCheckAsync(psi, timeout, cancellationToken);
        }
        finally
        {
            try { File.Delete(contentFile); } catch (IOException) { }
        }
    }

    private async Task<CaptureResult> RunCaptureAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var execution = await _processRunner.RunAsync(startInfo, timeout, cancellationToken);
        if (!execution.Started)
            return CaptureResult.Failed($"could not start build-capture worker: {execution.StartError}");
        if (execution.TimedOut)
            return CaptureResult.Failed($"build-capture worker timed out after {timeout.TotalSeconds:F0}s");

        var line = LastJsonLine(execution.StandardOutput);
        if (line is null)
            return CaptureResult.Failed("build-capture worker produced no parseable output");

        try
        {
            return JsonSerializer.Deserialize(line, BuildCaptureJsonContext.Default.CaptureResult)
                   ?? CaptureResult.Failed("build-capture worker output deserialized to null");
        }
        catch (JsonException ex)
        {
            return CaptureResult.Failed($"could not parse build-capture worker output: {ex.Message}");
        }
    }

    private async Task<CheckResult> RunCheckAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var execution = await _processRunner.RunAsync(startInfo, timeout, cancellationToken);
        if (!execution.Started)
            return CheckResult.Abstain($"could not start build-capture worker: {execution.StartError}");
        if (execution.TimedOut)
            return CheckResult.Abstain($"build-capture worker timed out after {timeout.TotalSeconds:F0}s");

        var line = LastJsonLine(execution.StandardOutput);
        if (line is null)
            return CheckResult.Abstain("build-capture worker produced no parseable output");

        try
        {
            return JsonSerializer.Deserialize(line, BuildCaptureJsonContext.Default.CheckResult)
                   ?? CheckResult.Abstain("worker output deserialized to null");
        }
        catch (JsonException ex)
        {
            return CheckResult.Abstain($"could not parse worker output: {ex.Message}");
        }
    }

    private static string? LastJsonLine(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault(line => line.StartsWith('{'));
}
