namespace Fuse.Collection;

/// <summary>
///     The default set of directory names pruned from a workspace scan, plus per-repository additions read from
///     a <c>.fuseignore</c> file. Shared by every discovery path so the file scanner and the .NET workspace
///     discoverer agree on what to skip.
/// </summary>
/// <remarks>
///     These are version-control metadata, build outputs, dependency trees, and editor or AI-agent tooling
///     directories: never product source, and sometimes full duplicate checkouts of the repository (for example
///     Claude Code worktrees under <c>.claude</c>). Matching is by path segment, case-insensitive. A repository
///     can add names in a <c>.fuseignore</c> file at its root, one directory name per line, where <c>#</c>
///     starts a comment.
/// </remarks>
public static class WorkspaceExclusions
{
    /// <summary>The name of the optional per-repository exclusion file read from the workspace root.</summary>
    public const string FuseIgnoreFileName = ".fuseignore";

    /// <summary>The directory names pruned from every workspace scan by default.</summary>
    public static IReadOnlyList<string> DefaultDirectoryNames { get; } =
    [
        // Version control metadata.
        ".git", ".hg", ".svn",
        // Fuse's own store.
        ".fuse",
        // AI-agent tooling (Claude Code worktrees live under .claude).
        ".claude", ".cursor", ".aider", ".continue",
        // Editors and IDEs.
        ".vs", ".vscode", ".idea",
        // .NET build output.
        "bin", "obj",
        // Dependency trees.
        "node_modules", "vendor", "Pods",
        // Python environments and caches.
        ".venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox",
        // Common build outputs and framework caches.
        "dist", "out", "target", ".next", ".nuxt", ".svelte-kit", ".turbo", ".parcel-cache", ".gradle", ".terraform",
    ];

    /// <summary>
    ///     Whether a directory is a nested version-control root: it directly contains a <c>.git</c> entry (a
    ///     worktree or submodule gitfile, or an embedded clone's directory), or a <c>.hg</c> or <c>.svn</c>
    ///     directory. Such a directory is a separate checkout and must not be enumerated as part of the workspace.
    /// </summary>
    /// <param name="directory">The absolute directory path to test.</param>
    /// <returns>True when the directory holds version-control metadata of its own.</returns>
    public static bool IsVcsRoot(string directory)
    {
        try
        {
            var git = Path.Combine(directory, ".git");
            return File.Exists(git)
                || Directory.Exists(git)
                || Directory.Exists(Path.Combine(directory, ".hg"))
                || Directory.Exists(Path.Combine(directory, ".svn"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Returns the default excluded directory names plus any listed in a <c>.fuseignore</c> file or the
    ///     <c>ignore</c> array of the root <c>fuse.json</c>.
    /// </summary>
    /// <param name="rootDirectory">The workspace root that may contain a <c>.fuseignore</c> or <c>fuse.json</c> file.</param>
    /// <returns>The merged, de-duplicated (case-insensitive) set of directory names to prune.</returns>
    /// <remarks>
    ///     The default set plus these per-repository additions is combined with nested version-control pruning (see
    ///     <see cref="IsVcsRoot" />) so vendored checkouts and generated trees are never indexed (R25). Beyond the
    ///     defaults (<c>node_modules</c>, <c>bin</c>, <c>obj</c>, and the rest), a repository excludes an extra
    ///     directory by name via <c>fuse.json</c> <c>{ "ignore": ["dir", ...] }</c> or a <c>.fuseignore</c> line.
    /// </remarks>
    public static IReadOnlyList<string> LoadDirectoryNames(string rootDirectory)
    {
        var names = new List<string>(DefaultDirectoryNames);
        names.AddRange(ReadFuseIgnoreNames(rootDirectory));
        names.AddRange(WorkspaceConfiguration.LoadOrThrow(rootDirectory).Ignore.SelectMany(ToDirectoryNames));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The <c>fuse.json</c> key holding an array of extra directory names to exclude from indexing.</summary>
    public const string IgnoreConfigKey = "ignore";

    private static IEnumerable<string> ToDirectoryNames(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        var name = raw.Trim().TrimEnd('/', '\\').Split('/', '\\').LastOrDefault();
        return string.IsNullOrEmpty(name) ? [] : [name];
    }

    // Reads directory names from a root .fuseignore. Each non-comment line is a directory name; a trailing slash
    // or a path is reduced to its final segment so "some/dir/" and "dir" both prune a directory named "dir". A
    // missing or unreadable file yields no additions rather than failing the scan.
    private static IEnumerable<string> ReadFuseIgnoreNames(string rootDirectory)
    {
        var path = Path.Combine(rootDirectory, FuseIgnoreFileName);
        string[] lines;
        try
        {
            if (!File.Exists(path))
                return [];
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var name = line.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault();
            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        }

        return names;
    }
}
