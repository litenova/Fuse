using Fuse.Indexing;

namespace Fuse.Retrieval;

/// <summary>
///     Produces candidate files and symbols for a localization request from one signal (exact resolution,
///     full-text, path, or diff).
/// </summary>
public interface ICandidateGenerator
{
    /// <summary>
    ///     Generates candidates for a request.
    /// </summary>
    /// <param name="request">The localization request.</param>
    /// <param name="cancellationToken">A token to cancel generation.</param>
    /// <returns>The candidates this generator found (possibly empty).</returns>
    Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken);
}

/// <summary>
///     Runs every registered candidate generator and concatenates their output.
/// </summary>
public sealed class CandidateGenerator
{
    private readonly IReadOnlyList<ICandidateGenerator> _generators;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CandidateGenerator" /> class.
    /// </summary>
    /// <param name="generators">The generators to run.</param>
    public CandidateGenerator(IEnumerable<ICandidateGenerator> generators) => _generators = generators.ToList();

    /// <summary>
    ///     Creates a generator wired with the default set (exact, lexical BM25F with PRF, path, diff) over a store.
    /// </summary>
    /// <param name="store">The index store to query.</param>
    /// <param name="changeSource">An optional change source so <c>ChangedSince</c> resolves to changed-file seeds.</param>
    /// <returns>A generator with the standard candidate sources.</returns>
    /// <remarks>
    ///     The lexical channel is <see cref="LexicalCandidateGenerator" />, which preserves the BM25F rank and
    ///     adds pseudo-relevance feedback. The <see cref="ICandidateGenerator" /> seam stays open for a future
    ///     generator (for example a re-added dense channel via a plugin).
    /// </remarks>
    public static CandidateGenerator CreateDefault(
        IWorkspaceIndexStore store, IChangeSource? changeSource = null)
    {
        var generators = new List<ICandidateGenerator>
        {
            new ExactCandidateGenerator(store),
            new LexicalCandidateGenerator(store),
            new PathCandidateGenerator(store),
            new DiffCandidateGenerator(changeSource),
        };

        return new CandidateGenerator(generators);
    }

    /// <summary>
    ///     Generates candidates from all sources.
    /// </summary>
    /// <param name="request">The localization request.</param>
    /// <param name="cancellationToken">A token to cancel generation.</param>
    /// <returns>The concatenated candidates from every generator.</returns>
    public async Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        var candidates = new List<CandidateNode>();
        foreach (var generator in _generators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.AddRange(await generator.GenerateAsync(request, cancellationToken));
        }

        return candidates;
    }
}

/// <summary>
///     Generates candidates by exact semantic resolution (service, request, route, config, symbol).
/// </summary>
public sealed class ExactCandidateGenerator : ICandidateGenerator
{
    private readonly SemanticResolver _resolver;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ExactCandidateGenerator" /> class.
    /// </summary>
    /// <param name="store">The index store to query.</param>
    public ExactCandidateGenerator(IWorkspaceIndexGraphStore store) => _resolver = new SemanticResolver(store);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        var candidates = new List<CandidateNode>();

        if (!string.IsNullOrWhiteSpace(request.Service))
            Add(candidates, await _resolver.ResolveServiceAsync(request.Service, cancellationToken), CandidateSource.ServiceExact);
        if (!string.IsNullOrWhiteSpace(request.Request))
            Add(candidates, await _resolver.ResolveRequestAsync(request.Request, cancellationToken), CandidateSource.RequestExact);
        if (!string.IsNullOrWhiteSpace(request.Route))
            Add(candidates, await _resolver.ResolveRouteAsync(request.Route, cancellationToken), CandidateSource.RouteExact);
        if (!string.IsNullOrWhiteSpace(request.ConfigSection))
            Add(candidates, await _resolver.ResolveConfigAsync(request.ConfigSection, cancellationToken), CandidateSource.ConfigExact);
        if (!string.IsNullOrWhiteSpace(request.Focus))
            Add(candidates, await _resolver.ResolveSymbolAsync(request.Focus, cancellationToken), CandidateSource.SymbolExact);

