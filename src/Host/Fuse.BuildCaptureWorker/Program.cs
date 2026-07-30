using System.Text.Json;
using Fuse.BuildCaptureWorker;
using Fuse.Indexing;

// The build-capture worker: a standalone process the main Fuse process spawns for N4 tier-1, so the
// Basic.CompilerLog Roslyn closure never shares a process with the parent's MSBuildWorkspace.
//
//   fuse-build-capture --build <solutionOrProject>   run the build, rehydrate, emit JSON
//   fuse-build-capture --binlog <path>               rehydrate an existing binary log, emit JSON
//
// Output is a single JSON object on stdout: { "succeeded": bool, "reason": string?, "projects": [...] }.
// Exit code 0 on a successful capture, 1 otherwise; diagnostics go to stderr so stdout stays pure JSON.

using var workerCancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    workerCancellation.Cancel();
};
var cancellationToken = workerCancellation.Token;
var rehydrator = new BuildCaptureRehydrator();

// Optional --root <path>: the workspace root the parent indexes against. Extracted file paths are made relative to
// it so symbol and node rows match the consumer's root-relative file rows. Stripped here (not position-dependent)
// so the fixed-length mode checks below operate on the remaining arguments unchanged. Absent for legacy callers,
// which fall back to the project directory basis.
string? workspaceRoot = null;
{
    var argList = new List<string>(args);
    var rootIndex = argList.IndexOf("--root");
    if (rootIndex >= 0 && rootIndex + 1 < argList.Count)
    {
        workspaceRoot = argList[rootIndex + 1];
        argList.RemoveRange(rootIndex, 2);
        args = argList.ToArray();
    }
}

// --check <target> <relativeFilePath> <newContentFile>: speculative typecheck of a proposed single-file patch.
// The new content is passed via a file (not an argument) so it is never a length-bounded command-line value.
if (args.Length == 4 && args[0] == "--check")
{
    CheckResult check;
    try
    {
        var newContent = await File.ReadAllTextAsync(args[3], cancellationToken);
        check = await rehydrator.CheckAsync(args[1], args[2], newContent, TimeSpan.FromMinutes(10), cancellationToken);
    }
    catch (Exception ex)
    {
        check = CheckResult.Abstain($"check error: {ex.Message}");
    }

    Console.Out.WriteLine(JsonSerializer.Serialize(check, BuildCaptureJsonContext.Default.CheckResult));
    return check.Verified ? 0 : 1;
}

// --check-complog <complogPath> <relativeFilePath> <newContentFile>: speculative typecheck of a proposed
// single-file patch against a captured compiler log, WITHOUT building (C2). The oracle-grade check answer on a
// machine that cannot restore or build; the compilation is rehydrated from the bundle's portable compiler log.
if (args.Length == 4 && args[0] == "--check-complog")
{
    CheckResult check;
    try
    {
        var newContent = await File.ReadAllTextAsync(args[3], cancellationToken);
        check = rehydrator.CheckFromLog(args[1], args[2], newContent, cancellationToken);
    }
    catch (Exception ex)
    {
        check = CheckResult.Abstain($"check-complog error: {ex.Message}");
    }

    Console.Out.WriteLine(JsonSerializer.Serialize(check, BuildCaptureJsonContext.Default.CheckResult));
    return check.Verified ? 0 : 1;
}

// --serve-check <complogPath>: a long-lived check worker (R48). Rehydrate the compiler log ONCE, then loop reading
// one JSON CheckRequest per stdin line and writing one CheckResult per stdout line, holding the compilations across
// requests so the rehydrate cost is paid once per session, not per check. The parent forks the in-memory document
// for each speculative edit through CheckHeld. A "ready" line signals rehydration is complete; EOF or "quit" exits.
if (args.Length == 2 && args[0] == "--serve-check")
{
    LazyHeldComplog held;
    try
    {
        held = rehydrator.RehydrateLazyHeld(args[1], cancellationToken);
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"serve-check rehydrate error: {ex.Message}");
        return 1;
    }

    try
    {
        // Signal readiness so the parent knows the (slow) rehydration finished before it sends the first request.
        Console.Out.WriteLine("{\"ready\":true}");
        Console.Out.Flush();

        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (line.Length == 0 || line == "quit")
                break;

            CheckResult check;
            try
            {
                var request = JsonSerializer.Deserialize(line, BuildCaptureJsonContext.Default.CheckRequest);
                if (request is null)
                {
                    check = CheckResult.Abstain("serve-check: unparseable request");
                }
                else
                {
                    var newContent = await File.ReadAllTextAsync(request.ContentPath, cancellationToken);
                    check = rehydrator.CheckLazyHeld(held, request.File, newContent, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                check = CheckResult.Abstain($"serve-check error: {ex.Message}");
            }

            Console.Out.WriteLine(JsonSerializer.Serialize(check, BuildCaptureJsonContext.Default.CheckResult));
            Console.Out.Flush();
        }
    }
    finally
    {
        held.Dispose();
    }

    return 0;
}

// --merge <fragmentsDir> <complogOutDir>: convert per-project fragment binlogs to portable compiler logs
// (fail-closed secret scanned) and emit the merged extracted graph as JSON (the G4 fragment-merge channel).
if (args.Length == 3 && args[0] == "--merge")
{
    CaptureResult merged;
    try
    {
        merged = rehydrator.MergeFragmentsToBundle(args[1], args[2], cancellationToken, workspaceRoot);
    }
    catch (Exception ex)
    {
        merged = CaptureResult.Failed($"merge error: {ex.Message}");
    }

    Console.Out.WriteLine(JsonSerializer.Serialize(merged, BuildCaptureJsonContext.Default.CaptureResult));
    return merged.Succeeded ? 0 : 1;
}

// --capture-bundle <target> <complogOut>: build the target and export a portable compiler log (the C2 capture
// artifact) to <complogOut>, emitting the extracted graph as JSON on stdout so the parent can package both.
if (args.Length == 3 && args[0] == "--capture-bundle")
{
    CaptureResult bundle;
    try
    {
        bundle = await rehydrator.ExportCompilerLogAsync(args[1], args[2], TimeSpan.FromMinutes(10), cancellationToken, workspaceRoot);
    }
    catch (Exception ex)
    {
        bundle = CaptureResult.Failed($"capture-bundle error: {ex.Message}");
    }

    Console.Out.WriteLine(JsonSerializer.Serialize(bundle, BuildCaptureJsonContext.Default.CaptureResult));
    return bundle.Succeeded ? 0 : 1;
}

if (args.Length < 2 || args[0] is not ("--build" or "--binlog"))
{
    await Console.Error.WriteLineAsync("usage: fuse-build-capture (--build <target> | --binlog <path> | --capture-bundle <target> <complogOut> | --merge <fragmentsDir> <complogOutDir> | --check <target> <file> <newContentFile> | --check-complog <complogPath> <file> <newContentFile>) [--root <workspaceRoot>]");
    return 2;
}

CaptureResult result;
try
{
    result = args[0] == "--build"
        ? await rehydrator.CaptureAsync(args[1], TimeSpan.FromMinutes(10), cancellationToken, workspaceRoot)
        : rehydrator.RehydrateFromBinlog(args[1], workspaceRoot, cancellationToken);
}
catch (Exception ex)
{
    result = CaptureResult.Failed($"capture error: {ex.Message}");
}

var json = JsonSerializer.Serialize(result, BuildCaptureJsonContext.Default.CaptureResult);
Console.Out.WriteLine(json);
return result.Succeeded ? 0 : 1;
