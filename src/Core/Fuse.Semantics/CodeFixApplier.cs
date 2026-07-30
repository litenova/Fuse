using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Semantics;

/// <summary>
///     The apply-codefix core (T4b): drive a code fix for a diagnostic id across a document, staged and
///     verify-gated. Given the analyzers that raise the diagnostic and the fix providers that repair it, it
///     collects the occurrences, applies the first offered fix to each (re-collecting after each apply, since an
///     edit shifts the rest), and returns the changed solution only when the target diagnostic count reached zero
///     with no new error. This is the load-independent mechanism; the caller supplies analyzers and providers,
///     which the tool wiring will reflection-load from the compilation's analyzer references.
/// </summary>
/// <remarks>
///     Correctness-by-verification like the other refactorers: an imperfect or inapplicable fix is caught by the
///     recompile gate and abstained, never returned as a broken diff. Bounded by a fix-count cap so a fix that
///     does not converge (re-raises its own diagnostic) cannot loop forever.
/// </remarks>
public sealed class CodeFixApplier
{
    private const int MaxFixes = 100;

    private readonly WarmSolutionCache _cache;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CodeFixApplier" /> class.
    /// </summary>
    /// <param name="cache">
    ///     The warm-solution cache (R42) the fix loads through; defaults to the process-wide
    ///     the supplied host-owned <see cref="WarmSolutionCache" />.
    /// </param>
    public CodeFixApplier(WarmSolutionCache? cache = null) => _cache = cache ?? new WarmSolutionCache();

    /// <summary>
    ///     Loads the workspace, discovers the analyzers and code fix providers the project's analyzer references
    ///     ship, and applies the fix for a diagnostic id in the named file, staged and verify-gated.
    /// </summary>
    /// <param name="solutionOrProjectPath">The absolute path to the solution or project to load.</param>
    /// <param name="diagnosticId">The diagnostic id to drive to zero.</param>
    /// <param name="file">The file (path or suffix) to apply the fix in.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The outcome: the changed document text, or an abstention with a reason.</returns>
    public async Task<CodeFixResult> ApplyCodeFixAsync(
        string solutionOrProjectPath, string diagnosticId, string file, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(diagnosticId) || string.IsNullOrWhiteSpace(file))
            return CodeFixResult.Abstain("provide a diagnostic id and a file to fix");

        try { MsBuildLocatorRegistration.EnsureRegistered(); }
        catch (Exception ex) { return CodeFixResult.Abstain($"no MSBuild/SDK found ({ex.Message}); cannot apply the fix"); }

        CachedSolution loaded;
        try
        {
            loaded = await _cache.OpenAsync(solutionOrProjectPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CodeFixResult.Abstain($"could not load the workspace: {ex.Message}");
        }

        if (loaded.LoadFailures.Count > 0)
            return CodeFixResult.Abstain($"the workspace did not load cleanly; refused. First load failure: {loaded.LoadFailures[0]}");

        var solution = loaded.Solution;
        var normalized = file.Replace('\\', '/');
        var document = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath is { } fp && fp.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
        if (document is null)
            return CodeFixResult.Abstain($"file '{file}' was not found in the loaded solution");

        var analyzers = DiscoverAnalyzers(document.Project);
        var providers = DiscoverFixProviders(document.Project, diagnosticId);
        if (providers.Count == 0)
            return CodeFixResult.Abstain($"no code fix provider fixing '{diagnosticId}' was found in the project's analyzer references");

        return await ApplyAsync(solution, document.Id, diagnosticId, analyzers, providers, cancellationToken);
    }

    // The analyzers the project's references ship, best-effort: a reference that fails to yield analyzers is
    // skipped rather than failing the whole operation.
    private static ImmutableArray<DiagnosticAnalyzer> DiscoverAnalyzers(Project project)
    {
        var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        foreach (var reference in project.AnalyzerReferences)
        {
            try { builder.AddRange(reference.GetAnalyzers(LanguageNames.CSharp)); }
            catch (Exception) { /* a reference that cannot load its analyzers is skipped. */ }
        }

        return builder.ToImmutable();
    }

