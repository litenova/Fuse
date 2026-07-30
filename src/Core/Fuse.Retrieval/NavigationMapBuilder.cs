using Fuse.Indexing;

namespace Fuse.Retrieval;

/// <summary>
///     Builds the <see cref="NavigationMap" /> returned on a partial or insufficient localization, from the
///     language-agnostic symbol, route, and file tables. The map lets an exploring agent that lacks a precise
///     anchor see what the request brushed against (areas, entry points, nearest symbols) and find its next
///     probe, so a refusal routes rather than dead-ends.
/// </summary>
internal sealed class NavigationMapBuilder
{
    private const int MaxAreas = 6;
    private const int MaxEntryPoints = 5;
    private const int MaxNearestSymbols = 8;
    private const int SymbolPoolSize = 200;

    // Conventional, language-agnostic entry-point file stems. A path whose name starts with one of these is an
    // orientation point an exploring agent recognizes regardless of language.
    private static readonly string[] EntryPointStems = ["Program", "Startup", "Main", "App", "index", "main"];

    // Short, ubiquitous words carry no scoping signal, so they are dropped before probing for nearest symbols.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "into", "that", "this", "fix", "bug", "add", "use", "set",
        "get", "new", "old", "via", "not", "but", "are", "was", "has", "can", "should", "when", "where",
        "merge", "branch", "pull", "request", "review",
    };

    private readonly IWorkspaceIndexQueryStore _store;

    public NavigationMapBuilder(IWorkspaceIndexQueryStore store) => _store = store;

    /// <summary>
    ///     Builds a navigation map for a request that did not localize confidently.
    /// </summary>
    /// <param name="request">The localization request.</param>
    /// <param name="ranked">The scored candidates (possibly empty), highest score first.</param>
    /// <param name="ask">The explicit ask for a sharper input.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The navigation map.</returns>
    public async Task<NavigationMap> BuildAsync(
        LocalizationRequest request,
        IReadOnlyList<ScoredCandidate> ranked,
        string ask,
        CancellationToken cancellationToken)
    {
        // Prefer the file paths the request actually brushed against; fall back to the index's own symbols when
        // nothing matched, so an insufficient result still hands back a real structural map rather than nothing.
        var pathPool = ranked
            .Select(c => c.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<SymbolListItem> symbols = [];
        if (pathPool.Count == 0)
        {
            symbols = await _store.ListSymbolsAsync(SymbolPoolSize, cancellationToken);
            pathPool = symbols.Select(s => s.FilePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        }

        var areas = TopAreas(pathPool);
        var entryPoints = await EntryPointsAsync(cancellationToken);
        var nearest = await NearestSymbolsAsync(request.Query, symbols, cancellationToken);
        return new NavigationMap(areas, entryPoints, nearest, ask);
    }

    // The most common top folders in the pool (first up-to-two path segments), most frequent first. These are
    // the areas a request leans toward and a useful coarse target for a refined query.
    private static IReadOnlyList<string> TopAreas(IReadOnlyList<string> paths)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var area = AreaOf(path);
            if (area.Length > 0)
                counts[area] = counts.GetValueOrDefault(area) + 1;
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(MaxAreas)
            .Select(kv => kv.Key)
            .ToList();
    }

    private static string AreaOf(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash < 0)
            return string.Empty;

        var dir = normalized[..lastSlash];
        var segments = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 2 ? dir : string.Join('/', segments[..2]);
    }

    private async Task<IReadOnlyList<string>> EntryPointsAsync(CancellationToken cancellationToken)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stem in EntryPointStems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in await _store.FindFilesByPathAsync(stem, 3, cancellationToken))
            {
                var name = NameOf(file.NormalizedPath);
                // Match the stem at a file-name boundary, so "Program.cs" counts but "ProgrammaticFoo.cs" does not.
                if (name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) && seen.Add(file.NormalizedPath))
                    found.Add(file.NormalizedPath);
                if (found.Count >= MaxEntryPoints)
                    return found;
            }
        }

        // Routes are entry points too; include a few when the index has them.
        foreach (var route in await _store.ListRoutesAsync(3, cancellationToken))
        {
            var label = $"{route.HttpMethod} {route.RoutePattern}";
            if (seen.Add(label))
                found.Add(label);
            if (found.Count >= MaxEntryPoints)
                break;
        }

        return found;
    }

    private async Task<IReadOnlyList<string>> NearestSymbolsAsync(
        string? query, IReadOnlyList<SymbolListItem> pool, CancellationToken cancellationToken)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0)
        {
            // No query terms to probe with (for example a merge-noise title): offer the index's public symbols.
            return pool
                .Where(s => s.IsPublicApi)
                .Select(s => s.Name)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxNearestSymbols)
                .ToList();
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var symbol in await _store.FindSymbolsByNameAsync(term, 3, cancellationToken))
            {
                if (seen.Add(symbol.Name))
                    names.Add(symbol.Name);
                if (names.Count >= MaxNearestSymbols)
                    return names;
            }
        }

        return names;
    }

    private static List<string> Tokenize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in query.Split(
            [' ', '\t', '\n', '\r', '.', ',', ':', ';', '(', ')', '[', ']', '{', '}', '/', '\\', '"', '\'', '#', '-', '_'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length >= 3 && !StopWords.Contains(raw) && seen.Add(raw))
                tokens.Add(raw);
        }

        return tokens;
    }

    private static string NameOf(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
    }
}
