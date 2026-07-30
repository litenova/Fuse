using System.Text;

namespace Fuse.Reduction.Caching;

/// <summary>
///     Reduction cache view over a bounded host-owned <see cref="IKeyValueStore" />.
/// </summary>
public sealed class MemoryReductionCache : IReductionCache
{
    private const string StoreName = "reduction";
    private readonly IKeyValueStore _store;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MemoryReductionCache" /> class.
    /// </summary>
    /// <param name="store">The repository-scoped memory cache view for this run.</param>
    public MemoryReductionCache(IKeyValueStore store) => _store = store;

    /// <inheritdoc />
    public ReductionCacheStatistics Statistics { get; } = new();

    /// <inheritdoc />
    public bool TryGet(ulong contentHash, ulong reductionOptionsHash, out string reducedContent)
    {
        if (_store.TryGet(StoreName, Key(contentHash, reductionOptionsHash), out var bytes) && bytes is not null)
        {
            reducedContent = Encoding.UTF8.GetString(bytes);
            Statistics.RecordHit();
            return true;
        }

        Statistics.RecordMiss();
        reducedContent = string.Empty;
        return false;
    }

    /// <inheritdoc />
    public void Set(ulong contentHash, ulong reductionOptionsHash, string reducedContent) =>
        _store.Set(StoreName, Key(contentHash, reductionOptionsHash), Encoding.UTF8.GetBytes(reducedContent));

    /// <inheritdoc />
    public void Clear() => _store.Clear(StoreName);

    private static string Key(ulong contentHash, ulong reductionOptionsHash) =>
        $"{contentHash:x16}{reductionOptionsHash:x16}";
}
