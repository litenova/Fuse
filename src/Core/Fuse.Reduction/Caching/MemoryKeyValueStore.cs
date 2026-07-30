namespace Fuse.Reduction.Caching;

/// <summary>
///     A thread-safe, least-recently-used in-memory cache view for derived reduction and analysis data.
/// </summary>
/// <remarks>
///     A standalone instance owns its own bounded cache. Views created by <see cref="MemoryStoreFactory" /> share
///     one host-owned backing cache while applying a repository scope, so one workspace cannot read another
///     workspace's derived values.
/// </remarks>
public sealed class MemoryKeyValueStore : IKeyValueStore
{
    private readonly BoundedMemoryStore _store;
    private readonly string _scope;

    /// <summary>
    ///     Initializes an isolated in-memory cache with the given encoded-entry capacity.
    /// </summary>
    /// <param name="capacityBytes">The maximum encoded-entry bytes retained by this cache.</param>
    public MemoryKeyValueStore(long capacityBytes = MemoryStoreFactory.DefaultCapacityBytes)
        : this(new BoundedMemoryStore(capacityBytes), string.Empty)
    {
    }

    internal MemoryKeyValueStore(BoundedMemoryStore store, string scope)
    {
        _store = store;
        _scope = scope;
    }

    /// <summary>The maximum encoded-entry bytes retained by the backing cache.</summary>
    public long CapacityBytes => _store.CapacityBytes;

    /// <summary>The encoded-entry bytes currently retained by the backing cache.</summary>
    public long RetainedBytes => _store.RetainedBytes;

    /// <inheritdoc />
    public bool TryGet(string store, string key, out byte[]? value) => _store.TryGet(_scope, store, key, out value);

    /// <inheritdoc />
    public void Set(string store, string key, byte[] value) => _store.Set(_scope, store, key, value);

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Clear(string store) => _store.Clear(_scope, store);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class BoundedMemoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, Entry> _entries = [];
    private readonly LinkedList<CacheKey> _recency = [];
    private long _retainedBytes;

    internal BoundedMemoryStore(long capacityBytes)
    {
        if (capacityBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Cache capacity must be positive.");
        CapacityBytes = capacityBytes;
    }

    internal long CapacityBytes { get; }

    internal long RetainedBytes
    {
        get
        {
            lock (_gate)
                return _retainedBytes;
        }
    }

    internal bool TryGet(string scope, string store, string key, out byte[]? value)
    {
        var cacheKey = new CacheKey(scope, store, key);
        lock (_gate)
        {
            if (!_entries.TryGetValue(cacheKey, out var entry))
            {
                value = null;
                return false;
            }

            _recency.Remove(entry.RecencyNode);
            _recency.AddFirst(entry.RecencyNode);
            value = entry.Value.ToArray();
            return true;
        }
    }

    internal void Set(string scope, string store, string key, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var cacheKey = new CacheKey(scope, store, key);
        var copy = value.ToArray();
        lock (_gate)
        {
            Remove(cacheKey);
            if (copy.LongLength > CapacityBytes)
                return;

            var node = _recency.AddFirst(cacheKey);
            _entries.Add(cacheKey, new Entry(copy, node));
            _retainedBytes += copy.LongLength;
            while (_retainedBytes > CapacityBytes && _recency.Last is { } oldest)
                Remove(oldest.Value);
        }
    }

    internal void Clear(string scope, string store)
    {
        lock (_gate)
        {
            var keys = _entries.Keys
                .Where(key => string.Equals(key.Scope, scope, StringComparison.Ordinal)
                    && string.Equals(key.Store, store, StringComparison.Ordinal))
                .ToArray();
            foreach (var key in keys)
                Remove(key);
        }
    }

    private void Remove(CacheKey key)
    {
        if (!_entries.Remove(key, out var existing))
            return;

        _recency.Remove(existing.RecencyNode);
        _retainedBytes -= existing.Value.LongLength;
    }

    private readonly record struct CacheKey(string Scope, string Store, string Key);

    private sealed record Entry(byte[] Value, LinkedListNode<CacheKey> RecencyNode);
}
