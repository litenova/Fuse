using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Fuse.Semantics;

/// <summary>
///     Resolves the project files that include one C# source path without loading an MSBuild solution.
/// </summary>
/// <remarks>
///     The resolver reads project items and the SDK default-compile convention before a compiler operation starts.
///     A linked source file can intentionally belong to more than one project, so callers receive every owner and
///     must verify each one before returning a clean result.
/// </remarks>
public sealed class ProjectOwnershipResolver
{
    private readonly DotNetWorkspaceDiscoverer _discoverer;

    /// <summary>
    ///     Initializes a project ownership resolver.
    /// </summary>
    /// <param name="discoverer">The repository project discoverer.</param>
    public ProjectOwnershipResolver(DotNetWorkspaceDiscoverer? discoverer = null) =>
        _discoverer = discoverer ?? new DotNetWorkspaceDiscoverer();

    /// <summary>
    ///     Finds every project that includes a repository-relative C# source or Razor markup path.
    /// </summary>
    /// <remarks>
    ///     Source-code files are owned by any project that includes them or uses default compile items. Razor
    ///     markup (<c>.razor</c>) and MVC view markup (<c>.cshtml</c>) are Content items rather than Compile
    ///     items, so they are owned only by projects built by a Razor-capable SDK (the ASP.NET / Web / Razor
    ///     SDKs) that has not disabled its default Razor items.
    /// </remarks>
    /// <param name="rootDirectory">The repository root.</param>
    /// <param name="relativeFilePath">The repository-relative source or markup path.</param>
    /// <param name="cancellationToken">A token that stops project discovery or project-file reads.</param>
    /// <returns>The selected project paths, or an empty result when no project includes the source file.</returns>
    public async Task<ProjectOwnership> ResolveAsync(
        string rootDirectory,
        string relativeFilePath,
        CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var file = Path.GetFullPath(Path.Combine(root, relativeFilePath));
        if (!IsUnderRoot(root, file))
            throw new ArgumentException("The source path must stay under the repository root.", nameof(relativeFilePath));

        var projects = await _discoverer.DiscoverProjectPathsAsync(root, cancellationToken);
        var owners = new List<string>();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IncludesFileAsync(project, file, cancellationToken))
                owners.Add(project);
        }

        return new ProjectOwnership(root, NormalizeRelative(root, file), owners);
    }

    private static async Task<bool> IncludesFileAsync(
        string projectPath,
        string filePath,
        CancellationToken cancellationToken)
    {
        XDocument document;
        try
        {
            var source = await File.ReadAllTextAsync(projectPath, cancellationToken);
            document = XDocument.Parse(source, LoadOptions.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }

        var root = document.Root;
        if (root is null)
            return false;

        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var compileItems = root
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Compile", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var includes = compileItems
            .SelectMany(element => AttributeValues(element, "Include"))
            .ToArray();
        var removes = compileItems
            .SelectMany(element => AttributeValues(element, "Remove"))
            .ToArray();

        // An SDK project can use default compile items and add one linked source file explicitly. The explicit
        // item must not suppress ownership of every ordinary source file in the project directory.
        var explicitlyIncluded = includes.Any(pattern => Matches(projectDirectory, filePath, pattern));
        var defaultIncluded = UsesDefaultCompileItems(root) && IsDefaultCompileFile(projectDirectory, filePath);
        var razorDefaultIncluded = UsesDefaultRazorItems(root) && IsDefaultRazorFile(projectDirectory, filePath);
        return (explicitlyIncluded || defaultIncluded || razorDefaultIncluded)
            && !removes.Any(pattern => Matches(projectDirectory, filePath, pattern));
    }

    private static IEnumerable<string> AttributeValues(XElement element, string attributeName)
    {
        var raw = element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, attributeName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !value.Contains("$(", StringComparison.Ordinal));
    }

    private static bool UsesDefaultCompileItems(XElement project)
    {
        var sdk = project.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "Sdk", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (string.IsNullOrWhiteSpace(sdk))
            return false;

        var disabled = project
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "EnableDefaultCompileItems", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .LastOrDefault(value => value.Length > 0);
        return !string.Equals(disabled, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefaultCompileFile(string projectDirectory, string filePath)
    {
        if (!Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || !IsUnderRoot(projectDirectory, filePath))
            return false;

        var relative = NormalizeRelative(projectDirectory, filePath);
        return !relative.Split('/').Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    // Razor markup and MVC view markup are Content items, not Compile items, so IsDefaultCompileFile (which gates
    // on a source-code extension) would never own a .razor or .cshtml file. These are the markup extensions the
    // .NET SDKs add as default Content items when a project is not plain-old-source-code. The bin/obj exclusion
    // matches IsDefaultCompileFile.
    private static bool IsDefaultRazorFile(string projectDirectory, string filePath)
    {
        if (!IsUnderRoot(projectDirectory, filePath))
            return false;

        var extension = Path.GetExtension(filePath).Equals(".razor", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(filePath).Equals(".cshtml", StringComparison.OrdinalIgnoreCase);
        if (!extension)
            return false;

        var relative = NormalizeRelative(projectDirectory, filePath);
        return !relative.Split('/').Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    // A project can own a default .razor or .cshtml file only when it is built by an SDK that wires Razor markup
    // into the build (the ASP.NET / Web / Razor SDKs). A plain Microsoft.NET.Sdk class library adds no default
    // Razor items, so a .razor file inside it is not compiled by it and must not be owned by it - matching the
    // MSBuild rule that adds .razor/.cshtml as default Content items only when the project is not plain-old-
    // source-code. The EnableDefaultRazorCompileItems switch is honored: when a Razor SDK disables it, the
    // project no longer owns its default Razor files.
    private static bool UsesDefaultRazorItems(XElement project)
    {
        var sdk = project.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "Sdk", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (sdk is not ("Microsoft.NET.Sdk.Web" or "Microsoft.NET.Sdk.Razor"))
            return false;

        var disabled = project
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "EnableDefaultRazorCompileItems", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .LastOrDefault(value => value.Length > 0);
        return !string.Equals(disabled, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(string projectDirectory, string filePath, string include)
    {
        var normalizedPattern = include.Replace('\\', '/');
        var relative = NormalizeRelative(projectDirectory, filePath);
        if (!normalizedPattern.Contains('*') && !normalizedPattern.Contains('?'))
        {
            try
            {
                var included = Path.GetFullPath(Path.Combine(projectDirectory, include));
                return PathsEqual(included, filePath);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return GlobRegex(normalizedPattern).IsMatch(relative);
    }

    private static Regex GlobRegex(string pattern)
    {
        var expression = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        index++;
                        expression.Append("(?:.*/)?");
                    }
                    else
                    {
                        expression.Append(".*");
                    }
                }
                else
                {
                    expression.Append("[^/]*");
                }

                continue;
            }

            if (character == '?')
            {
                expression.Append("[^/]");
                continue;
            }

            expression.Append(Regex.Escape(character.ToString()));
        }

        expression.Append('$');
        return new Regex(expression.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     The project ownership of a source file for a compiler operation.
/// </summary>
/// <param name="Root">The normalized repository root.</param>
/// <param name="RelativeFilePath">The normalized repository-relative source path.</param>
/// <param name="ProjectPaths">Every project that includes the source path.</param>
public sealed record ProjectOwnership(
    string Root,
    string RelativeFilePath,
    IReadOnlyList<string> ProjectPaths)
{
    /// <summary>Whether at least one project includes the source path.</summary>
    public bool IsResolved => ProjectPaths.Count > 0;

    /// <summary>Whether the source is linked into more than one project.</summary>
    public bool IsLinked => ProjectPaths.Count > 1;
}
