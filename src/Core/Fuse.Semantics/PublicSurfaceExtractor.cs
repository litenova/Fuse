using System.Text;
using Fuse.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Semantics;

/// <summary>
///     Extracts the public and protected API surface - types and their members - of a single C# file by syntax
///     analysis (T2). Unlike <see cref="SyntaxSymbolExtractor" />, which leaves member accessibility unset, this
///     computes effective accessibility for members (a class member defaults to private, an interface member to
///     public) and emits a member only when its whole containing-type chain is itself on the public surface, so a
///     public method of an internal class is not reported as public API. Member signatures and fully qualified
///     names are normalized (bodies stripped, parameter types included in the name so overloads stay distinct) for
///     a stable before/after comparison by <c>PublicApiDelta</c>.
/// </summary>
/// <remarks>
///     Syntax-only, so it works from any checkout state (the base side is read from the git base ref). A member
///     whose accessibility is reduced below protected (for example public to internal) simply leaves the emitted
///     set on that side, so the delta reports it as a removal - the conservative reading for a surface check.
/// </remarks>
public static class PublicSurfaceExtractor
{
    /// <summary>
    ///     Extracts the public and protected surface symbols of a C# file.
    /// </summary>
    /// <param name="normalizedPath">The forward-slash relative path used to key records to the file.</param>
    /// <param name="content">The file's source text.</param>
    /// <returns>The public and protected types and members; empty when the file has none or fails to parse.</returns>
    public static IReadOnlyList<SymbolRecord> Extract(string normalizedPath, string content)
    {
        if (string.IsNullOrEmpty(content))
            return [];

        SyntaxNode root;
        try
        {
            root = CSharpSyntaxTree.ParseText(content).GetRoot();
        }
        catch
        {
            return [];
        }

        var symbols = new List<SymbolRecord>();
        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            // A type is on the surface only when it and every enclosing type are public or protected; a nested
            // public type inside an internal type is not reachable from outside the assembly.
            if (!IsSurfaceType(type))
                continue;

            var typeFqn = TypeFqn(type);
            symbols.Add(new SymbolRecord(
                SymbolId: $"surface:{normalizedPath}:{typeFqn}",
                FilePath: normalizedPath,
                Kind: TypeKind(type),
                Name: type.Identifier.ValueText,
                FullyQualifiedName: typeFqn,
                Accessibility: DeclaredAccessibility(type.Modifiers, DefaultTypeAccessibility(type)),
                Signature: TypeSignature(type),
                IsPublicApi: true));

            foreach (var member in MemberDeclarations(type))
            {
                var accessibility = MemberAccessibility(member, type);
                if (!IsSurfaceAccessibility(accessibility))
                    continue;

                foreach (var (name, signature) in MemberIdentities(member))
                {
                    var fqn = $"{typeFqn}.{name}";
                    symbols.Add(new SymbolRecord(
                        SymbolId: $"surface:{normalizedPath}:{fqn}:{signature}",
                        FilePath: normalizedPath,
                        Kind: MemberKind(member),
                        Name: MemberSimpleName(member, name),
                        FullyQualifiedName: fqn,
                        ContainingType: type.Identifier.ValueText,
                        Accessibility: accessibility,
                        Signature: signature,
                        IsPublicApi: true));
                }
            }
        }

