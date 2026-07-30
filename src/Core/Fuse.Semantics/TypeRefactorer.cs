using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace Fuse.Semantics;

/// <summary>
///     Compiler-executed, verify-gated type-level refactors (T4): extract an interface from a class's public
///     surface, staged as a diff and returned only when the solution still compiles clean. Like the rename and
///     change-signature refactorers it is oracle-shaped - a solution that does not load, or a type that does not
///     resolve, yields an abstention rather than a partial change - and correctness-by-verification: the rewrite
///     is recompiled and any new diagnostic makes it abstain rather than return a broken diff.
/// </summary>
public sealed class TypeRefactorer
{
    private readonly WarmSolutionCache _cache;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TypeRefactorer" /> class.
    /// </summary>
    /// <param name="cache">
    ///     The warm-solution cache (R42) the refactor loads through; defaults to the process-wide
    ///     the supplied host-owned <see cref="WarmSolutionCache" />.
    /// </param>
    public TypeRefactorer(WarmSolutionCache? cache = null) => _cache = cache ?? new WarmSolutionCache();

    /// <summary>
    ///     Extracts an interface with the class's public instance methods and properties, adds it to the class's
    ///     base list, and returns the staged diff (or a named abstention).
    /// </summary>
    /// <param name="solutionOrProjectPath">The absolute path to the solution or project to load.</param>
    /// <param name="typeName">The simple name of the class to extract an interface from.</param>
    /// <param name="interfaceName">The new interface name; when null, the class name prefixed with <c>I</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The outcome: the per-file staged diffs, or an abstention with a concrete reason.</returns>
    public async Task<TypeRefactorResult> ExtractInterfaceAsync(
        string solutionOrProjectPath,
        string typeName,
        string? interfaceName,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadSolutionAsync(solutionOrProjectPath, cancellationToken);
        if (loaded.Solution is null)
            return TypeRefactorResult.Abstain(loaded.Reason!);
        return await ExtractInterfaceInSolutionAsync(loaded.Solution, typeName, interfaceName, cancellationToken);
    }

    /// <summary>
    ///     Moves a top-level type to its own new file (named after the type, in the same folder), removing it from
    ///     its current file, and returns the staged diff (or a named abstention). Verify-gated like the others.
    /// </summary>
    /// <param name="solutionOrProjectPath">The absolute path to the solution or project to load.</param>
    /// <param name="typeName">The simple name of the type to move.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The outcome: the staged new file and the trimmed original, or an abstention with a reason.</returns>
    public async Task<TypeRefactorResult> MoveTypeToOwnFileAsync(
        string solutionOrProjectPath,
        string typeName,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadSolutionAsync(solutionOrProjectPath, cancellationToken);
        if (loaded.Solution is null)
            return TypeRefactorResult.Abstain(loaded.Reason!);
        return await MoveTypeInSolutionAsync(loaded.Solution, typeName, cancellationToken);
    }

    internal async Task<TypeRefactorResult> MoveTypeInSolutionAsync(
        Solution solution, string typeName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return TypeRefactorResult.Abstain("provide the type name to move");

        var (type, reason) = await ResolveAnyTypeAsync(solution, typeName, cancellationToken);
        if (type is null)
            return TypeRefactorResult.Abstain(reason!);

        var declaration = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null || await declaration.GetSyntaxAsync(cancellationToken) is not BaseTypeDeclarationSyntax typeSyntax)
            return TypeRefactorResult.Abstain($"'{type.Name}' is not a single type declaration in source");
        var document = solution.GetDocument(declaration.SyntaxTree);
        if (document is null || await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root)
            return TypeRefactorResult.Abstain($"'{type.Name}' is not in a document of the loaded solution");

        var siblingTypes = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Count(t => t.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax);
        if (siblingTypes <= 1)
            return TypeRefactorResult.Abstain($"'{type.Name}' is already the only top-level type in its file; nothing to move");

        var baseline = await CollectErrorSignaturesAsync(solution, cancellationToken);