        return candidates;
    }

    private static void Add(List<CandidateNode> candidates, ResolveResult result, CandidateSource source)
    {
        foreach (var match in result.Matches)
        {
            if (match.FilePath is null)
                continue;

            candidates.Add(new CandidateNode(
                NodeId: match.NodeId,
                FilePath: match.FilePath,
                Kind: match.Kind,
                BaseScore: CandidateSourceWeights.Weight(source),
                Source: source,
                Reasons: [$"{source}: {result.Query} -> {match.DisplayName} ({match.Relation})"],
                TokenEstimate: 0));
        }
    }
}

/// <summary>
///     Generates candidates by matching the query against indexed file paths.
/// </summary>
public sealed class PathCandidateGenerator : ICandidateGenerator
{
    private readonly IWorkspaceIndexQueryStore _store;

    /// <summary>
    ///     Initializes a new instance of the <see cref="PathCandidateGenerator" /> class.
    /// </summary>
    /// <param name="store">The index store to query.</param>
    public PathCandidateGenerator(IWorkspaceIndexQueryStore store) => _store = store;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return [];

        var candidates = new List<CandidateNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Tokens(request.Query))
        {
            foreach (var file in await _store.FindFilesByPathAsync(token, request.MaxCandidates, cancellationToken))
            {
                if (!seen.Add(file.NormalizedPath))
                    continue;

                candidates.Add(new CandidateNode(
                    NodeId: string.Empty,
                    FilePath: file.NormalizedPath,
                    Kind: "file",
                    BaseScore: CandidateSourceWeights.Weight(CandidateSource.FtsPath),
                    Source: CandidateSource.FtsPath,
                    Reasons: [$"path match: {token}"],
                    TokenEstimate: 0));
            }
        }

        return candidates;
    }

    // Only path-like tokens (3+ chars, alphanumeric) are matched, so common short words do not match every file.
    private static IEnumerable<string> Tokens(string query) =>
        query.Split([' ', '\t', '\n', '.', '(', ')', ',', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3 && token.All(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
///     Generates must-keep candidates from explicitly selected paths and from files changed since a git base
///     ref (when a change source is available).
/// </summary>
/// <remarks>
///     Diff candidates are the strongest signal (weight 1.00) and act as must-keep seeds in context and review
///     planning. A change source failure (git unavailable, not a repository) is swallowed so localization still
///     produces other candidates; the engine surfaces the low-signal case separately.
/// </remarks>
public sealed class DiffCandidateGenerator : ICandidateGenerator
{
    private readonly IChangeSource? _changeSource;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiffCandidateGenerator" /> class.
    /// </summary>
    /// <param name="changeSource">An optional change source for resolving <c>ChangedSince</c>.</param>
    public DiffCandidateGenerator(IChangeSource? changeSource = null) => _changeSource = changeSource;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        if (request.SelectedPaths is { Count: > 0 } selected)
            paths.AddRange(selected.Where(p => !string.IsNullOrWhiteSpace(p)));

        if (!string.IsNullOrWhiteSpace(request.ChangedSince) && _changeSource is not null)
        {
            try
            {
                paths.AddRange(await _changeSource.GetChangedFilesAsync(request.RootDirectory, request.ChangedSince, cancellationToken));
            }
            catch (ChangeSourceException)
            {
                // Git unavailable or not a repository: skip diff candidates rather than failing the whole request.
            }
        }

        return paths
            .Select(p => p.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .Select(p => new CandidateNode(
                NodeId: string.Empty,
                FilePath: p,
                Kind: "file",
                BaseScore: CandidateSourceWeights.Weight(CandidateSource.DiffChangedFile),
                Source: CandidateSource.DiffChangedFile,
                Reasons: ["changed/selected file"],
                TokenEstimate: 0))
            .ToList();
    }
}
