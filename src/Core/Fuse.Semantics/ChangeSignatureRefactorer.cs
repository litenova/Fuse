using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Fuse.Semantics;

/// <summary>
///     Compiler-executed, constrained change-signature (T3): opens the workspace through MSBuild, resolves the
///     target method and its whole override/interface family, applies a best-effort syntactic rewrite (the new
///     parameter added to every declaration and an explicit argument added at every call site), then RECOMPILES
///     the solution and returns the staged diff only when no new diagnostic was introduced. Any regression makes
///     the tool abstain with the offending diagnostics named - never a "mostly right" diff.
/// </summary>
/// <remarks>
///     Correctness-by-verification, not correctness-by-construction: the rewriter only has to be good, because the
///     overlay-style recompile gate is the backstop. A reference the rewriter does not understand (a method-group
///     delegate conversion, an expression-tree call site) breaks compilation and is caught by the gate as an
///     abstention rather than committed as a bug. Named abstention classes (in docs): <c>params</c> interactions,
///     optional-parameter interactions, expression-tree call sites. Like <see cref="RenameRefactorer" />, it is
///     oracle-shaped: a solution that does not load cleanly, or a symbol that does not resolve unambiguously,
///     yields an abstention rather than a partial change.
/// </remarks>
public sealed class ChangeSignatureRefactorer
{
    private readonly WarmSolutionCache _cache;
    private readonly ChangeSignatureValidator _validator = new();
    private readonly ChangeSignatureVerifier _verifier = new();

    /// <summary>
    ///     Initializes a new instance of the <see cref="ChangeSignatureRefactorer" /> class.
    /// </summary>
    /// <param name="cache">
    ///     The warm-solution cache (R42) the change loads through; defaults to the process-wide
    ///     the supplied host-owned <see cref="WarmSolutionCache" />.
    /// </param>
    public ChangeSignatureRefactorer(WarmSolutionCache? cache = null) => _cache = cache ?? new WarmSolutionCache();

    /// <summary>
    ///     Adds a trailing parameter to a method (and its override/interface family) solution-wide, threading an
    ///     explicit argument value into every call site, and returns the staged diff or a named abstention.
    /// </summary>
    /// <param name="solutionOrProjectPath">The absolute path to the solution or project to load.</param>
    /// <param name="methodName">The simple name of the method whose signature to change.</param>
    /// <param name="containingTypeName">
    ///     The simple name of the declaring type, to disambiguate when several types declare a method of the same
    ///     name; null to match across all types (ambiguity then abstains).
    /// </param>
    /// <param name="parameterType">The new parameter's type, as written in source (for example <c>CancellationToken</c>).</param>
    /// <param name="parameterName">The new parameter's name.</param>
    /// <param name="argumentValue">The argument expression added at every call site (for example <c>default</c>).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The outcome: the per-file staged diffs, or an abstention with a concrete reason.</returns>
    public async Task<ChangeSignatureResult> AddParameterAsync(
        string solutionOrProjectPath,
        string methodName,
        string? containingTypeName,
        string parameterType,
        string parameterName,
        string argumentValue,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(methodName) || string.IsNullOrWhiteSpace(parameterType) || string.IsNullOrWhiteSpace(parameterName))
            return ChangeSignatureResult.Abstain("provide a method name, a parameter type, and a parameter name");

        var loaded = await LoadSolutionAsync(solutionOrProjectPath, cancellationToken);
        if (loaded.Solution is null)
            return ChangeSignatureResult.Abstain(loaded.Reason!);