        // The new file: the original usings, the type's namespace (file-scoped), and the type declaration.
        var namespaceName = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;
        var movedType = typeSyntax.WithLeadingTrivia(SyntaxFactory.TriviaList()).WithTrailingTrivia(SyntaxFactory.TriviaList());
        var newUnit = SyntaxFactory.CompilationUnit().WithUsings(root.Usings);
        newUnit = namespaceName is null
            ? newUnit.AddMembers(movedType)
            : newUnit.AddMembers(SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName(namespaceName)).AddMembers(movedType));
        var newContent = newUnit.NormalizeWhitespace().ToFullString() + Environment.NewLine;

        // The trimmed original: remove the moved type node.
        var trimmedRoot = root.RemoveNode(typeSyntax, SyntaxRemoveOptions.KeepNoTrivia);
        var changed = solution.WithDocumentSyntaxRoot(document.Id, trimmedRoot!);

        // Add the new document in the same folder as the original.
        var directory = document.FilePath is { } fp ? Path.GetDirectoryName(fp) : null;
        var newPath = directory is null ? $"{type.Name}.cs" : Path.Combine(directory, $"{type.Name}.cs");
        var newDocId = DocumentId.CreateNewId(document.Project.Id);
        changed = changed.AddDocument(newDocId, $"{type.Name}.cs", Microsoft.CodeAnalysis.Text.SourceText.From(newContent), filePath: newPath);

        var introduced = await CollectIntroducedErrorsAsync(changed, baseline, cancellationToken);
        if (introduced.Count > 0)
            return TypeRefactorResult.Abstain($"the change introduced {introduced.Count} new compile error(s), so it is refused: {string.Join("; ", introduced.Take(5))}");

        var diffs = new List<TypeRefactorFileDiff>
        {
            new(newPath, newContent),
            new(document.FilePath ?? document.Name, (await changed.GetDocument(document.Id)!.GetTextAsync(cancellationToken)).ToString()),
        };
        return TypeRefactorResult.Ok(type.ToDisplayString(), $"moved {type.Name} to {type.Name}.cs", diffs);
    }

    internal async Task<TypeRefactorResult> ExtractInterfaceInSolutionAsync(
        Solution solution,
        string typeName,
        string? interfaceName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return TypeRefactorResult.Abstain("provide the class name to extract an interface from");

        var (type, reason) = await ResolveClassAsync(solution, typeName, cancellationToken);
        if (type is null)
            return TypeRefactorResult.Abstain(reason!);

        var name = string.IsNullOrWhiteSpace(interfaceName) ? $"I{type.Name}" : interfaceName;

        // The extractable surface: public instance methods and properties declared on the class (not inherited,
        // not static, not the constructor). An empty surface has nothing to extract.
        var members = type.GetMembers()
            .Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsStatic && !m.IsImplicitlyDeclared)
            .Where(m => m is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol)
            .ToList();
        if (members.Count == 0)
            return TypeRefactorResult.Abstain($"'{type.Name}' has no public instance methods or properties to extract");

        var declaration = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null || await declaration.GetSyntaxAsync(cancellationToken) is not ClassDeclarationSyntax classSyntax)
            return TypeRefactorResult.Abstain($"'{type.Name}' is not a single class declaration in source");
        var document = solution.GetDocument(declaration.SyntaxTree);
        if (document is null)
            return TypeRefactorResult.Abstain($"'{type.Name}' is not in a document of the loaded solution");

        var baseline = await CollectErrorSignaturesAsync(solution, cancellationToken);

        var interfaceDecl = BuildInterface(name, members);
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken);
        editor.InsertBefore(classSyntax, interfaceDecl);
        editor.ReplaceNode(classSyntax, AddBaseType(classSyntax, name));
        var changedRoot = await editor.GetChangedDocument().GetSyntaxRootAsync(cancellationToken);
        var changed = solution.WithDocumentSyntaxRoot(document.Id, changedRoot!);

        var introduced = await CollectIntroducedErrorsAsync(changed, baseline, cancellationToken);
        if (introduced.Count > 0)
            return TypeRefactorResult.Abstain($"the change introduced {introduced.Count} new compile error(s), so it is refused: {string.Join("; ", introduced.Take(5))}");

        var diffs = await BuildDiffsAsync(solution, changed, cancellationToken);
        if (diffs.Count == 0)
            return TypeRefactorResult.Abstain("the rewrite produced no change");

        return TypeRefactorResult.Ok(type.ToDisplayString(), $"extracted interface {name} with {members.Count} member(s)", diffs);
    }

    private static InterfaceDeclarationSyntax BuildInterface(string name, IReadOnlyList<ISymbol> members)
    {
        var declarations = new List<MemberDeclarationSyntax>();
        foreach (var member in members)
        {
            switch (member)
            {
                case IMethodSymbol method:
                    var parameters = method.Parameters.Select(p =>
                        SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
                            .WithType(SyntaxFactory.ParseTypeName(p.Type.ToDisplayString())));
                    declarations.Add(SyntaxFactory.MethodDeclaration(
                            SyntaxFactory.ParseTypeName(method.ReturnType.ToDisplayString()),
                            SyntaxFactory.Identifier(method.Name))
                        .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
                    break;
                case IPropertySymbol property:
                    var accessors = new List<AccessorDeclarationSyntax>();
                    if (property.GetMethod is { DeclaredAccessibility: Accessibility.Public })
                        accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
                    if (property.SetMethod is { DeclaredAccessibility: Accessibility.Public })
                        accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
                    if (accessors.Count == 0)
                        break;
                    declarations.Add(SyntaxFactory.PropertyDeclaration(
                            SyntaxFactory.ParseTypeName(property.Type.ToDisplayString()),
                            SyntaxFactory.Identifier(property.Name))
                        .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors))));
                    break;
            }
        }

        return SyntaxFactory.InterfaceDeclaration(name)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithMembers(SyntaxFactory.List(declarations))
            .NormalizeWhitespace()
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed);
    }

    private static ClassDeclarationSyntax AddBaseType(ClassDeclarationSyntax classSyntax, string interfaceName)
    {
        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName));
        if (classSyntax.BaseList is not null)
            return classSyntax.WithBaseList(classSyntax.BaseList.AddTypes(baseType));

        // No base list yet: emit " : IName" right after the identifier and move the identifier's trailing trivia
        // (the newline before the opening brace) to after the new base list, so it renders "class X : IName\n{".
        var identifier = classSyntax.Identifier;
        var colon = SyntaxFactory.Token(SyntaxKind.ColonToken)
            .WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space);
        var baseList = SyntaxFactory.BaseList(colon, SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType))
            .WithTrailingTrivia(identifier.TrailingTrivia);
        return classSyntax
            .WithIdentifier(identifier.WithTrailingTrivia())
            .WithBaseList(baseList);
    }

    private static Task<(INamedTypeSymbol? Type, string? Reason)> ResolveClassAsync(
        Solution solution, string typeName, CancellationToken cancellationToken) =>
        ResolveTypeAsync(solution, typeName, t => t.TypeKind == TypeKind.Class, "class", cancellationToken);

    private static Task<(INamedTypeSymbol? Type, string? Reason)> ResolveAnyTypeAsync(
        Solution solution, string typeName, CancellationToken cancellationToken) =>
        ResolveTypeAsync(solution, typeName,
            t => t.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum, "type", cancellationToken);

    private static async Task<(INamedTypeSymbol? Type, string? Reason)> ResolveTypeAsync(
        Solution solution, string typeName, Func<INamedTypeSymbol, bool> kind, string noun, CancellationToken cancellationToken)
    {
        var matches = new List<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                return (null, $"project '{project.Name}' produced no compilation; the change would be incomplete");
            foreach (var type in EnumerateSourceTypes(compilation.Assembly.GlobalNamespace))
                if (type.Name == typeName && kind(type) && seen.Add(type))
                    matches.Add(type);
        }

        return matches.Count switch
        {
            0 => (null, $"{noun} '{typeName}' was not found in the loaded solution's source"),
            1 => (matches[0], null),
            _ => (null, $"'{typeName}' is ambiguous ({matches.Count} {noun}es match); disambiguate"),
        };
    }

    // Loads the solution/project through the warm-solution cache (R42), oracle-shaped: abstains on a locator
    // failure, a load exception, or a WorkspaceFailed event. A held, still-fresh solution is reused.
    private async Task<(Solution? Solution, string? Reason)> LoadSolutionAsync(
        string solutionOrProjectPath, CancellationToken cancellationToken)
    {
        try { MsBuildLocatorRegistration.EnsureRegistered(); }
        catch (Exception ex) { return (null, $"no MSBuild/SDK found ({ex.Message}); cannot refactor"); }

        CachedSolution loaded;
        try
        {
            loaded = await _cache.OpenAsync(solutionOrProjectPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, $"could not load the workspace: {ex.Message}");
        }

        if (loaded.LoadFailures.Count > 0)
            return (null, "the workspace did not load cleanly; a solution-wide change could be incomplete, so it is refused. " +
                          $"First load failure: {loaded.LoadFailures[0]}");

        return (loaded.Solution, null);
    }

    private static async Task<HashSet<string>> CollectErrorSignaturesAsync(Solution solution, CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                continue;
            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
                if (diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    set.Add(Signature(diagnostic));
        }

        return set;
    }

    private static async Task<IReadOnlyList<string>> CollectIntroducedErrorsAsync(
        Solution solution, HashSet<string> baseline, CancellationToken cancellationToken)
    {
        var introduced = new List<string>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                continue;
            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    continue;
                if (!baseline.Contains(Signature(diagnostic)))
                {
                    var span = diagnostic.Location.GetLineSpan();
                    introduced.Add($"{diagnostic.Id} at {System.IO.Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}");
                }
            }
        }

        return introduced;
    }

    private static string Signature(Diagnostic diagnostic) =>
        $"{diagnostic.Id}|{diagnostic.Location.SourceTree?.FilePath ?? "<none>"}|{diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}";

    private static async Task<IReadOnlyList<TypeRefactorFileDiff>> BuildDiffsAsync(
        Solution before, Solution after, CancellationToken cancellationToken)
    {
        var diffs = new List<TypeRefactorFileDiff>();
        foreach (var changedId in after.GetChanges(before).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()))
        {
            var beforeText = await before.GetDocument(changedId)!.GetTextAsync(cancellationToken);
            var afterText = await after.GetDocument(changedId)!.GetTextAsync(cancellationToken);
            if (beforeText.ContentEquals(afterText))
                continue;
            var path = after.GetDocument(changedId)!.FilePath ?? after.GetDocument(changedId)!.Name;
            diffs.Add(new TypeRefactorFileDiff(path, afterText.ToString()));
        }

        return diffs;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateSourceTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in NestedTypes(type))
                yield return nested;
        }

        foreach (var child in ns.GetNamespaceMembers())
            foreach (var nested in EnumerateSourceTypes(child))
                yield return nested;
    }

    private static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in NestedTypes(nested))
                yield return deeper;
        }
    }
}

