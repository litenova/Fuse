namespace Fuse.Indexing;

/// <summary>
///     Opens, initializes, resets, and describes the derived SQLite index.
/// </summary>
public interface IWorkspaceIndexLifecycleStore
{
    /// <summary>Opens the store for writing and rebuilds incompatible derived data when needed.</summary>
    /// <param name="cancellationToken">A token to cancel initialization.</param>
    /// <returns>The initialization outcome.</returns>
    Task<WorkspaceIndexInitializeOutcome> InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Discards every Fuse-owned index table and creates an empty current schema.</summary>
    /// <param name="cancellationToken">A token to cancel the reset.</param>
    /// <returns>A task that completes when the empty searchable schema is ready.</returns>
    Task ResetAsync(CancellationToken cancellationToken);

    /// <summary>Opens an existing compatible store for reads without mutating metadata.</summary>
    /// <param name="cancellationToken">A token to cancel the open.</param>
    /// <returns>The read-open outcome.</returns>
    Task<WorkspaceIndexReadOpenStatus> OpenForReadAsync(CancellationToken cancellationToken);

    /// <summary>Returns the schema version, status, and record counts currently stored.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The current store state.</returns>
    Task<WorkspaceIndexState> GetStateAsync(CancellationToken cancellationToken);
}
