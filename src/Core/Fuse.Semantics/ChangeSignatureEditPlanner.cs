using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Fuse.Semantics;

/// <summary>
///     Plans and applies in-memory Roslyn edits for supported change-signature operations. It never writes a
///     working-tree file; the caller verifies and stages its returned solution.
/// </summary>
internal sealed class ChangeSignatureEditPlanner
{
    internal async Task<(Solution? Solution, string? Reason)> RemoveParameterAsync(
        Solution solution,
        IReadOnlyCollection<IMethodSymbol> family,
        string parameterName,
        int index,
        CancellationToken cancellationToken)
    {
        var declarationEdits = new Dictionary<DocumentId, List<int>>();
        var callEdits = new Dictionary<DocumentId, List<int>>();
        foreach (var member in family)
        {
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                var document = solution.GetDocument(reference.SyntaxTree);
                if (document is null)
                    continue;
                var node = await reference.GetSyntaxAsync(cancellationToken);
                if (node is BaseMethodDeclarationSyntax { ParameterList: { } list } && index < list.Parameters.Count)
                    AddDeclarationSite(declarationEdits, document.Id, list.Parameters[index].SpanStart);
            }

            foreach (var referenced in await SymbolFinder.FindReferencesAsync(member, solution, cancellationToken))
            {
                foreach (var location in referenced.Locations)
                {
                    var document = location.Document;
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    var token = root?.FindToken(location.Location.SourceSpan.Start);
                    var invocation = token?.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation is null)
                        continue;
                    var argument = SelectArgument(invocation.ArgumentList, parameterName, index);
                    if (argument is null)
                        continue;
                    if (!IsSideEffectFree(argument.Expression))
                    {
                        return (null,
                            $"call site at {HumanNode(invocation)} passes a non-trivial argument for '{parameterName}' that would be dropped; remove-parameter abstains (possible side effect)");
                    }

                    AddDeclarationSite(callEdits, document.Id, argument.SpanStart);
                }
            }
        }

        var changed = solution;
        foreach (var documentId in declarationEdits.Keys.Concat(callEdits.Keys).Distinct())
        {
            var document = changed.GetDocument(documentId)!;
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken);
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null)
                continue;

            if (declarationEdits.TryGetValue(documentId, out var declarationSpans))
                foreach (var span in declarationSpans)
                {
                    var parameter = root.FindToken(span).Parent?.AncestorsAndSelf().OfType<ParameterSyntax>().FirstOrDefault();
                    if (parameter is not null)
                        editor.RemoveNode(parameter);
                }

            if (callEdits.TryGetValue(documentId, out var callSpans))
                foreach (var span in callSpans)
                {
                    var argument = root.FindToken(span).Parent?.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault();
                    if (argument is not null)
                        editor.RemoveNode(argument);
                }

            changed = changed.WithDocumentSyntaxRoot(
                documentId,
                await editor.GetChangedDocument().GetSyntaxRootAsync(cancellationToken) ?? root);
        }

        return (changed, null);
    }

    internal async Task<(Solution? Solution, string? Reason)> ReorderParametersAsync(
        Solution solution,
        IReadOnlyCollection<IMethodSymbol> family,
        int[] permutation,
        CancellationToken cancellationToken)
    {
        var declarationEdits = new Dictionary<DocumentId, List<int>>();
        foreach (var member in family)
        {
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                var document = solution.GetDocument(reference.SyntaxTree);
                if (document is null)
                    continue;
                var node = await reference.GetSyntaxAsync(cancellationToken);
                if (node is BaseMethodDeclarationSyntax { ParameterList: { } list } && list.Parameters.Count == permutation.Length)
                    AddDeclarationSite(declarationEdits, document.Id, list.SpanStart);
            }
        }

        var changed = solution;
        foreach (var documentId in declarationEdits.Keys)
        {
            var document = changed.GetDocument(documentId)!;
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken);
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null)
                continue;
            foreach (var span in declarationEdits[documentId])
            {
                var list = root.FindToken(span).Parent?.AncestorsAndSelf().OfType<ParameterListSyntax>().FirstOrDefault();
                if (list is null || list.Parameters.Count != permutation.Length)
                    continue;
                var reordered = permutation
                    .Select((oldIndex, newIndex) => list.Parameters[oldIndex]
                        .WithLeadingTrivia()
                        .WithLeadingTrivia(newIndex == 0 ? default : SyntaxFactory.Space))
                    .ToArray();
                editor.ReplaceNode(list, list.WithParameters(SyntaxFactory.SeparatedList(reordered)));
            }

            changed = changed.WithDocumentSyntaxRoot(
                documentId,
                await editor.GetChangedDocument().GetSyntaxRootAsync(cancellationToken) ?? root);
        }

        return (changed, null);
    }

    internal async Task<(Solution? Solution, string? Reason, IReadOnlyList<string> FollowUps)> AddParameterAsync(
        Solution solution,
        IReadOnlyCollection<IMethodSymbol> family,
        string parameterType,
        string parameterName,
        Func<SemanticModel?, InvocationExpressionSyntax, (string Arg, bool FollowUp)> argumentResolver,
        CancellationToken cancellationToken)
    {
        var declarationEdits = new Dictionary<DocumentId, List<int>>();
        var callEdits = new Dictionary<DocumentId, List<(int Span, string Argument)>>();
        var followUps = new List<string>();
        foreach (var member in family)
        {
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = solution.GetDocument(reference.SyntaxTree);
                if (document is null)
                    continue;
                var node = await reference.GetSyntaxAsync(cancellationToken);
                if (node is BaseMethodDeclarationSyntax { ParameterList: { } list })
                    AddDeclarationSite(declarationEdits, document.Id, list.SpanStart);
            }

            foreach (var referenced in await SymbolFinder.FindReferencesAsync(member, solution, cancellationToken))
            {
                foreach (var location in referenced.Locations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var document = location.Document;
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    var token = root?.FindToken(location.Location.SourceSpan.Start);
                    var invocation = token?.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation is null)
                        continue;
                    var model = await document.GetSemanticModelAsync(cancellationToken);
                    var (argument, followUp) = argumentResolver(model, invocation);
                    if (AddCallSite(callEdits, document.Id, invocation.ArgumentList.SpanStart, argument) && followUp)
                        followUps.Add(HumanNode(invocation));
                }
            }
        }

        var changed = solution;
        foreach (var documentId in declarationEdits.Keys.Concat(callEdits.Keys).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = changed.GetDocument(documentId)!;
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken);
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null)
                continue;

            if (declarationEdits.TryGetValue(documentId, out var declarationSpans))
                foreach (var span in declarationSpans)
                {
                    var list = root.FindToken(span).Parent?.AncestorsAndSelf().OfType<ParameterListSyntax>().FirstOrDefault();
                    if (list is null)
                        continue;
                    var parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameterName))
                        .WithType(SyntaxFactory.ParseTypeName(parameterType).WithTrailingTrivia(SyntaxFactory.Space))
                        .WithLeadingTrivia(SyntaxFactory.Space);
                    editor.ReplaceNode(list, list.AddParameters(parameter));
                }

            if (callEdits.TryGetValue(documentId, out var callSpans))
                foreach (var (span, argumentText) in callSpans)
                {
                    var list = root.FindToken(span).Parent?.AncestorsAndSelf().OfType<ArgumentListSyntax>().FirstOrDefault();
                    if (list is null)
                        continue;
                    var argument = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(argumentText))
                        .WithLeadingTrivia(SyntaxFactory.Space);
                    editor.ReplaceNode(list, list.AddArguments(argument));
                }

            changed = changed.WithDocumentSyntaxRoot(
                documentId,
                await editor.GetChangedDocument().GetSyntaxRootAsync(cancellationToken) ?? root);
        }

        return (changed, null, followUps);
    }

    internal static (string Arg, bool FollowUp) ResolveTokenArgument(
        SemanticModel? model,
        InvocationExpressionSyntax invocation)
    {
        if (model is not null)
        {
            var inScope = model.LookupSymbols(invocation.SpanStart)
                .Where(symbol => symbol is IParameterSymbol or ILocalSymbol)
                .Select(symbol => (Symbol: symbol, Type: (symbol as IParameterSymbol)?.Type ?? (symbol as ILocalSymbol)?.Type))
                .FirstOrDefault(candidate => candidate.Type is { Name: "CancellationToken", ContainingNamespace.Name: "Threading" });
            if (inScope.Symbol is not null)
                return (inScope.Symbol.Name, false);
        }

        return ("default", true);
    }

    private static ArgumentSyntax? SelectArgument(ArgumentListSyntax list, string parameterName, int index)
    {
        var named = list.Arguments.FirstOrDefault(argument => argument.NameColon?.Name.Identifier.Text == parameterName);
        if (named is not null)
            return named;
        var positional = list.Arguments.Where(argument => argument.NameColon is null).ToList();
        return index < positional.Count ? positional[index] : null;
    }

    private static bool IsSideEffectFree(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax => true,
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax member => IsSideEffectFree(member.Expression),
        DefaultExpressionSyntax => true,
        ThisExpressionSyntax or BaseExpressionSyntax => true,
        ParenthesizedExpressionSyntax parenthesized => IsSideEffectFree(parenthesized.Expression),
        _ when expression.IsKind(SyntaxKind.DefaultLiteralExpression) => true,
        _ => false,
    };

    private static string HumanNode(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return $"{Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}";
    }

    private static void AddDeclarationSite(Dictionary<DocumentId, List<int>> sites, DocumentId id, int span)
    {
        if (!sites.TryGetValue(id, out var entries))
            sites[id] = entries = [];
        if (!entries.Contains(span))
            entries.Add(span);
    }

    private static bool AddCallSite(
        Dictionary<DocumentId, List<(int Span, string Argument)>> sites,
        DocumentId id,
        int span,
        string argument)
    {
        if (!sites.TryGetValue(id, out var entries))
            sites[id] = entries = [];
        if (entries.Any(entry => entry.Span == span))
            return false;
        entries.Add((span, argument));
        return true;
    }
}
