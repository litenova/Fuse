using System.Collections.Concurrent;
using Fuse.Reduction.Caching;

namespace Fuse.Fusion.Storage;

/// <summary>
///     Run-local decoded cache fronting a namespaced host-memory <see cref="IKeyValueStore" />.
/// </summary>
/// <typeparam name="TKey">The logical key type.</typeparam>
/// <typeparam name="TValue">The cached value type.</typeparam>
internal sealed class NamespacedKvCache<TKey, TValue> where TKey : notnull
{
    private readonly IKeyValueStore _store;
    private readonly string _storeName;
    private readonly Func<TKey, string> _keyString;
    private readonly Func<TValue, byte[]> _encode;
    private readonly Func<byte[], TValue?> _decode;
    private readonly ConcurrentDictionary<TKey, TValue> _memory;

    /// <summary>
    ///     Initializes a new instance of the <see cref="NamespacedKvCache{TKey, TValue}" /> class.
    /// </summary>
    /// <param name="store">The shared key-value store for the run.</param>
    /// <param name="storeName">The logical store namespace.</param>
    /// <param name="keyString">Maps a logical key to its persisted string form.</param>
    /// <param name="encode">Encodes a value for persistence.</param>
    /// <param name="decode">Decodes persisted bytes; returns <see langword="null" /> on a miss or malformed entry.</param>
    /// <param name="comparer">Optional key comparer for the in-memory map.</param>
    internal NamespacedKvCache(
        IKeyValueStore store,
        string storeName,
        Func<TKey, string> keyString,
        Func<TValue, byte[]> encode,
        Func<byte[], TValue?> decode,
        IEqualityComparer<TKey>? comparer = null)
    {
        _store = store;
        _storeName = storeName;
        _keyString = keyString;
        _encode = encode;
        _decode = decode;
        _memory = new ConcurrentDictionary<TKey, TValue>(comparer ?? EqualityComparer<TKey>.Default);
    }

    /// <summary>
    ///     Reads a cached value, consulting the run-local decoded map first then the backing store.
    /// </summary>
    /// <param name="key">The logical key.</param>
    /// <param name="value">The value when found; otherwise the default for <typeparamref name="TValue" />.</param>
    /// <returns><see langword="true" /> when an entry exists and decodes successfully.</returns>
    internal bool TryGet(TKey key, out TValue? value)
    {
        if (_memory.TryGetValue(key, out var cached))
        {
            value = cached;
            return true;
        }

        if (_store.TryGet(_storeName, _keyString(key), out var bytes) && bytes is not null)
        {
            var decoded = _decode(bytes);
            if (decoded is not null)
            {
                _memory[key] = decoded;
                value = decoded;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    ///     Stores a value in the decoded map and the host memory cache.
    /// </summary>
    /// <param name="key">The logical key.</param>
    /// <param name="value">The value to store.</param>
    internal void Set(TKey key, TValue value)
    {
        _memory[key] = value;
        _store.Set(_storeName, _keyString(key), _encode(value));
    }
}