        return await AddParameterToSolutionAsync(
            loaded.Solution, methodName, containingTypeName, parameterType, parameterName, argumentValue, cancellationToken);
    }

    // Loads the solution/project through the warm-solution cache (R42), oracle-shaped: abstains on a locator
    // failure, a load exception, or a WorkspaceFailed event, because a solution that did not load cleanly could
    // yield an incomplete change. A held, still-fresh solution is reused; the immutable Solution is forked for
    // the change, so sharing it across calls is safe.
    private async Task<(Solution? Solution, string? Reason)> LoadSolutionAsync(
        string solutionOrProjectPath, CancellationToken cancellationToken)
    {
        try { MsBuildLocatorRegistration.EnsureRegistered(); }
        catch (Exception ex) { return (null, $"no MSBuild/SDK found ({ex.Message}); cannot change the signature"); }

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
            return (null, "the workspace did not load cleanly; a solution-wide signature change could be incomplete, so it is refused. " +
                          $"First load failure: {loaded.LoadFailures[0]}");

        return (loaded.Solution, null);
    }

    // The load-independent core: resolve, collect the family, baseline, rewrite, verify, and diff over an already
    // loaded solution. Exposed to the test project so the rewrite + verify gate can be exercised in-memory over an
    // AdhocWorkspace, without an MSBuild load (deterministic, environment-independent).
    internal async Task<ChangeSignatureResult> AddParameterToSolutionAsync(
        Solution solution,
        string methodName,
        string? containingTypeName,
        string parameterType,
        string parameterName,
        string argumentValue,
        CancellationToken cancellationToken)
    {
        // Resolve the target method unambiguously across the loaded solution.
        var resolution = await _validator.ResolveMethodAsync(solution, methodName, containingTypeName, cancellationToken);
        if (resolution.Method is null)
            return ChangeSignatureResult.Abstain(resolution.Reason!);
        var method = resolution.Method;

        // Named abstention: a params tail cannot take a trailing parameter without changing call semantics.
        if (method.Parameters.Any(p => p.IsParams))
            return ChangeSignatureResult.Abstain($"'{method.Name}' has a params parameter; add-parameter abstains (params interaction)");

        // The whole family that must change together, or the override/interface contract breaks: the base-most
        // definition, every override, and every interface implementation.
        var family = await _validator.CollectMethodFamilyAsync(solution, method, cancellationToken);

        // Baseline compile-error signatures, so the verify gate can tell an INTRODUCED error from a pre-existing one.
        var baseline = await _verifier.CollectErrorSignaturesAsync(solution, cancellationToken);

        // Best-effort rewrite: the parameter into every declaration, the constant argument into every call site.
        return await RewriteVerifyAndStageAsync(
            solution, method, family, parameterType, parameterName, baseline,
            (_, _) => (argumentValue, false), cancellationToken);
    }

    /// <summary>
    ///     Adds a <c>CancellationToken</c> parameter to a method (and its family) and threads an in-scope token
    ///     into every call site where one is available, listing token-less sites as manual follow-ups; the change
    ///     is verify-gated and staged as a diff, or abstained with a reason.
    /// </summary>
    /// <param name="solutionOrProjectPath">The absolute path to the solution or project to load.</param>
    /// <param name="methodName">The simple name of the method to thread the token through.</param>
    /// <param name="containingTypeName">The declaring type's simple name to disambiguate, or null.</param>
    /// <param name="parameterName">The token parameter's name (for example <c>cancellationToken</c>).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The outcome: the staged diffs plus token-less follow-up sites, or an abstention with a reason.</returns>
    public async Task<ChangeSignatureResult> ThreadCancellationTokenAsync(
        string solutionOrProjectPath,
        string methodName,
        string? containingTypeName,
        string parameterName,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadSolutionAsync(solutionOrProjectPath, cancellationToken);
        if (loaded.Solution is null)
            return ChangeSignatureResult.Abstain(loaded.Reason!);
        return await ThreadCancellationTokenInSolutionAsync(
            loaded.Solution, methodName, containingTypeName, parameterName, cancellationToken);
    }

    internal async Task<ChangeSignatureResult> ThreadCancellationTokenInSolutionAsync(
        Solution solution,
        string methodName,
        string? containingTypeName,
        string parameterName,
        CancellationToken cancellationToken)
    {
        var resolution = await _validator.ResolveMethodAsync(solution, methodName, containingTypeName, cancellationToken);
        if (resolution.Method is null)
            return ChangeSignatureResult.Abstain(resolution.Reason!);
        var method = resolution.Method;
        if (method.Parameters.Any(p => p.IsParams))
            return ChangeSignatureResult.Abstain($"'{method.Name}' has a params parameter; threading abstains (params interaction)");

        var family = await _validator.CollectMethodFamilyAsync(solution, method, cancellationToken);
        var baseline = await _verifier.CollectErrorSignaturesAsync(solution, cancellationToken);
        return await RewriteVerifyAndStageAsync(
            solution, method, family, "CancellationToken", parameterName, baseline,
            ResolveTokenArgument, cancellationToken);
    }

    /// <summary>
    ///     Removes a parameter from a method (and its override/interface family) solution-wide, dropping the
    ///     corresponding argument at every call site, and returns the staged diff or a named abstention. Refuses
    ///     when the parameter is used in any body (the change would not compile) or when a call site passes a
    ///     non-trivial argument whose removal could drop a side effect (a silent semantic change).
    /// </summary>
    /// <param name="solutionOrProjectPath">The absolute path to the solution or project to load.</param>
    /// <param name="methodName">The simple name of the method whose parameter to remove.</param>
    /// <param name="containingTypeName">The declaring type's simple name to disambiguate, or null.</param>
    /// <param name="parameterName">The name of the parameter to remove.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The outcome: the per-file staged diffs, or an abstention with a concrete reason.</returns>
    public async Task<ChangeSignatureResult> RemoveParameterAsync(
        string solutionOrProjectPath,
        string methodName,
        string? containingTypeName,
        string parameterName,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadSolutionAsync(solutionOrProjectPath, cancellationToken);
        if (loaded.Solution is null)
            return ChangeSignatureResult.Abstain(loaded.Reason!);
        return await RemoveParameterInSolutionAsync(
            loaded.Solution, methodName, containingTypeName, parameterName, cancellationToken);
    }

    internal async Task<ChangeSignatureResult> RemoveParameterInSolutionAsync(
        Solution solution,
        string methodName,
        string? containingTypeName,
        string parameterName,
        CancellationToken cancellationToken)
    {
        var resolution = await _validator.ResolveMethodAsync(solution, methodName, containingTypeName, cancellationToken);
        if (resolution.Method is null)
            return ChangeSignatureResult.Abstain(resolution.Reason!);
        var method = resolution.Method;

        var index = -1;
        for (var i = 0; i < method.Parameters.Length; i++)
            if (method.Parameters[i].Name == parameterName)
                index = i;
        if (index < 0)
            return ChangeSignatureResult.Abstain($"'{method.Name}' has no parameter named '{parameterName}'");
        if (method.Parameters[index].IsParams)
            return ChangeSignatureResult.Abstain($"'{parameterName}' is a params parameter; remove-parameter abstains (params interaction)");

        var family = await _validator.CollectMethodFamilyAsync(solution, method, cancellationToken);

        // Safety pre-check the compile gate cannot see: the parameter must be unused in every family member's body
        // (removing a used parameter would not even compile, but naming the site is clearer than a raw diagnostic).
        var usedIn = await _validator.FindParameterUsageAsync(solution, family, index, cancellationToken);
        if (usedIn is not null)
            return ChangeSignatureResult.Abstain($"'{parameterName}' is used in the body of {usedIn}; remove-parameter abstains (it is not a dead parameter)");

        var baseline = await _verifier.CollectErrorSignaturesAsync(solution, cancellationToken);
        var rewrite = await ApplyRemovalAsync(solution, family, parameterName, index, cancellationToken);
        if (rewrite.Solution is null)
            return ChangeSignatureResult.Abstain(rewrite.Reason!);

        var introduced = await _verifier.CollectIntroducedErrorsAsync(rewrite.Solution, baseline, cancellationToken);
        if (introduced.Count > 0)
            return ChangeSignatureResult.Abstain($"the change introduced {introduced.Count} new compile error(s), so it is refused: {string.Join("; ", introduced.Take(5))}");

        var diffs = await _verifier.BuildDiffsAsync(solution, rewrite.Solution, cancellationToken);
        if (diffs.Count == 0)
            return ChangeSignatureResult.Abstain("the rewrite produced no change (the method or its call sites were not found in source)");

        return ChangeSignatureResult.Ok(method.ToDisplayString(), $"removed {parameterName}", diffs);
    }

    // Removes the parameter at the given index from every family declaration and the matching argument at every
    // call site (a named argument named parameterName, else the positional argument at index), abstaining when a
    // removed argument is not side-effect-free (dropping it could change behavior).
    private async Task<(Solution? Solution, string? Reason)> ApplyRemovalAsync(
        Solution solution,
        IReadOnlyCollection<IMethodSymbol> family,
        string parameterName,
        int index,
        CancellationToken cancellationToken)
    {
        var declEdits = new Dictionary<DocumentId, List<int>>();
        var callEdits = new Dictionary<DocumentId, List<int>>();

        foreach (var member in family)
        {
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                var doc = solution.GetDocument(reference.SyntaxTree);
                if (doc is null)
                    continue;
                var node = await reference.GetSyntaxAsync(cancellationToken);
                if (node is BaseMethodDeclarationSyntax { ParameterList: { } list } && index < list.Parameters.Count)
                    AddDeclSite(declEdits, doc.Id, list.Parameters[index].SpanStart);
            }

            foreach (var referenced in await SymbolFinder.FindReferencesAsync(member, solution, cancellationToken))
            {
                foreach (var location in referenced.Locations)
                {
                    var doc = location.Document;
                    var root = await doc.GetSyntaxRootAsync(cancellationToken);
                    var token = root?.FindToken(location.Location.SourceSpan.Start);
                    var invocation = token?.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation is null)
                        continue;
                    var argument = SelectArgument(invocation.ArgumentList, parameterName, index);
                    if (argument is null)
                        continue; // The parameter was omitted at this site (an optional argument); nothing to drop.
                    if (!IsSideEffectFree(argument.Expression))
                        return (null, $"call site at {HumanNode(invocation)} passes a non-trivial argument for '{parameterName}' that would be dropped; remove-parameter abstains (possible side effect)");
                    AddDeclSite(callEdits, doc.Id, argument.SpanStart);
                }
            }
        }

        var changed = solution;
        foreach (var docId in declEdits.Keys.Concat(callEdits.Keys).Distinct())
        {
            var document = changed.GetDocument(docId)!;
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken);
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null)
                continue;

            if (declEdits.TryGetValue(docId, out var declSpans))
                foreach (var span in declSpans)
                {
                    var parameter = root.FindToken(span).Parent?.AncestorsAndSelf().OfType<ParameterSyntax>().FirstOrDefault();
                    if (parameter is not null)
                        editor.RemoveNode(parameter);
                }

            if (callEdits.TryGetValue(docId, out var callSpans))
                foreach (var span in callSpans)
                {
                    var argument = root.FindToken(span).Parent?.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault();
                    if (argument is not null)
                        editor.RemoveNode(argument);
                }

            changed = changed.WithDocumentSyntaxRoot(docId, await editor.GetChangedDocument().GetSyntaxRootAsync(cancellationToken) ?? root);
        }

        return (changed, null);
    }

    // The argument that binds to the parameter: a named argument matching the name, else the positional argument
    // at the index (when the call supplied that many positional arguments), else null (the parameter was omitted).
    private static ArgumentSyntax? SelectArgument(ArgumentListSyntax list, string parameterName, int index)
    {
        var named = list.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == parameterName);
        if (named is not null)
            return named;
        var positional = list.Arguments.Where(a => a.NameColon is null).ToList();
        return index < positional.Count ? positional[index] : null;
    }

    // A conservative side-effect-free test: literals, identifiers, member access, `default`, and `this`/`base` are
    // safe to drop; anything that can invoke code (a call, object creation, an assignment) is not.
    private static bool IsSideEffectFree(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax => true,
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax member => IsSideEffectFree(member.Expression),
        DefaultExpressionSyntax => true,
        ThisExpressionSyntax or BaseExpressionSyntax => true,
        ParenthesizedExpressionSyntax paren => IsSideEffectFree(paren.Expression),
        _ when expression.IsKind(SyntaxKind.DefaultLiteralExpression) => true,
        _ => false,
    };

    /// <summary>
    ///     Reorders a method's parameters (and its override/interface family) into the given order and returns the
    ///     staged diff or a named abstention. Safe only when every call site names its arguments: a positional call
    ///     site would silently bind different values after a reorder, so any positional call site abstains.
    /// </summary>
    /// <param name="solutionOrProjectPath">The absolute path to the solution or project to load.</param>
    /// <param name="methodName">The simple name of the method whose parameters to reorder.</param>
    /// <param name="containingTypeName">The declaring type's simple name to disambiguate, or null.</param>
    /// <param name="newOrder">The parameter names in the desired order (a permutation of the current parameters).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The outcome: the per-file staged diffs, or an abstention with a concrete reason.</returns>
    public async Task<ChangeSignatureResult> ReorderParametersAsync(
        string solutionOrProjectPath,
        string methodName,
        string? containingTypeName,
        IReadOnlyList<string> newOrder,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadSolutionAsync(solutionOrProjectPath, cancellationToken);
        if (loaded.Solution is null)
            return ChangeSignatureResult.Abstain(loaded.Reason!);
        return await ReorderParametersInSolutionAsync(
            loaded.Solution, methodName, containingTypeName, newOrder, cancellationToken);
    }

    internal async Task<ChangeSignatureResult> ReorderParametersInSolutionAsync(
        Solution solution,
        string methodName,
        string? containingTypeName,
        IReadOnlyList<string> newOrder,
        CancellationToken cancellationToken)
    {
        var resolution = await _validator.ResolveMethodAsync(solution, methodName, containingTypeName, cancellationToken);
        if (resolution.Method is null)
            return ChangeSignatureResult.Abstain(resolution.Reason!);
        var method = resolution.Method;

        var current = method.Parameters.Select(p => p.Name).ToList();
        if (newOrder.Count != current.Count || !newOrder.OrderBy(n => n).SequenceEqual(current.OrderBy(n => n)))
            return ChangeSignatureResult.Abstain($"the new order must be a permutation of ({string.Join(", ", current)})");
        var permutation = newOrder.Select(n => current.IndexOf(n)).ToArray();
        if (permutation.SequenceEqual(Enumerable.Range(0, current.Count)))
            return ChangeSignatureResult.Abstain("the requested order is the current order; nothing to do");

        var family = await _validator.CollectMethodFamilyAsync(solution, method, cancellationToken);

        // Safety: a positional call site silently rebinds after a reorder, so require every call site to name its
        // arguments (a positional reorder with same types is the item's stated kill risk).
        var positionalSite = await _validator.FindFirstPositionalCallSiteAsync(solution, family, cancellationToken);
        if (positionalSite is not null)
            return ChangeSignatureResult.Abstain($"call site at {positionalSite} uses positional arguments; reorder abstains (only named-argument call sites are safe to reorder)");

        var baseline = await _verifier.CollectErrorSignaturesAsync(solution, cancellationToken);
        var rewrite = await ApplyReorderAsync(solution, family, permutation, cancellationToken);
        if (rewrite.Solution is null)
            return ChangeSignatureResult.Abstain(rewrite.Reason!);

        var introduced = await _verifier.CollectIntroducedErrorsAsync(rewrite.Solution, baseline, cancellationToken);
        if (introduced.Count > 0)
            return ChangeSignatureResult.Abstain($"the change introduced {introduced.Count} new compile error(s), so it is refused: {string.Join("; ", introduced.Take(5))}");

        var diffs = await _verifier.BuildDiffsAsync(solution, rewrite.Solution, cancellationToken);
        if (diffs.Count == 0)
            return ChangeSignatureResult.Abstain("the rewrite produced no change");

        return ChangeSignatureResult.Ok(method.ToDisplayString(), $"reordered to ({string.Join(", ", newOrder)})", diffs);
    }

    // Reorders each family declaration's parameter list per the permutation (all call sites are named, so they
    // need no edit). Trivia is normalized to ", " separators for a clean diff.
    private async Task<(Solution? Solution, string? Reason)> ApplyReorderAsync(
        Solution solution,
        IReadOnlyCollection<IMethodSymbol> family,
        int[] permutation,
        CancellationToken cancellationToken)
    {
        var declEdits = new Dictionary<DocumentId, List<int>>();
        foreach (var member in family)
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                var doc = solution.GetDocument(reference.SyntaxTree);
                if (doc is null)
                    continue;
                var node = await reference.GetSyntaxAsync(cancellationToken);
                if (node is BaseMethodDeclarationSyntax { ParameterList: { } list } && list.Parameters.Count == permutation.Length)
                    AddDeclSite(declEdits, doc.Id, list.SpanStart);
            }

        var changed = solution;
        foreach (var docId in declEdits.Keys)
        {
            var document = changed.GetDocument(docId)!;
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken);
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null)
                continue;
            foreach (var span in declEdits[docId])
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

            changed = changed.WithDocumentSyntaxRoot(docId, await editor.GetChangedDocument().GetSyntaxRootAsync(cancellationToken) ?? root);
        }

        return (changed, null);
    }

    // The shared tail both operations use: rewrite with the given per-site argument resolver, verify by recompile
    // (abstain on any introduced error), and stage the diffs (with any manual follow-ups).
    private async Task<ChangeSignatureResult> RewriteVerifyAndStageAsync(
        Solution solution,
        IMethodSymbol method,
        IReadOnlyCollection<IMethodSymbol> family,
        string parameterType,
        string parameterName,
        HashSet<string> baseline,
        Func<SemanticModel?, InvocationExpressionSyntax, (string Arg, bool FollowUp)> argResolver,
        CancellationToken cancellationToken)
    {
        var rewrite = await ApplyRewriteAsync(solution, family, parameterType, parameterName, argResolver, cancellationToken);
        if (rewrite.Solution is null)
            return ChangeSignatureResult.Abstain(rewrite.Reason!);
        var changed = rewrite.Solution;

        // Verify: recompile and abstain on any newly introduced compile error, naming it. This is the gate that
        // turns an imperfect rewriter into a safe one.
        var introduced = await _verifier.CollectIntroducedErrorsAsync(changed, baseline, cancellationToken);
        if (introduced.Count > 0)
        {
            var sites = string.Join("; ", introduced.Take(5));
            return ChangeSignatureResult.Abstain(
                $"the change introduced {introduced.Count} new compile error(s), so it is refused: {sites}");
        }

        var diffs = await _verifier.BuildDiffsAsync(solution, changed, cancellationToken);
        if (diffs.Count == 0)
            return ChangeSignatureResult.Abstain("the rewrite produced no change (the method or its call sites were not found in source)");

        return ChangeSignatureResult.Ok(method.ToDisplayString(), $"{parameterType} {parameterName}", diffs, rewrite.FollowUps);
    }

    // Applies the parameter to every family declaration and an argument to every invocation call site, grouped by
    // document so each document is edited once. The per-site argument comes from argResolver, so add-parameter
    // passes a constant while the CancellationToken recipe threads an in-scope token (or default, flagged as a
    // manual follow-up). Non-invocation references (method groups, nameof) are left alone; if that breaks
    // compilation the verify gate abstains.
    private async Task<(Solution? Solution, string? Reason, IReadOnlyList<string> FollowUps)> ApplyRewriteAsync(
        Solution solution,
        IReadOnlyCollection<IMethodSymbol> family,
        string parameterType,
        string parameterName,
        Func<SemanticModel?, InvocationExpressionSyntax, (string Arg, bool FollowUp)> argResolver,
        CancellationToken cancellationToken)
    {
        // Collect edit sites per document: declaration parameter-list spans, and (argument-list span, arg text).
        var declSites = new Dictionary<DocumentId, List<int>>();
        var callSites = new Dictionary<DocumentId, List<(int Span, string Arg)>>();
        var followUps = new List<string>();

        foreach (var member in family)
        {
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var doc = solution.GetDocument(reference.SyntaxTree);
                if (doc is null)
                    continue;
                var node = await reference.GetSyntaxAsync(cancellationToken);
                if (node is BaseMethodDeclarationSyntax { ParameterList: { } list })
                    AddDeclSite(declSites, doc.Id, list.SpanStart);
            }

            foreach (var referenced in await SymbolFinder.FindReferencesAsync(member, solution, cancellationToken))
            {
                foreach (var location in referenced.Locations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var doc = location.Document;
                    var root = await doc.GetSyntaxRootAsync(cancellationToken);
                    var token = root?.FindToken(location.Location.SourceSpan.Start);
                    var invocation = token?.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation is null)
                        continue;
                    var model = await doc.GetSemanticModelAsync(cancellationToken);
                    var (arg, followUp) = argResolver(model, invocation);
                    if (AddCallSite(callSites, doc.Id, invocation.ArgumentList.SpanStart, arg) && followUp)
                        followUps.Add(HumanNode(invocation));
                }
            }
        }

        var changed = solution;
        var affected = declSites.Keys.Concat(callSites.Keys).Distinct().ToList();
        foreach (var docId in affected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = changed.GetDocument(docId)!;
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken);
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null)
                continue;

            if (declSites.TryGetValue(docId, out var declSpans))
            {
                foreach (var span in declSpans)
                {
                    var list = root.FindToken(span).Parent?.AncestorsAndSelf().OfType<ParameterListSyntax>().FirstOrDefault();
                    if (list is null)
                        continue;
                    // Leading space so the inserted separator renders "int x, int n", not "int x,int n" - a clean
                    // staged diff an agent can apply without a reformat.
                    var parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameterName))
                        .WithType(SyntaxFactory.ParseTypeName(parameterType).WithTrailingTrivia(SyntaxFactory.Space))
                        .WithLeadingTrivia(SyntaxFactory.Space);
                    editor.ReplaceNode(list, list.AddParameters(parameter));
                }
            }

            if (callSites.TryGetValue(docId, out var callSpans))
            {
                foreach (var (span, arg) in callSpans)
                {
                    var list = root.FindToken(span).Parent?.AncestorsAndSelf().OfType<ArgumentListSyntax>().FirstOrDefault();
                    if (list is null)
                        continue;
                    var argument = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(arg))
                        .WithLeadingTrivia(SyntaxFactory.Space);
                    editor.ReplaceNode(list, list.AddArguments(argument));
                }
            }

            changed = changed.WithDocumentSyntaxRoot(docId, await editor.GetChangedDocument().GetSyntaxRootAsync(cancellationToken) ?? root);
        }

        return (changed, null, followUps);
    }

    // Resolves the argument for a CancellationToken threading call site: the name of an in-scope CancellationToken
    // (a parameter or local visible at the call), or "default" flagged as a manual follow-up when none is in scope.
    private static (string Arg, bool FollowUp) ResolveTokenArgument(SemanticModel? model, InvocationExpressionSyntax invocation)
    {
        if (model is not null)
        {
            var inScope = model.LookupSymbols(invocation.SpanStart)
                .Where(s => s is IParameterSymbol or ILocalSymbol)
                .Select(s => (Symbol: s, Type: (s as IParameterSymbol)?.Type ?? (s as ILocalSymbol)?.Type))
                .FirstOrDefault(t => t.Type is { Name: "CancellationToken", ContainingNamespace.Name: "Threading" });
            if (inScope.Symbol is not null)
                return (inScope.Symbol.Name, false);
        }

        // No token in scope: pass default and list the site so a human threads a real token later.
        return ("default", true);
    }

    private static string HumanNode(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return $"{System.IO.Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}";
    }

    private static void AddDeclSite(Dictionary<DocumentId, List<int>> sites, DocumentId id, int span)
    {
        if (!sites.TryGetValue(id, out var list))
            sites[id] = list = [];
        if (!list.Contains(span))
            list.Add(span);
    }

    private static bool AddCallSite(Dictionary<DocumentId, List<(int Span, string Arg)>> sites, DocumentId id, int span, string arg)
    {
        if (!sites.TryGetValue(id, out var list))
            sites[id] = list = [];
        if (list.Any(s => s.Span == span))
            return false;
        list.Add((span, arg));
        return true;
    }

}

