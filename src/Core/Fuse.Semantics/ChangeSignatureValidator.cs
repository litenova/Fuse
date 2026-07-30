using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Fuse.Semantics;

/// <summary>
///     Resolves a change-signature target and rejects unsafe parameter or positional-call conditions before any
///     source edit is planned.
/// </summary>
internal sealed class ChangeSignatureValidator
{
    private const int SymbolEqualityComparerCapacity = 4;

    internal async Task<(IMethodSymbol? Method, string? Reason)> ResolveMethodAsync(
        Solution solution,
        string methodName,
        string? containingTypeName,
        CancellationToken cancellationToken)
    {
        var matches = new List<IMethodSymbol>(SymbolEqualityComparerCapacity);
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                return (null, $"project '{project.Name}' produced no compilation; the change would be incomplete");

            foreach (var type in EnumerateSourceTypes(compilation.Assembly.GlobalNamespace))
            {
                if (containingTypeName is not null && type.Name != containingTypeName)
                    continue;
                foreach (var member in type.GetMembers(methodName).OfType<IMethodSymbol>())
                {
                    if (member.MethodKind != MethodKind.Ordinary || member.IsImplicitlyDeclared)
                        continue;
                    if (seen.Add(member))
                        matches.Add(member);
                }
            }
        }

        return matches.Count switch
        {
            0 => (null, $"method '{methodName}' was not found in the loaded solution's source"),
            1 => (matches[0], null),
            _ => (null, $"'{methodName}' is ambiguous ({matches.Count} methods match); pass a containing type or disambiguate (overload sets are not yet supported)"),
        };
    }

    internal async Task<IReadOnlyCollection<IMethodSymbol>> CollectMethodFamilyAsync(
        Solution solution,
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        var family = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { method };
        var root = method;
        while (root.OverriddenMethod is { } overridden)
            root = overridden;
        family.Add(root);
        foreach (var overridden in await SymbolFinder.FindOverridesAsync(root, solution, cancellationToken: cancellationToken))
            if (overridden is IMethodSymbol overrideMethod)
                family.Add(overrideMethod);

        foreach (var @interface in method.ContainingType.AllInterfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var interfaceMember in @interface.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                var implementation = method.ContainingType.FindImplementationForInterfaceMember(interfaceMember);
                if (implementation is null || !SymbolEqualityComparer.Default.Equals(implementation, method))
                    continue;

                family.Add(interfaceMember);
                foreach (var found in await SymbolFinder.FindImplementationsAsync(interfaceMember, solution, cancellationToken: cancellationToken))
                    if (found is IMethodSymbol implementationMethod)
                        family.Add(implementationMethod);
            }
        }

        return family;
    }

    internal async Task<string?> FindParameterUsageAsync(
        Solution solution,
        IReadOnlyCollection<IMethodSymbol> family,
        int index,
        CancellationToken cancellationToken)
    {
        foreach (var member in family)
        {
            if (index >= member.Parameters.Length)
                continue;
            foreach (var referenced in await SymbolFinder.FindReferencesAsync(member.Parameters[index], solution, cancellationToken))
                if (referenced.Locations.Any())
                    return member.ToDisplayString();
        }

        return null;
    }

    internal async Task<string?> FindFirstPositionalCallSiteAsync(
        Solution solution,
        IReadOnlyCollection<IMethodSymbol> family,
        CancellationToken cancellationToken)
    {
        foreach (var member in family)
        {
            foreach (var referenced in await SymbolFinder.FindReferencesAsync(member, solution, cancellationToken))
            {
                foreach (var location in referenced.Locations)
                {
                    var document = location.Document;
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    var token = root?.FindToken(location.Location.SourceSpan.Start);
                    var invocation = token?.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation is not null && invocation.ArgumentList.Arguments.Any(argument => argument.NameColon is null))
                        return HumanNode(invocation);
                }
            }
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateSourceTypes(INamespaceSymbol @namespace)
    {
        foreach (var type in @namespace.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in EnumerateNestedTypes(type))
                yield return nested;
        }

        foreach (var child in @namespace.GetNamespaceMembers())
            foreach (var nested in EnumerateSourceTypes(child))
                yield return nested;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNestedTypes(nested))
                yield return deeper;
        }
    }

    private static string HumanNode(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return $"{Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}";
    }
}
