namespace Fuse.Reduction.Caching;

/// <summary>
///     Opens repository-scoped views of the daemon-owned derived-data memory cache.
/// </summary>
public interface IWorkspaceMemoryStoreFactory
{
    /// <summary>
    ///     Opens a cache view isolated to the repository that contains <paramref name="sourceDirectory" />.
    /// </summary>
    /// <param name="sourceDirectory">A source directory inside or outside a Git repository.</param>
    /// <returns>A process-lifetime cache view for that repository.</returns>
    /// <remarks>
    ///     The returned view owns no storage and disposing it does not clear shared cache entries. The host owns
    ///     the backing cache for its lifetime, so a daemon can reuse reductions across requests without creating
    ///     a second SQLite database.
    /// </remarks>
    IKeyValueStore Open(string sourceDirectory);
}
