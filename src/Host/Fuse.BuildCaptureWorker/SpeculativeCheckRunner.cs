using Fuse.Indexing;
using Microsoft.CodeAnalysis;

namespace Fuse.BuildCaptureWorker;

/// <summary>
///     Applies one proposed source overlay to a held compiler compilation and reports its compiler diagnostics
///     without writing the working tree or launching another build.
/// </summary>
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
            var diagnostics = forked.GetSemanticModel(newTree)
                .GetDiagnostics(cancellationToken: cancellationToken)
                .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                .Select(ToCheckDiagnostic)
                .ToList();
            return CheckResult.Ok(diagnostics);
        }

        return CheckResult.Abstain($"the changed file '{relativeFilePath}' was not found in any captured C# project");
    }

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
