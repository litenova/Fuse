namespace Fuse.Indexing;

/// <summary>
///     Reads and writes bounded index metadata such as manifest, lifecycle, and diagnostic stamps.
/// </summary>
public interface IWorkspaceIndexMetadataStore
{
    /// <summary>Sets a metadata value.</summary>
    /// <param name="key">The metadata key.</param>
    /// <param name="value">The value to persist.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the value is committed.</returns>
    Task SetMetaAsync(string key, string value, CancellationToken cancellationToken);

    /// <summary>Gets a metadata value.</summary>
    /// <param name="key">The metadata key.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The stored value, or null when the key is absent.</returns>
    Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken);
}
