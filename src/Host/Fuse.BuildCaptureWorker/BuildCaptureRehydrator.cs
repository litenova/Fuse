using System.Diagnostics;
using Basic.CompilerLog.Util;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Microsoft.CodeAnalysis;

namespace Fuse.BuildCaptureWorker;

/// <summary>
///     Runs a repository's own build once with a binary log and rehydrates exact Roslyn compilations from the
///     recorded compiler invocations (N4 tier-1). Lives in the standalone worker executable so its
///     Basic.CompilerLog.Util Roslyn closure never shares a process with the parent's MSBuildWorkspace, which the
///     two would conflict over; this type never invokes MSBuildWorkspace.
/// </summary>
public sealed class BuildCaptureRehydrator
{
    private readonly IProcessRunner _processRunner;

    /// <summary>
    ///     Initializes a build-capture rehydrator with an owned process runner for its compiler build.
    /// </summary>
    /// <param name="processRunner">The runner that terminates this worker's build tree on cancellation or timeout.</param>
    public BuildCaptureRehydrator(IProcessRunner? processRunner = null) =>
        _processRunner = processRunner ?? new OwnedProcessRunner();

    /// <summary>
    ///     Builds the target and rehydrates its C# compilations, reporting each project's outcome.
    /// </summary>
    /// <param name="buildTarget">The absolute path to the solution or project to build.</param>
    /// <param name="buildTimeout">The maximum time to allow the build to run.</param>
    /// <param name="cancellationToken">A token to cancel the capture.</param>
    /// <param name="workspaceRoot">The workspace root used to make captured source paths relative.</param>
    /// <returns>The capture result: the achieved outcome plus one entry per rehydrated C# compilation.</returns>
    public async Task<CaptureResult> CaptureAsync(
        string buildTarget, TimeSpan buildTimeout, CancellationToken cancellationToken, string? workspaceRoot = null)
    {
        var binlogPath = Path.Combine(Path.GetTempPath(), $"fuse-capture-{Guid.NewGuid():N}.binlog");
        try
        {
            var (exitCode, timedOut, firstError) = await RunBuildAsync(buildTarget, binlogPath, buildTimeout, cancellationToken);
            if (timedOut)
                return CaptureResult.Failed($"build timed out after {buildTimeout.TotalSeconds:F0}s");
            if (exitCode != 0 || !File.Exists(binlogPath))
                return CaptureResult.Failed(firstError is null ? $"build failed (exit {exitCode})" : $"build failed ({firstError})");

            return RehydrateFromBinlog(binlogPath, workspaceRoot, cancellationToken);
        }
        finally
        {
            TryDelete(binlogPath);
        }
    }

