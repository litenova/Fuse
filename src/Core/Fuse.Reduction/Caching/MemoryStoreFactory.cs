using System.Security.Cryptography;
using System.Text;
using Fuse.Collection.FileSystem;

namespace Fuse.Reduction.Caching;

/// <summary>
///     Creates repository-scoped views over one bounded, process-lifetime derived-data cache.
/// </summary>
/// <remarks>
///     Register this type as a singleton in the host. Its 64 MiB default applies to encoded cache entries across
///     every repository served by that host; least-recently-used entries leave memory first when the limit is
///     reached. No cache database or sidecar file is created.
/// </remarks>
public sealed class MemoryStoreFactory : IWorkspaceMemoryStoreFactory
{
    /// <summary>The maximum encoded-entry bytes retained by the default host cache.</summary>
    public const long DefaultCapacityBytes = 64L * 1024 * 1024;

    private readonly BoundedMemoryStore _store;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MemoryStoreFactory" /> class.
    /// </summary>
    /// <param name="capacityBytes">The maximum encoded-entry bytes retained by the shared cache.</param>
    public MemoryStoreFactory(long capacityBytes = DefaultCapacityBytes) => _store = new BoundedMemoryStore(capacityBytes);

    /// <summary>The maximum encoded-entry bytes this factory retains.</summary>
    public long CapacityBytes => _store.CapacityBytes;

    /// <summary>The encoded-entry bytes currently retained across every repository scope.</summary>
    public long RetainedBytes => _store.RetainedBytes;

    /// <inheritdoc />
    public IKeyValueStore Open(string sourceDirectory) => new MemoryKeyValueStore(_store, WorkspaceScope(sourceDirectory));

    private static string WorkspaceScope(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        var root = RepositoryRootResolver.TryFindRepositoryRoot(sourceDirectory)
            ?? Path.GetFullPath(sourceDirectory);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
