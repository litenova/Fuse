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
    private readonly ChangeSignatureEditPlanner _editPlanner = new();
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
            ChangeSignatureEditPlanner.ResolveTokenArgument, cancellationToken);
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
        var rewrite = await _editPlanner.RemoveParameterAsync(solution, family, parameterName, index, cancellationToken);
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
        var rewrite = await _editPlanner.ReorderParametersAsync(solution, family, permutation, cancellationToken);
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
        var rewrite = await _editPlanner.AddParameterAsync(solution, family, parameterType, parameterName, argResolver, cancellationToken);
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