/// <summary>One file's staged type-refactor change (the full new file text, since an extract can add a declaration).</summary>
/// <param name="FilePath">The changed file's path.</param>
/// <param name="NewText">The full proposed new content of the file (staged, not written to disk).</param>
public sealed record TypeRefactorFileDiff(string FilePath, string NewText);

/// <summary>The outcome of a compiler-executed, verify-gated type refactor (T4).</summary>
/// <param name="Changed">Whether the change ran and verified clean.</param>
/// <param name="Reason">The abstention reason when <see cref="Changed" /> is false.</param>
/// <param name="Target">The resolved type's display name, when changed.</param>
/// <param name="Summary">A description of what changed.</param>
/// <param name="Diffs">The per-file staged changes, when changed.</param>
public sealed record TypeRefactorResult(
    bool Changed, string? Reason, string? Target, string? Summary, IReadOnlyList<TypeRefactorFileDiff> Diffs)
{
    /// <summary>Creates a successful, verified result.</summary>
    /// <param name="target">The resolved type display name.</param>
    /// <param name="summary">A description of the change.</param>
    /// <param name="diffs">The staged per-file changes.</param>
    /// <returns>A changed result.</returns>
    public static TypeRefactorResult Ok(string target, string summary, IReadOnlyList<TypeRefactorFileDiff> diffs) =>
        new(true, null, target, summary, diffs);

    /// <summary>Creates an abstention.</summary>
    /// <param name="reason">The concrete reason the change was refused.</param>
    /// <returns>An unchanged result.</returns>
    public static TypeRefactorResult Abstain(string reason) => new(false, reason, null, null, []);
}
