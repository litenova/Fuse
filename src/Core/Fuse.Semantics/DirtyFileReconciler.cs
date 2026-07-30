using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     Reconciles a warm syntax index with the current file inventory without rebuilding unchanged rows.
/// </summary>
internal sealed class DirtyFileReconciler
{
    private const int MaxReconcileFiles = 300;
    private readonly WorkspaceInventoryPlanner _inventory;
    private readonly SyntaxIndexStage _syntaxStage;
    private readonly IndexFinalizer _finalizer;

    internal DirtyFileReconciler(
        WorkspaceInventoryPlanner inventory,
        SyntaxIndexStage syntaxStage,
        IndexFinalizer finalizer)
    {
        _inventory = inventory;
        _syntaxStage = syntaxStage;
        _finalizer = finalizer;
    }

    internal async Task<int> ReindexFileAsync(
        string root,
        string normalizedPath,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken)
    {
        var scan = await _inventory.ScanAsync(root, cancellationToken);
        var file = scan.Files.FirstOrDefault(candidate =>
            string.Equals(candidate.NormalizedPath, normalizedPath, StringComparison.Ordinal));
        if (file is null)
        {
            await store.DeleteFileAsync(normalizedPath, cancellationToken);
            return 0;
        }

        return await _syntaxStage.ReindexFileAsync(root, file, store, cancellationToken);
    }

    internal async Task<FreshnessResult> ReconcileAsync(
        string root,
        IWorkspaceIndexStore store,
        string staleAsOfMetaKey,
        CancellationToken cancellationToken)
    {
        var stored = await store.GetAllFileHashesAsync(cancellationToken);
        var scan = await _inventory.ScanAsync(root, cancellationToken);
        var current = scan.Files.ToDictionary(file => file.NormalizedPath, StringComparer.Ordinal);

        var changed = new List<IndexedFileRecord>();
        foreach (var (normalizedPath, file) in current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stored.TryGetValue(normalizedPath, out var storedHash)
                || !string.Equals(file.ContentHash, storedHash, StringComparison.Ordinal))
                changed.Add(file);
        }

        // A stored-only path is either deleted or no longer part of the scannable inventory. Delete its row even
        // when the file still exists under an excluded directory such as bin/ or obj/.
        var removedOrExcluded = stored.Keys.Where(path => !current.ContainsKey(path)).ToArray();
        var dirtyCount = changed.Count + removedOrExcluded.Length;

        if (dirtyCount == 0)
        {
            await FinalizeFreshAsync(root, store, scan, staleAsOfMetaKey, cancellationToken);
            return new FreshnessResult(current.Count, 0, 0, Stamped: false);
        }

        if (dirtyCount > MaxReconcileFiles)
        {
            await store.SetMetaAsync(
                staleAsOfMetaKey,
                dirtyCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken);
            return new FreshnessResult(current.Count, 0, dirtyCount, Stamped: true);
        }

        var reconciled = 0;
        foreach (var file in changed)
        {
            await _syntaxStage.ReindexFileAsync(root, file, store, cancellationToken);
            reconciled++;
        }

        foreach (var path in removedOrExcluded)
        {
            await store.DeleteFileAsync(path, cancellationToken);
            reconciled++;
        }

        await FinalizeFreshAsync(root, store, scan, staleAsOfMetaKey, cancellationToken);
        return new FreshnessResult(current.Count, reconciled, 0, Stamped: false);
    }

    private async Task FinalizeFreshAsync(
        string root,
        IWorkspaceIndexStore store,
        FileScanResult scan,
        string staleAsOfMetaKey,
        CancellationToken cancellationToken)
    {
        await store.SetMetaAsync(staleAsOfMetaKey, "0", cancellationToken);
        await _finalizer.StampSkippedFilesAsync(store, scan.Skipped, cancellationToken);
        await _finalizer.StampDetailLimitedFilesAsync(store, scan.DetailLimited, cancellationToken);
        await WorkspaceIndexManifest.CompleteAsync(root, store, scan.Files, cancellationToken);
    }
}
