namespace Fuse.Indexing;

/// <summary>
///     Persists file catalog, project, symbol, chunk, and target-framework rows for an index pass.
/// </summary>
public interface IWorkspaceIndexWriteStore
{
    /// <summary>Inserts or updates file catalog rows.</summary>
    /// <param name="files">The file records to persist.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertFilesAsync(IReadOnlyList<IndexedFileRecord> files, CancellationToken cancellationToken);

    /// <summary>Inserts or updates project records.</summary>
    /// <param name="projects">The project records to persist.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertProjectsAsync(IReadOnlyList<ProjectRecord> projects, CancellationToken cancellationToken);

    /// <summary>Replaces the target-framework availability union.</summary>
    /// <param name="availability">The complete target-framework availability rows.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the replacement is committed.</returns>
    Task ReplaceTfmAvailabilityAsync(IReadOnlyList<TfmAvailabilityRecord> availability, CancellationToken cancellationToken);

    /// <summary>Inserts or updates symbol records.</summary>
    /// <param name="symbols">The symbols to persist.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken cancellationToken);

    /// <summary>Inserts or updates source chunk records.</summary>
    /// <param name="chunks">The chunks to persist.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken);

    /// <summary>Deletes derived rows for one file while preserving its file catalog row.</summary>
    /// <param name="normalizedPath">The normalized path to clear.</param>
    /// <param name="cancellationToken">A token to cancel the delete.</param>
    /// <returns>A task that completes when the delete is committed.</returns>
    Task DeleteFileDataAsync(string normalizedPath, CancellationToken cancellationToken);

    /// <summary>Clears derived rows for a batch of files while preserving their file catalog rows.</summary>
    /// <param name="normalizedPaths">The normalized paths to clear.</param>
    /// <param name="cancellationToken">A token to cancel the delete.</param>
    /// <returns>A task that completes when the delete is committed.</returns>
    Task ClearFileDataAsync(IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken);

    /// <summary>Deletes one file catalog row and all of its derived index data.</summary>
    /// <param name="normalizedPath">The normalized path to delete.</param>
    /// <param name="cancellationToken">A token to cancel the delete.</param>
    /// <returns>A task that completes when the delete is committed.</returns>
    Task DeleteFileAsync(string normalizedPath, CancellationToken cancellationToken);

    /// <summary>Deletes file catalog rows absent from a complete current inventory.</summary>
    /// <param name="normalizedPaths">The complete current normalized path set.</param>
    /// <param name="cancellationToken">A token to cancel the prune.</param>
    /// <returns>The number of removed rows.</returns>
    Task<int> PruneFilesAsync(IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken);
}
