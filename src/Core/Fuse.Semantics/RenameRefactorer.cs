using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;

namespace Fuse.Semantics;

/// <summary>
///     Compiler-executed solution-wide rename (R7): opens the workspace through MSBuild, resolves the named
///     symbol, renames it and every reference through Roslyn's <see cref="Renamer" />, and returns the change as
///     a staged unified diff, never touching the working tree. Because Roslyn drives the rename, a same-named
///     local or unrelated symbol is not touched, which a textual rename cannot guarantee.
/// </summary>
/// <remarks>
///     Oracle-shaped: the rename is only complete if every project loaded, so a solution that does not fully load
///     yields an abstention rather than a partial rename (which is worse than none). The result is a diff for the
///     agent to review and, per R1, to re-check; a rename that crosses a boundary Roslyn does not see (a string
///     in config, a reflection call) surfaces as a diagnostic on re-check rather than a silently committed bug.
/// </remarks>
public sealed class RenameRefactorer
{
    private readonly WarmSolutionCache _cache;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RenameRefactorer" /> class.
    /// </summary>
    /// <param name="cache">
    ///     The warm-solution cache (R42) the rename loads through; defaults to the process-wide
    ///     the supplied host-owned <see cref="WarmSolutionCache" />, so a second refactor in the same session reuses the held
    ///     solution instead of re-opening MSBuild.
    /// </param>
    public RenameRefactorer(WarmSolutionCache? cache = null) => _cache = cache ?? new WarmSolutionCache();

    /// <summary>
    ///     Renames a symbol solution-wide and returns the staged diffs.
    /// </summary>
    /// <param name="solutionOrProjectPath">The absolute path to the solution or project to load.</param>
    /// <param name="symbolName">The simple name of the symbol to rename.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="cancellationToken">A token to cancel the rename.</param>
    /// <returns>The rename outcome: the per-file staged diffs, or an abstention with a concrete reason.</returns>
    public async Task<RenameResult> RenameAsync(
        string solutionOrProjectPath, string symbolName, string newName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbolName) || string.IsNullOrWhiteSpace(newName))
            return RenameResult.Abstain("provide a symbol name and a new name");

        try { MsBuildLocatorRegistration.EnsureRegistered(); }
        catch (Exception ex) { return RenameResult.Abstain($"no MSBuild/SDK found ({ex.Message}); cannot rename"); }

        // Load through the warm-solution cache (R42): a held, still-fresh solution is reused; otherwise a fresh
        // MSBuildWorkspace is opened and cached. Only real load failures abstain; benign warnings (analyzer/
        // SDK-resolver notes) are ignored. See WorkspaceLoadFailures.
        CachedSolution loaded;
        try
        {
            loaded = await _cache.OpenAsync(solutionOrProjectPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RenameResult.Abstain($"could not load the workspace to rename: {ex.Message}");
        }

        var solution = loaded.Solution;
        var loadFailures = loaded.LoadFailures;

        // Resolve the symbol across all projects; if it does not resolve, or the load reported problems, abstain
        // rather than produce an incomplete rename.
        ISymbol? symbol = null;
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
                return RenameResult.Abstain($"project '{project.Name}' produced no compilation; the rename would be incomplete");
            symbol ??= FindSymbol(compilation, symbolName);
        }

        if (symbol is null)
            return RenameResult.Abstain($"symbol '{symbolName}' was not found in the loaded solution");
        if (loadFailures.Count > 0)
            return RenameResult.Abstain(
                "the workspace did not load cleanly; a solution-wide rename could be incomplete, so it is refused. " +
                $"First load failure: {loadFailures[0]}");

        Solution renamed;
        try
        {
            renamed = await Renamer.RenameSymbolAsync(
                solution, symbol, new SymbolRenameOptions(), newName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RenameResult.Abstain($"the rename failed: {ex.Message}");
        }

        var diffs = new List<RenameFileDiff>();
        foreach (var changedId in renamed.GetChanges(solution).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = await solution.GetDocument(changedId)!.GetTextAsync(cancellationToken);
            var after = await renamed.GetDocument(changedId)!.GetTextAsync(cancellationToken);
            if (before.ContentEquals(after))
                continue;
            var path = renamed.GetDocument(changedId)!.FilePath ?? renamed.GetDocument(changedId)!.Name;
            diffs.Add(new RenameFileDiff(path, BuildLineDiff(before.ToString(), after.ToString())));
        }

        return RenameResult.Ok(symbol.ToDisplayString(), newName, diffs);
    }

    // A compact line-level diff. A rename replaces identifiers in place without inserting or deleting lines, so a
    // positional line comparison is accurate here (it is not a general-purpose diff); each differing line is
    // emitted as a "-" old / "+" new pair with its 1-based line number.
    private static string BuildLineDiff(string before, string after)
    {
        var beforeLines = before.Replace("\r\n", "\n").Split('\n');
        var afterLines = after.Replace("\r\n", "\n").Split('\n');
        var builder = new System.Text.StringBuilder();
        var max = Math.Max(beforeLines.Length, afterLines.Length);
        for (var i = 0; i < max; i++)
        {
            var b = i < beforeLines.Length ? beforeLines[i] : null;
            var a = i < afterLines.Length ? afterLines[i] : null;
            if (b == a)
                continue;
            if (b is not null)
                builder.AppendLine($"-{i + 1}: {b}");
            if (a is not null)
                builder.AppendLine($"+{i + 1}: {a}");
        }

        return builder.ToString().TrimEnd();
    }

    // Finds a type or a member by simple name in a compilation's own source. Types first, then members, so a
    // type rename (the common case) resolves deterministically.
    private static ISymbol? FindSymbol(Compilation compilation, string name)
    {
        foreach (var type in EnumerateSourceTypes(compilation.Assembly.GlobalNamespace))
        {
            if (type.Name == name)
                return type;
            foreach (var member in type.GetMembers())
                if (member.Name == name && !member.IsImplicitlyDeclared)
                    return member;
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateSourceTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
            yield return type;
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var nested in EnumerateSourceTypes(child))
                yield return nested;
    }
}

/// <summary>One file's staged rename change.</summary>
/// <param name="FilePath">The changed file's path.</param>
/// <param name="UnifiedDiff">The unified diff of the change (staged, not written to disk).</param>
public sealed record RenameFileDiff(string FilePath, string UnifiedDiff);

/// <summary>The outcome of a compiler-executed rename (R7).</summary>
/// <param name="Renamed">Whether the rename ran (the symbol resolved and the solution loaded cleanly).</param>
/// <param name="Reason">The abstention reason when <see cref="Renamed" /> is false.</param>
/// <param name="OldName">The resolved symbol's display name, when renamed.</param>
/// <param name="NewName">The new name, when renamed.</param>
/// <param name="Diffs">The per-file staged diffs, when renamed.</param>
public sealed record RenameResult(
    bool Renamed, string? Reason, string? OldName, string? NewName, IReadOnlyList<RenameFileDiff> Diffs)
{
    /// <summary>Creates a successful rename result.</summary>
    /// <param name="oldName">The resolved symbol display name.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="diffs">The staged per-file diffs.</param>
    /// <returns>A renamed result.</returns>
    public static RenameResult Ok(string oldName, string newName, IReadOnlyList<RenameFileDiff> diffs) =>
        new(true, null, oldName, newName, diffs);

    /// <summary>Creates an abstention.</summary>
    /// <param name="reason">The concrete reason the rename was refused.</param>
    /// <returns>An unrenamed result.</returns>
    public static RenameResult Abstain(string reason) => new(false, reason, null, null, []);
}