    /// <summary>
    ///     Builds the target and exports a portable compiler log (<c>.complog</c>) to <paramref name="complogPath" />
    ///     (C2). The complog packages the compiler inputs (source, reference closure, generated documents, and
    ///     command lines) self-contained and, unlike the binary log, WITHOUT the build's environment variables, so
    ///     it is the artifact a bundle ships. Also rehydrates and returns the extracted graph so the caller can
    ///     package the graph alongside the complog. The binary log is deleted; the complog is left at the path.
    /// </summary>
    /// <param name="buildTarget">The absolute path to the solution or project to build.</param>
    /// <param name="complogPath">The absolute path to write the portable compiler log to.</param>
    /// <param name="buildTimeout">The maximum time to allow the build to run.</param>
    /// <param name="cancellationToken">A token to cancel the capture.</param>
    /// <param name="workspaceRoot">The workspace root used to make captured source paths relative.</param>
    /// <returns>The capture result (the extracted graph) on success, or a concrete failure; the complog is at <paramref name="complogPath" /> on success.</returns>
    public async Task<CaptureResult> ExportCompilerLogAsync(
        string buildTarget, string complogPath, TimeSpan buildTimeout, CancellationToken cancellationToken, string? workspaceRoot = null)
    {
        var binlogPath = Path.Combine(Path.GetTempPath(), $"fuse-capture-{Guid.NewGuid():N}.binlog");
        try
        {
            var (exitCode, timedOut, firstError) = await RunBuildAsync(buildTarget, binlogPath, buildTimeout, cancellationToken);
            if (timedOut)
                return CaptureResult.Failed($"build timed out after {buildTimeout.TotalSeconds:F0}s");
            if (exitCode != 0 || !File.Exists(binlogPath))
                return CaptureResult.Failed(firstError is null ? $"build failed (exit {exitCode})" : $"build failed ({firstError})");

            // Convert the binary log to the portable complog (all recorded compiler calls). The complog is a zip of
            // the compiler inputs with no environment block, which is the C2 secret posture (the binlog never ships).
            var conversion = CompilerLogUtil.ConvertBinaryLog(binlogPath, complogPath, static _ => true);
            if (!File.Exists(complogPath))
            {
                var reason = conversion.Count > 0 ? conversion[0] : "no compiler calls were recorded";
                return CaptureResult.Failed($"compiler-log export produced no file ({reason})");
            }

            // Fail-closed secret scan (C2): scan the build-injected artifacts the complog newly exposes (generated
            // documents and additional files) with the shipped redactor. Any finding, or any error that prevents a
            // complete scan, deletes the complog and fails the capture, naming the match class and artifact but
            // never the secret value. A partial capture that might ship a secret is never preferred over abstaining.
            ComplogSecretFinding? finding;
            try
            {
                finding = ComplogSecretScanner.ScanCompilerLog(complogPath, redactor: null, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDelete(complogPath);
                return CaptureResult.Failed($"secret scan could not complete ({ex.GetType().Name}); the compiler log was not kept (fail closed)");
            }

            if (finding is not null)
            {
                TryDelete(complogPath);
                return CaptureResult.Failed($"secret scan failed closed: a {finding.Kind} secret was detected in {finding.Label}; the compiler log was not kept");
            }

            // Rehydrate the graph from the binlog too, so the bundle carries the extracted graph next to the complog.
            return RehydrateFromBinlog(binlogPath, workspaceRoot, cancellationToken);
        }
        finally
        {
            TryDelete(binlogPath);
        }
    }

    /// <summary>
    ///     Speculatively typechecks a proposed single-file patch: builds and rehydrates the compilations, replaces
    ///     the target file's syntax tree with the proposed content in memory, and returns the compiler diagnostics
    ///     for that document (R1). No disk write of the patch, no second build.
    /// </summary>
    /// <param name="buildTarget">The absolute path to the solution or project to build and capture.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="buildTimeout">The maximum time to allow the capture build to run.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The diagnostics for the changed document, or an abstention when capture is unavailable.</returns>
    public async Task<CheckResult> CheckAsync(
        string buildTarget, string relativeFilePath, string newContent, TimeSpan buildTimeout, CancellationToken cancellationToken)
    {
        var binlogPath = Path.Combine(Path.GetTempPath(), $"fuse-check-{Guid.NewGuid():N}.binlog");
        try
        {
            var (exitCode, timedOut, firstError) = await RunBuildAsync(buildTarget, binlogPath, buildTimeout, cancellationToken);
            if (timedOut || exitCode != 0 || !File.Exists(binlogPath))
                return CheckResult.Abstain(timedOut ? "capture build timed out" : $"capture build did not succeed ({firstError ?? $"exit {exitCode}"}); cannot verify");

            return CheckFromLog(binlogPath, relativeFilePath, newContent, cancellationToken);
        }
        finally
        {
            TryDelete(binlogPath);
        }
    }

    /// <summary>
    ///     Speculatively typechecks a proposed single-file patch against a captured compiler log (a <c>.complog</c>
    ///     or a binary log) WITHOUT building (C2). Rehydrates the compilations recorded in the log, replaces the
    ///     target file's syntax tree with the proposed content in memory, and returns the compiler diagnostics for
    ///     that document. This is the oracle-grade check answer on a machine that cannot restore or build: the
    ///     compilation comes from the bundle's portable compiler log, not a fresh build.
    /// </summary>
    /// <param name="logPath">The path to the portable compiler log (or binary log) to rehydrate.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The diagnostics for the changed document, or an abstention when the file is not in the log.</returns>
    public CheckResult CheckFromLog(
        string logPath, string relativeFilePath, string newContent, CancellationToken cancellationToken)
    {
        using var held = RehydrateHeld(logPath, cancellationToken);
        return CheckHeld(held, relativeFilePath, newContent, cancellationToken);
    }

    /// <summary>
    ///     Rehydrates and holds the C# compilations recorded in a compiler log (R48), so a long-lived worker can
    ///     answer many <see cref="CheckHeld" /> requests against them without re-rehydrating per check. The reader
    ///     is kept alive for the returned handle's lifetime because a rehydrated compilation resolves some inputs
    ///     lazily through it; <see cref="HeldComplog.Dispose" /> releases the reader.
    /// </summary>
    /// <param name="logPath">The path to the portable compiler log (or binary log) to rehydrate.</param>
    /// <param name="cancellationToken">A token to cancel the rehydration.</param>
    /// <returns>The held compilations, disposable.</returns>
    public HeldComplog RehydrateHeld(string logPath, CancellationToken cancellationToken)
    {
        var reader = CompilerCallReaderUtil.Create(logPath);
        var compilations = new List<Compilation>();
        try
        {
            foreach (var data in reader.ReadAllCompilationData())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (data.CompilerCall.IsCSharp != true)
                    continue;
                compilations.Add(NormalizeSigning(data.GetCompilationAfterGenerators(cancellationToken), data.CompilerCall.ProjectFilePath));
            }
        }
        catch
        {
            reader.Dispose();
            throw;
        }

        return new HeldComplog(reader, compilations);
    }