/// <summary>One file's staged change-signature edit.</summary>
/// <param name="FilePath">The changed file's path.</param>
/// <param name="UnifiedDiff">The line-level diff of the change (staged, not written to disk).</param>
public sealed record ChangeSignatureFileDiff(string FilePath, string UnifiedDiff);

/// <summary>The outcome of a compiler-executed, verify-gated change-signature (T3).</summary>
/// <param name="Changed">Whether the change ran and verified clean.</param>
/// <param name="Reason">The abstention reason when <see cref="Changed" /> is false.</param>
/// <param name="OldSignature">The resolved method's display signature, when changed.</param>
/// <param name="Added">A description of what was added, when changed.</param>
/// <param name="Diffs">The per-file staged diffs, when changed.</param>
/// <param name="ManualFollowUps">
///     Call sites the change could not fully resolve and a human should review (for the CancellationToken recipe,
///     the sites where no in-scope token was found so <c>default</c> was passed); empty for a fully-threaded change.
/// </param>
public sealed record ChangeSignatureResult(
    bool Changed,
    string? Reason,
    string? OldSignature,
    string? Added,
    IReadOnlyList<ChangeSignatureFileDiff> Diffs,
    IReadOnlyList<string> ManualFollowUps)
{
    /// <summary>Creates a successful, verified change result.</summary>
    /// <param name="oldSignature">The resolved method display signature.</param>
    /// <param name="added">A description of what was added.</param>
    /// <param name="diffs">The staged per-file diffs.</param>
    /// <param name="manualFollowUps">The call sites listed for manual review, or empty.</param>
    /// <returns>A changed result.</returns>
    public static ChangeSignatureResult Ok(
        string oldSignature, string added, IReadOnlyList<ChangeSignatureFileDiff> diffs, IReadOnlyList<string>? manualFollowUps = null) =>
        new(true, null, oldSignature, added, diffs, manualFollowUps ?? []);

    /// <summary>Creates an abstention.</summary>
    /// <param name="reason">The concrete reason the change was refused.</param>
    /// <returns>An unchanged result.</returns>
    public static ChangeSignatureResult Abstain(string reason) => new(false, reason, null, null, [], []);
}
