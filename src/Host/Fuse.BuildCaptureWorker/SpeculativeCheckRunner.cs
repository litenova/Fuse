using Fuse.Indexing;
using Microsoft.CodeAnalysis;

namespace Fuse.BuildCaptureWorker;

/// <summary>
///     Applies one proposed source overlay to a held compiler compilation and reports the compiler diagnostics the
///     edit INTRODUCED (the forked diagnostics minus the base compilation's pre-existing diagnostics for the same
///     document, keyed by id and message) without writing the working tree or launching another build.
/// </summary>
/// <remarks>
///     The check is delta-based on purpose: a rehydrated compilation can carry pre-existing diagnostics the edit
///     did not cause (for example phantom errors from a source generator that failed to load because it was built
///     against a newer Roslyn than the worker bundles), and reporting those would make a no-op edit look broken.
///     For a clean base the delta is identical to the full diagnostic set, so a genuinely breaking edit is still
///     flagged.
/// </remarks>
internal sealed class SpeculativeCheckRunner
{
    internal CheckResult Check(
        IReadOnlyList<Compilation> compilations,
        string relativeFilePath,
        string newContent,
        CancellationToken cancellationToken)
    {
        var normalized = relativeFilePath.Replace('\\', '/');
        foreach (var compilation in compilations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tree = compilation.SyntaxTrees.FirstOrDefault(tree =>
                tree.FilePath.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
            if (tree is null)
                continue;

            var newTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                newContent,
                (Microsoft.CodeAnalysis.CSharp.CSharpParseOptions?)tree.Options,
                tree.FilePath,
                cancellationToken: cancellationToken);
            var forked = compilation.ReplaceSyntaxTree(tree, newTree);
            var forkedDiagnostics = forked.GetSemanticModel(newTree)
                .GetDiagnostics(cancellationToken: cancellationToken)
                .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                .Select(ToCheckDiagnostic)
                .ToList();

            // The rehydrated compilation can carry pre-existing diagnostics on the changed document that the edit
            // did not cause. The build-capture path rehydrates the recorded compiler log with the worker's bundled
            // Roslyn, and a source generator built against a newer Roslyn (for example the .NET 10 SDK's Razor
            // generator, which needs Microsoft.CodeAnalysis 5.9.0 against the worker's 5.6.0) fails to load, so the
            // partial class it would have generated is missing and the compiler reports phantom errors (for example
            // CS0115/CS0120 on a Blazor code-behind) that the real build never produced. Reporting those would
            // make a no-op edit look broken. The check is therefore delta-based: it reports only the diagnostics
            // the edit INTRODUCED - the forked diagnostics whose (id, message) key is not present in the base
            // compilation's diagnostics for the same document. For a clean base this is identical to reporting the
            // full set; for a base with pre-existing errors a no-op edit yields zero introduced diagnostics and is
            // reported clean, while a genuinely breaking edit still contributes its new error and is flagged.
            // The key matches the codebase's DiagnosticDelta convention (file + id + message, line excluded, since
            // an edit shifts line numbers for every diagnostic below it).
            var baselineKeys = BaseDiagnosticKeys(compilation, tree, cancellationToken);
            var introduced = forkedDiagnostics
                .Where(diagnostic => !baselineKeys.Contains(DeltaKey(diagnostic.Id, diagnostic.Message)))
                .ToList();
            return CheckResult.Ok(introduced);
        }

        return CheckResult.Abstain($"the changed file '{relativeFilePath}' was not found in any captured C# project");
    }

    // The (id, message) keys of the base compilation's error/warning diagnostics for one document, used to tell
    // pre-existing diagnostics apart from ones the edit introduced.
    private static HashSet<string> BaseDiagnosticKeys(
        Compilation compilation, SyntaxTree tree, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in compilation.GetSemanticModel(tree)
                     .GetDiagnostics(cancellationToken: cancellationToken)
                     .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
        {
            keys.Add(DeltaKey(diagnostic.Id, diagnostic.GetMessage()));
        }

        return keys;
    }

    // The delta key for a diagnostic: its id and normalized message. Line is excluded on purpose - an edit shifts
    // the line numbers of every diagnostic below it, so line is an unreliable identity across a fork.
    private static string DeltaKey(string id, string message)
        => id + "\u0000" + message;

    private static CheckDiagnostic ToCheckDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan() : default;
        return new CheckDiagnostic(
            Id: diagnostic.Id,
            Severity: diagnostic.Severity.ToString(),
            Message: diagnostic.GetMessage(),
            FilePath: diagnostic.Location.IsInSource ? diagnostic.Location.SourceTree?.FilePath : null,
            Line: diagnostic.Location.IsInSource ? span.StartLinePosition.Line + 1 : 0);
    }
}