    /// <summary>
    ///     Opens a compiler log for lazy pooled checks (R53). Compiler-call source metadata is read up front, but
    ///     the costly Roslyn compilation is deferred until a request names a source file that call owns.
    /// </summary>
    public LazyHeldComplog RehydrateLazyHeld(string logPath, CancellationToken cancellationToken)
    {
        var reader = CompilerCallReaderUtil.Create(logPath);
        try
        {
            var calls = reader.ReadAllCompilationData()
                .Where(static data => data.CompilerCall.IsCSharp == true)
                .ToList();
            var sourcePaths = calls
                .Select(data => reader.ReadAllSourceTextData(data.CompilerCall)
                    .Select(source => source.FilePath.Replace('\\', '/'))
                    .ToList())
                .ToList();
            return new LazyHeldComplog(reader, calls, sourcePaths);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Speculatively typechecks a proposed single-file patch against already-held compilations (R48): the exact
    ///     same fork-and-diagnostics as <see cref="CheckFromLog" />, over the held compilations rather than a fresh
    ///     rehydration, so a pooled worker's verdict is identical to the spawn-per-call verdict.
    /// </summary>
    /// <param name="held">The held compilations from <see cref="RehydrateHeld" />.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The diagnostics for the changed document, or an abstention when the file is not in the log.</returns>
    public CheckResult CheckHeld(
        HeldComplog held, string relativeFilePath, string newContent, CancellationToken cancellationToken)
        => CheckCompilations(held.Compilations, relativeFilePath, newContent, cancellationToken);

    /// <summary>
    ///     Typechecks a single-file overlay through a lazy held compiler log. Every captured compiler invocation
    ///     containing the file is rehydrated on first touch, preserving the original log order and the same
    ///     fork-and-diagnostics behavior as <see cref="CheckHeld"/>.
    /// </summary>
    public CheckResult CheckLazyHeld(
        LazyHeldComplog held, string relativeFilePath, string newContent, CancellationToken cancellationToken)
    {
        var compilations = held.GetCompilationsFor(relativeFilePath, NormalizeSigning, cancellationToken);
        return CheckCompilations(compilations, relativeFilePath, newContent, cancellationToken);
    }

    private static CheckResult CheckCompilations(
        IReadOnlyList<Compilation> compilations, string relativeFilePath, string newContent, CancellationToken cancellationToken)
    {
        var normalized = relativeFilePath.Replace('\\', '/');
        foreach (var compilation in compilations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tree = compilation.SyntaxTrees.FirstOrDefault(t =>
                t.FilePath.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
            if (tree is null)
                continue; // The changed file is not in this project; try the next.

            var newTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                newContent, (Microsoft.CodeAnalysis.CSharp.CSharpParseOptions?)tree.Options, tree.FilePath, cancellationToken: cancellationToken);
            var forked = compilation.ReplaceSyntaxTree(tree, newTree);
            var diagnostics = forked.GetSemanticModel(newTree)
                .GetDiagnostics(cancellationToken: cancellationToken)
                .Where(d => d.Severity is Microsoft.CodeAnalysis.DiagnosticSeverity.Error or Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
                .Select(ToCheckDiagnostic)
                .ToList();
            return CheckResult.Ok(diagnostics);
        }

        return CheckResult.Abstain($"the changed file '{relativeFilePath}' was not found in any captured C# project");
    }

    // Normalizes strong-name signing on a rehydrated compilation before its diagnostics are read. The compilation
    // is analyzed (symbols, wiring graph, typecheck), never emitted, but a captured compiler call records the
    // signing key by a RELATIVE path (for example "../signing.snk") that does not resolve in the rehydration
    // sandbox, so Roslyn reports an emit-output signing error (CS7027) the real build never hit - and worse, an
    // empty signing key breaks InternalsVisibleTo(PublicKey=...) matching, cascading into CS0281 (friend-access
    // key mismatch) and a CS0122 for every internal member a strong-named test project touches. None of these are
    // code errors; all are strong-name artifacts of rehydration. The fix keeps signing correct where possible:
    // resolve the relative key file against the project directory (the repos commit the .snk), so the real public
    // key is produced and friend access matches. Only when the key genuinely is not in the checkout do we clear
    // the key settings, which at least removes the spurious CS7027. We rehydrate only a build that already
    // succeeded (exit 0), so neither branch can mask a genuine build failure.
    internal static Microsoft.CodeAnalysis.Compilation NormalizeSigning(
        Microsoft.CodeAnalysis.Compilation compilation, string? projectFilePath)
    {
        if (compilation.Options is not Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions csOptions)
            return compilation;

        var resolvedKey = ResolveKeyFile(csOptions.CryptoKeyFile, projectFilePath);
        if (resolvedKey is not null)
            // Keep signing, with the key file resolved to an absolute path: the real public key is produced, so
            // InternalsVisibleTo(PublicKey=...) between a strong-named library and its test project still matches.
            return compilation.WithOptions(csOptions.WithCryptoKeyFile(resolvedKey));

        var neutralized = csOptions
            .WithCryptoKeyFile(null)
            .WithCryptoKeyContainer(null)
            .WithCryptoPublicKey(System.Collections.Immutable.ImmutableArray<byte>.Empty)
            .WithDelaySign(null)
            .WithPublicSign(false);
        return compilation.WithOptions(neutralized);
    }

    // Resolves a recorded signing key file to an existing absolute path: an already-rooted path if it exists, else
    // the relative path against the project directory (where the compiler resolved it at build time). Returns null
    // when no key file is recorded or it cannot be found in the checkout.
    private static string? ResolveKeyFile(string? cryptoKeyFile, string? projectFilePath)
    {
        if (string.IsNullOrEmpty(cryptoKeyFile))
            return null;
        if (Path.IsPathRooted(cryptoKeyFile))
            return File.Exists(cryptoKeyFile) ? cryptoKeyFile : null;
        var projectDir = projectFilePath is null ? null : Path.GetDirectoryName(projectFilePath);
        if (projectDir is null)
            return null;
        var candidate = Path.GetFullPath(Path.Combine(projectDir, cryptoKeyFile));
        return File.Exists(candidate) ? candidate : null;
    }

    private static CheckDiagnostic ToCheckDiagnostic(Microsoft.CodeAnalysis.Diagnostic d)
    {
        var span = d.Location.IsInSource ? d.Location.GetLineSpan() : default;
        return new CheckDiagnostic(
            Id: d.Id,
            Severity: d.Severity.ToString(),
            Message: d.GetMessage(),
            FilePath: d.Location.IsInSource ? d.Location.SourceTree?.FilePath : null,
            Line: d.Location.IsInSource ? span.StartLinePosition.Line + 1 : 0);
    }

    /// <summary>
    ///     Merges per-project fragment binary logs into a version-2 bundle's inputs (G4): converts each fragment
    ///     binlog to a portable per-project compiler log under <paramref name="complogOutDir" /> (fail-closed
    ///     secret scanned, exactly as a direct capture) and returns the merged extracted graph. The caller
    ///     assembles the bundle from the written complogs and the returned graph. Fails closed: any secret finding
    ///     or scan error deletes every written complog and returns a failure, so a merged bundle never ships a
    ///     secret a fragment exposed.
    /// </summary>
    /// <param name="fragmentsDir">The directory holding per-project fragment binary logs (<c>*.binlog</c>).</param>
    /// <param name="complogOutDir">The directory to write the per-project portable compiler logs to.</param>
    /// <param name="cancellationToken">A token to cancel the merge.</param>
    /// <param name="workspaceRoot">The workspace root used to make captured source paths relative.</param>
    /// <returns>The merged graph, or a failure when no fragment recorded a C# compilation or a secret was found.</returns>
    public CaptureResult MergeFragmentsToBundle(string fragmentsDir, string complogOutDir, CancellationToken cancellationToken, string? workspaceRoot = null)
    {
        if (!Directory.Exists(fragmentsDir))
            return CaptureResult.Failed($"fragments directory not found: {fragmentsDir}");
        var binlogs = Directory.GetFiles(fragmentsDir, "*.binlog").OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (binlogs.Count == 0)
            return CaptureResult.Failed("no fragment binary logs (*.binlog) found to merge");

        Directory.CreateDirectory(complogOutDir);
        var written = new List<string>();
        var index = 0;
        foreach (var binlog in binlogs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var complog = Path.Combine(complogOutDir, $"fragment-{index:D4}.complog");
            index++;
            CompilerLogUtil.ConvertBinaryLog(binlog, complog, static _ => true);
            if (!File.Exists(complog))
                continue; // A fragment with no C# compiler call produces no complog; the merge unions the rest.

            ComplogSecretFinding? finding;
            try
            {
                finding = ComplogSecretScanner.ScanCompilerLog(complog, redactor: null, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                written.Add(complog);
                foreach (var w in written) TryDelete(w);
                return CaptureResult.Failed($"secret scan could not complete ({ex.GetType().Name}); no bundle was kept (fail closed)");
            }

            if (finding is not null)
            {
                written.Add(complog);
                foreach (var w in written) TryDelete(w);
                return CaptureResult.Failed($"secret scan failed closed: a {finding.Kind} secret was detected in {finding.Label}; no bundle was kept");
            }

            written.Add(complog);
        }

        if (written.Count == 0)
            return CaptureResult.Failed("no fragment recorded a C# compilation");

        return MergeFragments(binlogs, cancellationToken, workspaceRoot);
    }

    /// <summary>
    ///     Merges per-project capture fragments (G4): rehydrates each fragment log and unions the resulting
    ///     projects into one capture result, so a bundle assembled from fragments carries the same extracted graph
    ///     as a direct whole-solution capture. Each fragment is a per-project binary (or compiler) log; a fragment
    ///     that records no C# compilation contributes nothing. The union is deduplicated by project path (or name)
    ///     and target framework, so a fragment that a dependency build caused to also record a referenced project
    ///     does not double-count it while a multi-target project keeps every target's facts.
    /// </summary>
    /// <param name="fragmentLogPaths">The per-project fragment log paths (binary or compiler logs).</param>
    /// <param name="cancellationToken">A token to cancel the merge.</param>
    /// <param name="workspaceRoot">The workspace root used to make captured source paths relative.</param>
    /// <returns>The unioned capture result, or a failure when no fragment recorded a C# compilation.</returns>
    public CaptureResult MergeFragments(IReadOnlyList<string> fragmentLogPaths, CancellationToken cancellationToken, string? workspaceRoot = null)
    {
        var merged = new List<RehydratedProject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fragment in fragmentLogPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(fragment))
                continue;
            foreach (var project in RehydrateProjects(fragment, workspaceRoot, cancellationToken))
            {
                // Keep one compiler invocation per project and target framework. A project can legitimately appear
                // once for each target, and the canonical union downstream needs all of those facts.
                var key = $"{(string.IsNullOrEmpty(project.Project.FilePath) ? project.Project.Name : project.Project.FilePath)}\u001f{project.Project.TargetFramework}";
                if (seen.Add(key))
                    merged.Add(project);
            }
        }

        return merged.Count == 0
            ? CaptureResult.Failed("no capture fragment recorded a C# compilation")
            : CaptureResult.Ok(ProjectCoveringTestEdges(merged, workspaceRoot, cancellationToken));
    }

    /// <summary>
    ///     Rehydrates the C# compilations recorded in a binary log. Exposed so a test can rehydrate a binlog it
    ///     produced without re-running a build.
    /// </summary>
    /// <param name="binlogPath">The path to the binary log.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The capture result with one entry per rehydrated C# compilation.</returns>
    public CaptureResult RehydrateFromBinlog(string binlogPath, CancellationToken cancellationToken)
        => RehydrateFromBinlog(binlogPath, workspaceRoot: null, cancellationToken);

    /// <summary>
    ///     Rehydrates every C# compilation in the binary log and extracts the symbol and wiring graph, keying file
    ///     paths relative to <paramref name="workspaceRoot" /> so they match the consumer's root-relative file rows.
    /// </summary>
    /// <param name="binlogPath">The path to the binary log.</param>
    /// <param name="workspaceRoot">
    ///     The workspace root the consumer indexes against. Symbol, node, route, DI, and options file paths are made
    ///     relative to it, so they resolve against the root-relative <c>files.normalized_path</c> the store links
    ///     foreign keys against. When null or empty, falls back to each project's directory (the legacy basis, which
    ///     only matched when the project sat at the workspace root).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The capture result with one entry per rehydrated C# compilation.</returns>
    public CaptureResult RehydrateFromBinlog(string binlogPath, string? workspaceRoot, CancellationToken cancellationToken)
    {
        var projects = RehydrateProjects(binlogPath, workspaceRoot, cancellationToken);
        return projects.Count == 0
            ? CaptureResult.Failed("the build log recorded no C# compiler invocations")
            : CaptureResult.Ok(ProjectCoveringTestEdges(projects, workspaceRoot, cancellationToken));
    }

    private static List<RehydratedProject> RehydrateProjects(
        string binlogPath, string? workspaceRoot, CancellationToken cancellationToken)
    {
        using var reader = CompilerCallReaderUtil.Create(binlogPath);
        var symbolExtractor = new SemanticSymbolExtractor();
        var analyzers = SemanticAnalysisRunner.CreateDefault();
        var projects = new List<RehydratedProject>();
        foreach (var data in reader.ReadAllCompilationData())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = data.CompilerCall;
            if (call.IsCSharp != true)
                continue;

            var compilation = NormalizeSigning(data.GetCompilationAfterGenerators(cancellationToken), call.ProjectFilePath);
            var errorCount = compilation.GetDiagnostics(cancellationToken)
                .Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            var typeCount = CountTypes(compilation.Assembly.GlobalNamespace);

            // Run Fuse's semantic extraction over the rehydrated compilation (never MSBuildWorkspace), the crux of
            // tier-1: the worker produces the same symbol and wiring-graph data the in-process semantic pass does.
            var projectDir = Path.GetDirectoryName(call.ProjectFilePath) ?? Directory.GetCurrentDirectory();
            // Normalize file paths against the workspace root (not the project directory) so symbol and node rows
            // match the root-relative files.normalized_path the store links foreign keys against. Falling back to the
            // project directory reproduces the pre-fix basis, which only matched when a project sat at the root; on a
            // nested layout it produced project-relative paths that never resolved, dropping every symbol.
            var normalizeRoot = string.IsNullOrEmpty(workspaceRoot) ? projectDir : workspaceRoot;
            var loaded = new LoadedProject(
                Name: Path.GetFileNameWithoutExtension(call.ProjectFilePath) ?? call.ProjectFileName ?? "project",
                FilePath: call.ProjectFilePath ?? "",
                AssemblyName: compilation.AssemblyName,
                Compilation: compilation);
            var symbols = symbolExtractor.Extract(loaded, normalizeRoot, cancellationToken);
            var graph = analyzers.Run(new SemanticAnalysisContext(loaded, normalizeRoot), cancellationToken);

            projects.Add(new RehydratedProject(new CapturedProject(
                Name: loaded.Name,
                FilePath: loaded.FilePath,
                AssemblyName: compilation.AssemblyName,
                ErrorCount: errorCount,
                TypeCount: typeCount,
                SymbolCount: symbols.Count,
                NodeCount: graph.Nodes.Count,
                EdgeCount: graph.Edges.Count,
                Symbols: symbols,
                Nodes: graph.Nodes,
                Edges: graph.Edges,
                Routes: graph.Routes,
                DiRegistrations: graph.DiRegistrations,
                OptionsBindings: graph.OptionsBindings,
                TargetFramework: call.TargetFramework),
                loaded));
        }

        return projects;
    }

    // TestEdgeExtractor deliberately runs after every project's wiring graph exists, because test code commonly
    // references production types from a different project. The normal workspace path does this in
    // SemanticIndexer.RunAnalyzers; build capture must do the equivalent projection before serializing its graph.
    // Keep target frameworks separate so CanonicalTfmUnion can record the edge availability correctly.
    private static IReadOnlyList<CapturedProject> ProjectCoveringTestEdges(
        IReadOnlyList<RehydratedProject> captures, string? workspaceRoot, CancellationToken cancellationToken)
    {
        var projected = captures.Select(c => c.Project).ToList();
        foreach (var targetGroup in captures
            .Select((capture, index) => (Capture: capture, Index: index))
            .GroupBy(x => x.Capture.Project.TargetFramework ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var members = targetGroup.ToList();
            var nodesById = new Dictionary<string, int>(StringComparer.Ordinal);
            var existingNodeIds = new HashSet<string>(StringComparer.Ordinal);
            var allEdges = new List<SemanticEdgeRecord>();
            foreach (var member in members)
            {
                foreach (var node in projected[member.Index].Nodes ?? [])
                {
                    existingNodeIds.Add(node.NodeId);
                    nodesById.TryAdd(node.NodeId, member.Index);
                }

                allEdges.AddRange(projected[member.Index].Edges ?? []);
            }

            var diResolvesTo = allEdges
                .Where(edge => edge.EdgeType == "di_resolves_to")
                .GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group.Select(edge => edge.ToNodeId).Distinct(StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal);
            var root = !string.IsNullOrEmpty(workspaceRoot)
                ? workspaceRoot
                : Path.GetDirectoryName(members[0].Capture.Project.FilePath) ?? Directory.GetCurrentDirectory();
            var (testNodes, testEdges) = new TestEdgeExtractor().Extract(
                members.Select(member => member.Capture.Loaded).ToList(),
                existingNodeIds,
                diResolvesTo,
                root,
                cancellationToken);

            var nodesToAdd = new Dictionary<int, List<NodeRecord>>();
            foreach (var node in testNodes)
            {
                if (nodesById.TryGetValue(node.NodeId, out var owner))
                    (nodesToAdd.TryGetValue(owner, out var list) ? list : nodesToAdd[owner] = []).Add(node);
            }

            var edgesToAdd = new Dictionary<int, List<SemanticEdgeRecord>>();
            foreach (var edge in testEdges)
            {
                if (nodesById.TryGetValue(edge.FromNodeId, out var owner))
                    (edgesToAdd.TryGetValue(owner, out var list) ? list : edgesToAdd[owner] = []).Add(edge);
            }

            foreach (var member in members)
            {
                var hasNodes = nodesToAdd.TryGetValue(member.Index, out var memberNodes);
                var hasEdges = edgesToAdd.TryGetValue(member.Index, out var memberEdges);
                if (!hasNodes && !hasEdges)
                    continue;

                var nodes = (projected[member.Index].Nodes ?? [])
                    .Concat(memberNodes ?? [])
                    .GroupBy(node => node.NodeId, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();
                var edges = (projected[member.Index].Edges ?? [])
                    .Concat(memberEdges ?? [])
                    .GroupBy(edge => (edge.FromNodeId, edge.ToNodeId, edge.EdgeType, edge.EvidenceFilePath), EqualityComparer<(string, string, string, string?)>.Default)
                    .Select(group => group.First())
                    .ToList();
                projected[member.Index] = projected[member.Index] with
                {
                    Nodes = nodes,
                    NodeCount = nodes.Count,
                    Edges = edges,
                    EdgeCount = edges.Count,
                };
            }
        }

        return projected;
    }

    private sealed record RehydratedProject(CapturedProject Project, LoadedProject Loaded);

    private static int CountTypes(INamespaceSymbol ns)
    {
        var count = ns.GetTypeMembers().Length;
        foreach (var child in ns.GetNamespaceMembers())
            count += CountTypes(child);
        return count;
    }

    // Runs `dotnet build <target> -bl:<binlog>` with a fixed, bounded argument list (never a variable-length
    // path or id list, per the change-safety invariant) and a timeout. `--no-incremental` is required: rehydration
    // reads the C# compiler (Csc) invocations from the binary log, but an already-built or up-to-date repository
    // builds incrementally and emits NO Csc invocations, so the rehydrator would see an empty log and fail with
    // "the build log recorded no C# compiler invocations". Forcing a non-incremental build makes every project
    // compile, so the binlog always carries the Csc calls tier-1 needs (the cost is a full compile per capture).
    private async Task<(int ExitCode, bool TimedOut, string? FirstError)> RunBuildAsync(
        string buildTarget, string binlogPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(buildTarget) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(buildTarget);
        psi.ArgumentList.Add("--no-incremental");
        psi.ArgumentList.Add($"-bl:{binlogPath}");
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v:quiet");

        var execution = await _processRunner.RunAsync(psi, timeout, cancellationToken);
        var output = string.Concat(execution.StandardOutput, Environment.NewLine, execution.StandardError);
        if (!execution.Started)
            return (-1, false, execution.StartError);
        if (execution.TimedOut)
            return (-1, true, null);

        var match = System.Text.RegularExpressions.Regex.Match(output, @"error\s+([A-Z]{2,}\d{3,})");
        return (execution.ExitCode ?? -1, false, match.Success ? match.Groups[1].Value : null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
///     A set of rehydrated C# compilations held live for the lifetime of a pooled build-capture check worker
///     (R48), so many speculative checks reuse one rehydration instead of re-reading the compiler log per check.
///     Holds the underlying compiler-log reader alive because a rehydrated compilation resolves some inputs lazily
///     through it; <see cref="Dispose" /> releases both.
/// </summary>
public sealed class HeldComplog : IDisposable
{
    private readonly ICompilerCallReader _reader;

    internal HeldComplog(ICompilerCallReader reader, IReadOnlyList<Compilation> compilations)
    {
        _reader = reader;
        Compilations = compilations;
    }

    /// <summary>The rehydrated C# compilations, one per recorded C# compiler invocation in the log.</summary>
    public IReadOnlyList<Compilation> Compilations { get; }

    /// <summary>Releases the held compilations' underlying compiler-log reader.</summary>
    public void Dispose() => _reader.Dispose();
}

/// <summary>
///     A compiler log held for lazy pooled checks (R53). Source ownership is available before compilation
///     rehydration; each matching C# project is materialized once on demand and stays cached for later requests.
/// </summary>
public sealed class LazyHeldComplog : IDisposable
{
    private readonly ICompilerCallReader _reader;
    private readonly IReadOnlyList<CompilationData> _calls;
    private readonly IReadOnlyList<IReadOnlyList<string>> _sourcePaths;
    private readonly Compilation?[] _compilations;

    internal LazyHeldComplog(
        ICompilerCallReader reader,
        IReadOnlyList<CompilationData> calls,
        IReadOnlyList<IReadOnlyList<string>> sourcePaths)
    {
        _reader = reader;
        _calls = calls;
        _sourcePaths = sourcePaths;
        _compilations = new Compilation[calls.Count];
    }

    /// <summary>Gets how many captured C# project compilations have been rehydrated in this worker.</summary>
    public int RehydratedProjectCount => _compilations.Count(static compilation => compilation is not null);

    internal IReadOnlyList<Compilation> GetCompilationsFor(
        string relativeFilePath,
        Func<Compilation, string?, Compilation> normalizeSigning,
        CancellationToken cancellationToken)
    {
        var normalized = relativeFilePath.Replace('\\', '/');
        var matches = new List<Compilation>();
        for (var index = 0; index < _calls.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sourcePaths[index].Any(path => path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase)))
                continue;

            _compilations[index] ??= normalizeSigning(
                _calls[index].GetCompilationAfterGenerators(cancellationToken),
                _calls[index].CompilerCall.ProjectFilePath);
            matches.Add(_compilations[index]!);
        }

        return matches;
    }

    /// <summary>Releases the compiler-log reader and its lazily loaded inputs.</summary>
    public void Dispose() => _reader.Dispose();
}
