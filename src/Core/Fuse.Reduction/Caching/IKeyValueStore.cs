namespace Fuse.Reduction.Caching;

/// <summary>
///     Repository-scoped in-memory key-value storage for derived cache data.
/// </summary>
/// <remarks>
///     Implementations must be safe for concurrent readers and writers. <see cref="FlushAsync" /> is retained as
///     a no-op lifecycle boundary for cache consumers; derived values are never written to disk.
/// </remarks>
public interface IKeyValueStore : IAsyncDisposable
{
    /// <summary>
    ///     Reads a value for the namespaced key, consulting buffered writes first.
    /// </summary>
    /// <param name="store">The logical store namespace.</param>
    /// <param name="key">The entry key within <paramref name="store" />.</param>
    /// <param name="value">The value when found; otherwise <see langword="null" />.</param>
    /// <returns><see langword="true" /> when an entry exists for the key.</returns>
    bool TryGet(string store, string key, out byte[]? value);

    /// <summary>
    ///     Stores a value in the process-lifetime cache.
    /// </summary>
    /// <param name="store">The logical store namespace.</param>
    /// <param name="key">The entry key within <paramref name="store" />.</param>
    /// <param name="value">The value to store.</param>
    void Set(string store, string key, byte[] value);

    /// <summary>
    ///     Completes a cache lifecycle boundary without writing data to disk.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the flush.</param>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes every entry under a logical store namespace.
    /// </summary>
    /// <param name="store">The logical store namespace to clear.</param>
    void Clear(string store);
}
