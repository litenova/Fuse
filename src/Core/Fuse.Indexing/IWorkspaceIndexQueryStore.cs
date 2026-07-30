namespace Fuse.Indexing;

/// <summary>
///     Reads searchable source, symbols, file catalog data, and index summaries without graph mutation.
/// </summary>
public interface IWorkspaceIndexQueryStore
{
    /// <summary>Lists target-framework availability rows in deterministic order.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The canonical availability rows.</returns>
    Task<IReadOnlyList<TfmAvailabilityRecord>> GetTfmAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>Returns indexed file paths carrying a language tag.</summary>
    /// <param name="language">The language tag to match.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching normalized paths.</returns>
    Task<IReadOnlyList<string>> GetFilesByLanguageAsync(string language, CancellationToken cancellationToken);

    /// <summary>Returns the count of indexed routes.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The route count.</returns>
    Task<int> GetRouteCountAsync(CancellationToken cancellationToken);

    /// <summary>Returns indexed file counts grouped by language.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The language counts.</returns>
    Task<IReadOnlyList<LanguageCount>> GetLanguageCountsAsync(CancellationToken cancellationToken);

    /// <summary>Runs full-text search over indexed source chunks.</summary>
    /// <param name="query">The search query.</param>
    /// <param name="cancellationToken">A token to cancel the search.</param>
    /// <returns>Ranked source hits.</returns>
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);

    /// <summary>Lists indexed symbols for a workspace summary.</summary>
    /// <param name="limit">The maximum number of symbols to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The symbol summaries.</returns>
    Task<IReadOnlyList<SymbolListItem>> ListSymbolsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Lists indexed routes in deterministic order.</summary>
    /// <param name="limit">The maximum number of routes to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The route summaries.</returns>
    Task<IReadOnlyList<RouteListItem>> ListRoutesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Finds symbols whose simple names contain a fragment.</summary>
    /// <param name="nameFragment">The name fragment to match.</param>
    /// <param name="limit">The maximum number of matches.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching symbols.</returns>
    Task<IReadOnlyList<SymbolListItem>> FindSymbolsByNameAsync(string nameFragment, int limit, CancellationToken cancellationToken);

    /// <summary>Gets exact signature records for requested simple or qualified names.</summary>
    /// <param name="names">The names to look up.</param>
    /// <param name="limitPerName">The maximum matches for each name.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching signatures.</returns>
    Task<IReadOnlyList<SymbolSignature>> GetSignaturesByNamesAsync(
        IReadOnlyCollection<string> names, int limitPerName, CancellationToken cancellationToken);

    /// <summary>Gets member signatures belonging to a type.</summary>
    /// <param name="typeName">The simple or qualified declaring type name.</param>
    /// <param name="limit">The maximum members to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching member signatures.</returns>
    Task<IReadOnlyList<SymbolSignature>> GetMembersOfTypeAsync(
        string typeName, int limit, CancellationToken cancellationToken);

    /// <summary>Finds file catalog rows by normalized-path fragment.</summary>
    /// <param name="fragment">The path fragment to match.</param>
    /// <param name="limit">The maximum files to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching files.</returns>
    Task<IReadOnlyList<FileListItem>> FindFilesByPathAsync(string fragment, int limit, CancellationToken cancellationToken);

    /// <summary>Gets one file's summed reduced-token estimate.</summary>
    /// <param name="normalizedPath">The normalized file path.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The token estimate.</returns>
    Task<int> GetFileTokenEstimateAsync(string normalizedPath, CancellationToken cancellationToken);

    /// <summary>Gets reduced-token estimates for every indexed file.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>A map keyed by normalized file path.</returns>
    Task<IReadOnlyDictionary<string, int>> GetFileTokenEstimatesAsync(CancellationToken cancellationToken);

    /// <summary>Gets content hashes for a requested file subset.</summary>
    /// <param name="normalizedPaths">The normalized paths to look up.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The found path-to-hash map.</returns>
    Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(
        IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken);

    /// <summary>Gets content hashes for every indexed file.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The complete path-to-hash map.</returns>
    Task<IReadOnlyDictionary<string, string>> GetAllFileHashesAsync(CancellationToken cancellationToken);
}