    // The code fix providers the project's analyzer-reference assemblies export, discovered by reflection: load
    // each reference assembly, find non-abstract CodeFixProvider subtypes, instantiate them, and keep those that
    // fix the id. Best-effort and defensive - a reference that cannot load (a Roslyn-version mismatch) is skipped.
    private static IReadOnlyList<CodeFixProvider> DiscoverFixProviders(Project project, string diagnosticId)
    {
        var providers = new List<CodeFixProvider>();
        foreach (var path in project.AnalyzerReferences.OfType<AnalyzerFileReference>().Select(r => r.FullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var assembly = Assembly.LoadFrom(path);
                foreach (var type in assembly.GetTypes())
                {
                    if (!type.IsAbstract && typeof(CodeFixProvider).IsAssignableFrom(type) && Activator.CreateInstance(type) is CodeFixProvider provider
                        && provider.FixableDiagnosticIds.Contains(diagnosticId))
                        providers.Add(provider);
                }
            }
            catch (Exception) { /* a reference assembly that cannot load or reflect is skipped. */ }
        }

        return providers;
    }

    /// <summary>
    ///     Applies the code fix for a diagnostic id across a document and returns the outcome.
    /// </summary>
    /// <param name="solution">The loaded solution.</param>
    /// <param name="documentId">The document to fix.</param>
    /// <param name="diagnosticId">The diagnostic id to drive to zero.</param>
    /// <param name="analyzers">The analyzers that raise the diagnostic (empty for a compiler diagnostic).</param>
    /// <param name="providers">The code fix providers whose fixes to apply (those fixing the id).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The outcome: the changed document text, or an abstention with a reason.</returns>
    public async Task<CodeFixResult> ApplyAsync(
        Solution solution,
        DocumentId documentId,
        string diagnosticId,
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        IReadOnlyList<CodeFixProvider> providers,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(diagnosticId))
            return CodeFixResult.Abstain("provide a diagnostic id to fix");
        var applicable = providers.Where(p => p.FixableDiagnosticIds.Contains(diagnosticId)).ToList();
        if (applicable.Count == 0)
            return CodeFixResult.Abstain($"no code fix provider fixes '{diagnosticId}'");

        var document = solution.GetDocument(documentId);
        if (document is null)
            return CodeFixResult.Abstain("the document is not in the loaded solution");

        var baselineErrors = await ErrorSignaturesAsync(solution, cancellationToken);
        var initialCount = await CountAsync(solution, documentId, diagnosticId, analyzers, cancellationToken);
        if (initialCount == 0)
            return CodeFixResult.Abstain($"no '{diagnosticId}' diagnostic in the document to fix");

        var current = solution;
        var applied = 0;
        for (var i = 0; i < MaxFixes; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = await DiagnosticsAsync(current, documentId, diagnosticId, analyzers, cancellationToken);
            if (diagnostics.Count == 0)
                break;

            var action = await FirstActionAsync(current.GetDocument(documentId)!, diagnostics[0], applicable, cancellationToken);
            if (action is null)
                return CodeFixResult.Abstain($"no fix was offered for a '{diagnosticId}' occurrence; cannot drive it to zero");

            var operations = await action.GetOperationsAsync(cancellationToken);
            var apply = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
            if (apply is null)
                return CodeFixResult.Abstain("the offered fix has no applicable solution change");
            current = apply.ChangedSolution;
            applied++;
        }

        var remaining = await CountAsync(current, documentId, diagnosticId, analyzers, cancellationToken);
        if (remaining > 0)
            return CodeFixResult.Abstain($"the fix did not drive '{diagnosticId}' to zero ({remaining} left after {applied} applied); refused");

        var introduced = await IntroducedErrorsAsync(current, baselineErrors, cancellationToken);
        if (introduced.Count > 0)
            return CodeFixResult.Abstain($"applying the fix introduced {introduced.Count} new compile error(s), so it is refused: {string.Join("; ", introduced.Take(5))}");

