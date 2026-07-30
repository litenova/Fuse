using Microsoft.CodeAnalysis;

namespace Fuse.Semantics;

/// <summary>
///     Captures a solution's existing compiler errors, rejects newly introduced errors, and renders verified
///     staged source diffs for a change-signature operation.
/// </summary>
internal sealed class ChangeSignatureVerifier
{
    internal async Task<HashSet<string>> CollectErrorSignaturesAsync(
        Solution solution,
        CancellationToken cancellationToken)
    {
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                continue;
            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
                if (diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    signatures.Add(Signature(diagnostic));
        }

        return signatures;
    }

    internal async Task<IReadOnlyList<string>> CollectIntroducedErrorsAsync(
        Solution solution,
        HashSet<string> baseline,
        CancellationToken cancellationToken)
    {
        var introduced = new List<string>();
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                continue;
            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    continue;
                if (!baseline.Contains(Signature(diagnostic)))
                    introduced.Add(HumanSite(diagnostic));
            }
        }

        return introduced;
    }

    internal async Task<IReadOnlyList<ChangeSignatureFileDiff>> BuildDiffsAsync(
        Solution before,
        Solution after,
        CancellationToken cancellationToken)
    {
        var diffs = new List<ChangeSignatureFileDiff>();
        foreach (var changedId in after.GetChanges(before).GetProjectChanges().SelectMany(change => change.GetChangedDocuments()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var beforeText = await before.GetDocument(changedId)!.GetTextAsync(cancellationToken);
            var afterText = await after.GetDocument(changedId)!.GetTextAsync(cancellationToken);
            if (beforeText.ContentEquals(afterText))
                continue;
            var document = after.GetDocument(changedId)!;
            var path = document.FilePath ?? document.Name;
            diffs.Add(new ChangeSignatureFileDiff(path, BuildLineDiff(beforeText.ToString(), afterText.ToString())));
        }

        return diffs;
    }

    private static string Signature(Diagnostic diagnostic)
    {
        var file = diagnostic.Location.SourceTree?.FilePath ?? "<none>";
        return $"{diagnostic.Id}|{file}|{diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string HumanSite(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        var file = Path.GetFileName(span.Path);
        return $"{diagnostic.Id} at {file}:{span.StartLinePosition.Line + 1} ({diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)})";
    }

    private static string BuildLineDiff(string before, string after)
    {
        var beforeLines = before.Replace("\r\n", "\n").Split('\n');
        var afterLines = after.Replace("\r\n", "\n").Split('\n');
        var builder = new System.Text.StringBuilder();
        var max = Math.Max(beforeLines.Length, afterLines.Length);
        for (var index = 0; index < max; index++)
        {
            var oldLine = index < beforeLines.Length ? beforeLines[index] : null;
            var newLine = index < afterLines.Length ? afterLines[index] : null;
            if (oldLine == newLine)
                continue;
            if (oldLine is not null)
                builder.AppendLine($"-{index + 1}: {oldLine}");
            if (newLine is not null)
                builder.AppendLine($"+{index + 1}: {newLine}");
        }

        return builder.ToString().TrimEnd();
    }
}
