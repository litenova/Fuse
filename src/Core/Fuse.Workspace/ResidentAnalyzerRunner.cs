using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Workspace;

/// <summary>
///     Runs a project's diagnostic analyzers against a compilation and returns the analyzer diagnostics located in
///     a single document (S4). This is the analyzer half of the check: the compiler diagnostics come from the
///     semantic model, and these come from the repo's configured analyzers run with the editorconfig-derived
///     options, so a check reports what CI's build step would enforce, not just the raw compiler errors.
/// </summary>
/// <remarks>
///     Factored out of <see cref="ResidentWorkspace" /> so the analyzer-run-and-filter behavior is unit-tested
///     with an in-memory compilation and an inline analyzer, without a build capture. Only error and warning
///     severities are returned (hidden and info are editor noise); the analyzers run with the supplied
///     <see cref="AnalyzerOptions" /> so an editorconfig-silenced rule produces nothing.
/// </remarks>
public static class ResidentAnalyzerRunner
{
    /// <summary>
    ///     Runs the analyzers and returns their error and warning diagnostics located in the given syntax tree.
    /// </summary>
    /// <param name="compilation">The compilation to analyze (the forked overlay compilation for a check).</param>
    /// <param name="analyzers">The analyzers to run; an empty set returns no diagnostics.</param>
    /// <param name="options">The analyzer options (additional files plus editorconfig severities), or null for defaults.</param>
    /// <param name="tree">The document to scope the returned diagnostics to.</param>
    /// <param name="cancellationToken">A token to cancel the analyzer run.</param>
    /// <returns>The analyzer error and warning diagnostics whose location is in <paramref name="tree" />.</returns>
    public static async Task<IReadOnlyList<Diagnostic>> DiagnosticsForTreeAsync(
        Compilation compilation,
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        AnalyzerOptions? options,
        SyntaxTree tree,
        CancellationToken cancellationToken)
    {
        if (analyzers.IsDefaultOrEmpty)
            return [];

        var withAnalyzers = compilation.WithAnalyzers(analyzers, options);
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);
        return diagnostics
            .Where(d => d.Location.SourceTree == tree)
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .ToList();
    }
}
