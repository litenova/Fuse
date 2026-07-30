using Fuse.Fusion.Storage;
using Fuse.Reduction.Caching;

namespace Fuse.Fusion.Indexing;

/// <summary>
///     Host-memory <see cref="IAnalysisIndex" /> using the <c>analysis</c> store namespace, fronted by a
///     run-local decoded map so repeated lookups do not decode the same bytes twice.
/// </summary>
/// <remarks>
///     The encoded format is three tab-separated lines (referenced types, declared types, declared symbols).
///     Symbol names contain neither tabs nor newlines, so the format needs no escaping. Malformed entries are
///     treated as misses rather than throwing.
/// </remarks>
public sealed class MemoryAnalysisIndex : IAnalysisIndex
{
    private const string StoreName = "analysis";

    private readonly NamespacedKvCache<string, FileAnalysis> _cache;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MemoryAnalysisIndex" /> class.
    /// </summary>
    /// <param name="store">The repository-scoped host memory cache view.</param>
    public MemoryAnalysisIndex(IKeyValueStore store) =>
        _cache = new NamespacedKvCache<string, FileAnalysis>(
            store,
            StoreName,
            static key => key,
            NamespacedKvCodec.EncodeFileAnalysis,
            NamespacedKvCodec.DecodeFileAnalysis,
            StringComparer.Ordinal);

    /// <inheritdoc />
    public AnalysisIndexStatistics Statistics { get; } = new();

    /// <inheritdoc />
    public bool TryGet(string key, out FileAnalysis? analysis)
    {
        if (_cache.TryGet(key, out var cached))
        {
            Statistics.RecordHit();
            analysis = cached;
            return true;
        }

        Statistics.RecordMiss();
        analysis = null;
        return false;
    }

    /// <inheritdoc />
    public void Set(string key, FileAnalysis analysis) => _cache.Set(key, analysis);
}