        return symbols;
    }

    // A type is on the surface when it and every enclosing type declaration are public or protected.
    private static bool IsSurfaceType(BaseTypeDeclarationSyntax type)
    {
        if (!IsSurfaceAccessibility(DeclaredAccessibility(type.Modifiers, DefaultTypeAccessibility(type))))
            return false;

        foreach (var outer in type.Ancestors().OfType<BaseTypeDeclarationSyntax>())
        {
            if (!IsSurfaceAccessibility(DeclaredAccessibility(outer.Modifiers, DefaultTypeAccessibility(outer))))
                return false;
        }

        return true;
    }

    private static bool IsSurfaceAccessibility(string accessibility) =>
        accessibility is "public" or "protected" or "protected internal";

    // The declared accessibility from explicit modifiers, or the supplied default when none are present.
    private static string DeclaredAccessibility(SyntaxTokenList modifiers, string defaultAccessibility)
    {
        var isProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);
        var isInternal = modifiers.Any(SyntaxKind.InternalKeyword);
        var isPrivate = modifiers.Any(SyntaxKind.PrivateKeyword);

        if (modifiers.Any(SyntaxKind.PublicKeyword))
            return "public";
        if (isProtected && isInternal)
            return "protected internal";
        if (isPrivate && isProtected)
            return "private protected";
        if (isProtected)
            return "protected";
        if (isInternal)
            return "internal";
        if (isPrivate)
            return "private";
        return defaultAccessibility;
    }

    // A top-level type defaults to internal; a nested type defaults to private.
    private static string DefaultTypeAccessibility(BaseTypeDeclarationSyntax type) =>
        type.Parent is BaseTypeDeclarationSyntax ? "private" : "internal";

    // Members of an interface default to public; every other container defaults its members to private. Enum
    // members are always public (the enum values themselves).
    private static string MemberAccessibility(MemberDeclarationSyntax member, BaseTypeDeclarationSyntax container)
    {
        if (member is EnumMemberDeclarationSyntax)
            return "public";

        var defaultAccessibility = container is InterfaceDeclarationSyntax ? "public" : "private";
        return DeclaredAccessibility(member.Modifiers, defaultAccessibility);
    }

    private static IEnumerable<MemberDeclarationSyntax> MemberDeclarations(BaseTypeDeclarationSyntax type)
    {
        if (type is TypeDeclarationSyntax typeDeclaration)
            return typeDeclaration.Members.Where(m => m is not BaseTypeDeclarationSyntax);
        if (type is EnumDeclarationSyntax enumDeclaration)
            return enumDeclaration.Members;
        return [];
    }

    // The (name, signature) identities a member contributes. A field or event-field with multiple declarators
    // contributes one identity per variable; every other member contributes exactly one.
    private static IEnumerable<(string Name, string Signature)> MemberIdentities(MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                var methodName = $"{method.Identifier.ValueText}{TypeParameters(method.TypeParameterList)}({ParameterTypes(method.ParameterList)})";
                yield return (methodName, Normalize($"{Modifiers(method.Modifiers)} {method.ReturnType} {methodName}"));
                break;
            case ConstructorDeclarationSyntax ctor:
                var ctorName = $"{ctor.Identifier.ValueText}({ParameterTypes(ctor.ParameterList)})";
                yield return (ctorName, Normalize($"{Modifiers(ctor.Modifiers)} {ctorName}"));
                break;
            case PropertyDeclarationSyntax property:
                yield return (property.Identifier.ValueText,
                    Normalize($"{Modifiers(property.Modifiers)} {property.Type} {property.Identifier.ValueText} {Accessors(property.AccessorList)}"));
                break;
            case IndexerDeclarationSyntax indexer:
                var indexerName = $"this[{ParameterTypes(indexer.ParameterList)}]";
                yield return (indexerName, Normalize($"{Modifiers(indexer.Modifiers)} {indexer.Type} {indexerName} {Accessors(indexer.AccessorList)}"));
                break;
            case EventDeclarationSyntax eventDeclaration:
                yield return (eventDeclaration.Identifier.ValueText,
                    Normalize($"{Modifiers(eventDeclaration.Modifiers)} event {eventDeclaration.Type} {eventDeclaration.Identifier.ValueText}"));
                break;
            case EventFieldDeclarationSyntax eventField:
                foreach (var variable in eventField.Declaration.Variables)
                    yield return (variable.Identifier.ValueText,
                        Normalize($"{Modifiers(eventField.Modifiers)} event {eventField.Declaration.Type} {variable.Identifier.ValueText}"));
                break;
            case FieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                    yield return (variable.Identifier.ValueText,
                        Normalize($"{Modifiers(field.Modifiers)} {field.Declaration.Type} {variable.Identifier.ValueText}"));
                break;
            case OperatorDeclarationSyntax op:
                var opName = $"operator {op.OperatorToken.ValueText}({ParameterTypes(op.ParameterList)})";
                yield return (opName, Normalize($"{Modifiers(op.Modifiers)} {op.ReturnType} {opName}"));
                break;
            case ConversionOperatorDeclarationSyntax conversion:
                var conversionName = $"{conversion.ImplicitOrExplicitKeyword.ValueText} operator {conversion.Type}({ParameterTypes(conversion.ParameterList)})";
                yield return (conversionName, Normalize($"{Modifiers(conversion.Modifiers)} {conversionName}"));
                break;
            case EnumMemberDeclarationSyntax enumMember:
                yield return (enumMember.Identifier.ValueText, Normalize(enumMember.Identifier.ValueText));
                break;
        }
    }

    // The simple name for display: strip the parameter-type suffix used to keep overloads distinct.
    private static string MemberSimpleName(MemberDeclarationSyntax member, string identity) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.ValueText,
        ConstructorDeclarationSyntax c => c.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        OperatorDeclarationSyntax o => $"operator {o.OperatorToken.ValueText}",
        ConversionOperatorDeclarationSyntax => "operator",
        _ => identity,
    };

    private static string MemberKind(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax => "method",
        ConstructorDeclarationSyntax => "constructor",
        PropertyDeclarationSyntax => "property",
        IndexerDeclarationSyntax => "indexer",
        EventDeclarationSyntax or EventFieldDeclarationSyntax => "event",
        FieldDeclarationSyntax => "field",
        OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => "operator",
        EnumMemberDeclarationSyntax => "enum-member",
        _ => "member",
    };

    private static string ParameterTypes(ParameterListSyntax? parameters) =>
        parameters is null ? string.Empty : string.Join(",", parameters.Parameters.Select(p => p.Type?.ToString() ?? string.Empty));

    private static string ParameterTypes(BracketedParameterListSyntax? parameters) =>
        parameters is null ? string.Empty : string.Join(",", parameters.Parameters.Select(p => p.Type?.ToString() ?? string.Empty));

    private static string TypeParameters(TypeParameterListSyntax? typeParameters) =>
        typeParameters is null ? string.Empty : typeParameters.ToString();

    private static string Modifiers(SyntaxTokenList modifiers) => modifiers.ToString();

    // The accessor shape (get/set/init presence), which is part of the property contract; accessor bodies are
    // dropped so an implementation change does not read as a surface change.
    private static string Accessors(AccessorListSyntax? accessors) =>
        accessors is null
            ? string.Empty
            : "{ " + string.Join(" ", accessors.Accessors.Select(a => $"{Modifiers(a.Modifiers)} {a.Keyword.ValueText};".Trim())) + " }";

    private static string TypeFqn(BaseTypeDeclarationSyntax type)
    {
        var parts = new List<string>();
        var ns = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns is not null)
            parts.Add(ns.Name.ToString());

        foreach (var outer in type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse())
            parts.Add(NameWithArity(outer.Identifier.ValueText, outer.TypeParameterList));

        parts.Add(NameWithArity(type.Identifier.ValueText, (type as TypeDeclarationSyntax)?.TypeParameterList));
        return string.Join(".", parts);
    }

    // The type's name with a CLR-style arity marker (Foo`1 for Foo<T>) so a generic type and a same-named
    // non-generic type are distinct identities and not collapsed into one - and so renaming a type parameter (T to
    // TResult), which is not an API break, does not read as one. Arity, not the parameter names, is the identity.
    private static string NameWithArity(string name, TypeParameterListSyntax? typeParameters) =>
        typeParameters is null || typeParameters.Parameters.Count == 0
            ? name
            : $"{name}`{typeParameters.Parameters.Count}";

    private static string TypeKind(BaseTypeDeclarationSyntax type) => type switch
    {
        InterfaceDeclarationSyntax => "interface",
        StructDeclarationSyntax => "struct",
        RecordDeclarationSyntax => "record",
        EnumDeclarationSyntax => "enum",
        ClassDeclarationSyntax => "class",
        _ => "type",
    };

    private static string TypeSignature(BaseTypeDeclarationSyntax type)
    {
        var keyword = TypeKind(type);
        var typeParameters = (type as TypeDeclarationSyntax)?.TypeParameterList?.ToString() ?? string.Empty;
        return Normalize($"{Modifiers(type.Modifiers)} {keyword} {type.Identifier.ValueText}{typeParameters}");
    }

    // Collapses runs of whitespace to a single space and trims, so formatting-only differences never read as a
    // signature change.
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                builder.Append(ch);
                lastWasSpace = false;
            }
        }

        return builder.ToString().Trim();
    }
}