        var before = await solution.GetDocument(documentId)!.GetTextAsync(cancellationToken);
        var after = await current.GetDocument(documentId)!.GetTextAsync(cancellationToken);
        if (before.ContentEquals(after))
            return CodeFixResult.Abstain("the fix produced no change");

        var path = current.GetDocument(documentId)!.FilePath ?? current.GetDocument(documentId)!.Name;
        return CodeFixResult.Ok(diagnosticId, applied, path, after.ToString());
    }

    private static async Task<CodeAction?> FirstActionAsync(
        Document document, Diagnostic diagnostic, IReadOnlyList<CodeFixProvider> providers, CancellationToken cancellationToken)
    {
        foreach (var provider in providers)
        {
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), cancellationToken);
            await provider.RegisterCodeFixesAsync(context);
            if (actions.Count > 0)
                return actions[0];
        }

        return null;
    }

    private static async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(
        Solution solution, DocumentId documentId, string diagnosticId, ImmutableArray<DiagnosticAnalyzer> analyzers, CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(documentId)!;
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (compilation is null || tree is null)
            return [];

        IEnumerable<Diagnostic> source = compilation.GetDiagnostics(cancellationToken);
        if (!analyzers.IsDefaultOrEmpty)
        {
            var withAnalyzers = compilation.WithAnalyzers(analyzers, document.Project.AnalyzerOptions);
            source = source.Concat(await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken));
        }

        return source
            .Where(d => d.Id == diagnosticId && d.Location.SourceTree == tree)
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ToList();
    }

    private static async Task<int> CountAsync(
        Solution solution, DocumentId documentId, string diagnosticId, ImmutableArray<DiagnosticAnalyzer> analyzers, CancellationToken cancellationToken) =>
        (await DiagnosticsAsync(solution, documentId, diagnosticId, analyzers, cancellationToken)).Count;

    private static async Task<HashSet<string>> ErrorSignaturesAsync(Solution solution, CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                continue;
            foreach (var d in compilation.GetDiagnostics(cancellationToken))
                if (d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    set.Add($"{d.Id}|{d.Location.SourceTree?.FilePath}|{d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        return set;
    }

    private static async Task<IReadOnlyList<string>> IntroducedErrorsAsync(
        Solution solution, HashSet<string> baseline, CancellationToken cancellationToken)
    {
        var introduced = new List<string>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                continue;
            foreach (var d in compilation.GetDiagnostics(cancellationToken))
            {
                if (d.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    continue;
                var sig = $"{d.Id}|{d.Location.SourceTree?.FilePath}|{d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}";
                if (!baseline.Contains(sig))
                    introduced.Add(d.Id);
            }
        }

        return introduced;
    }
}

/// <summary>The outcome of applying a code fix across a document (T4b).</summary>
/// <param name="Changed">Whether the fix ran, drove the diagnostic to zero, and verified clean.</param>
/// <param name="Reason">The abstention reason when <see cref="Changed" /> is false.</param>
/// <param name="DiagnosticId">The diagnostic id that was fixed, when changed.</param>
/// <param name="Applied">The number of fix applications made.</param>
/// <param name="FilePath">The changed document's path, when changed.</param>
/// <param name="NewText">The full new document content, when changed.</param>
public sealed record CodeFixResult(
    bool Changed, string? Reason, string? DiagnosticId, int Applied, string? FilePath, string? NewText)
{
    /// <summary>Creates a successful result.</summary>
    /// <param name="diagnosticId">The fixed diagnostic id.</param>
    /// <param name="applied">The number of applications.</param>
    /// <param name="filePath">The changed document path.</param>
    /// <param name="newText">The full new content.</param>
    /// <returns>A changed result.</returns>
    public static CodeFixResult Ok(string diagnosticId, int applied, string filePath, string newText) =>
        new(true, null, diagnosticId, applied, filePath, newText);

    /// <summary>Creates an abstention.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>An unchanged result.</returns>
    public static CodeFixResult Abstain(string reason) => new(false, reason, null, 0, null, null);
}
