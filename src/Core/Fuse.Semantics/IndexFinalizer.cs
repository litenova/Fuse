using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     Writes bounded index completion metadata after a syntax, semantic, or reconciliation stage commits its
///     rows. Finalization failures are intentionally best effort and never invalidate committed source data.
/// </summary>
internal sealed class IndexFinalizer
{
    internal async Task StampLoadDiagnosisAsync(
        IWorkspaceIndexMetadataStore store,
        LoadDiagnosis diagnosis,
        CancellationToken cancellationToken)
    {
        try
        {
            var persisted = new PersistedLoadDiagnosis(
                diagnosis.Tier,
                diagnosis.ProjectsLoaded,
                diagnosis.ProjectsTotal,
                diagnosis.Projects.Select(project => new PersistedProjectReport(
                    project.Name,
                    project.FilePath,
                    project.Loaded,
                    project.Reason)).ToList(),
                diagnosis.SelectedSolution,
                diagnosis.SelectionNote);
            var json = System.Text.Json.JsonSerializer.Serialize(
                persisted,
                PersistedLoadDiagnosisJsonContext.Default.PersistedLoadDiagnosis);
            await store.SetMetaAsync(WorkspaceIndexStore.LoadDiagnosisMetaKey, json, cancellationToken);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or System.Text.Json.JsonException)
        {
        }
    }

    internal async Task StampIntegrityAsync(
        IWorkspaceIndexLifecycleStore lifecycleStore,
        IWorkspaceIndexMetadataStore metadataStore,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await lifecycleStore.GetStateAsync(cancellationToken);
            await metadataStore.SetMetaAsync(
                WorkspaceIndexStore.IndexIntegrityMetaKey,
                IndexIntegrity.Check(state).Summary(),
                cancellationToken);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
        }
    }

    internal Task StampSkippedFilesAsync(
        IWorkspaceIndexMetadataStore store,
        IReadOnlyList<SkippedFile> skipped,
        CancellationToken cancellationToken)
    {
        const int maxListed = 20;
        var summary = skipped.Count == 0
            ? "0"
            : $"{skipped.Count}: " + string.Join("; ", skipped.Take(maxListed).Select(file => $"{file.Path} ({file.Reason})"))
              + (skipped.Count > maxListed ? $"; and {skipped.Count - maxListed} more" : string.Empty);
        return store.SetMetaAsync(WorkspaceIndexStore.SkippedFilesMetaKey, summary, cancellationToken);
    }

    internal Task StampDetailLimitedFilesAsync(
        IWorkspaceIndexMetadataStore store,
        IReadOnlyList<DetailLimitedFile> detailLimited,
        CancellationToken cancellationToken)
    {
        const int maxListed = 20;
        var summary = detailLimited.Count == 0
            ? "0"
            : $"{detailLimited.Count}: " + string.Join(
                "; ",
                detailLimited.Take(maxListed).Select(file => $"{file.Path} ({ToDetailValue(file.DetailLevel)}: {file.Reason})"))
              + (detailLimited.Count > maxListed ? $"; and {detailLimited.Count - maxListed} more" : string.Empty);
        return store.SetMetaAsync(WorkspaceIndexStore.DetailLimitedFilesMetaKey, summary, cancellationToken);
    }

    private static string ToDetailValue(IndexDetailLevel detailLevel) => detailLevel switch
    {
        IndexDetailLevel.Full => "full",
        IndexDetailLevel.Declarations => "declarations",
        IndexDetailLevel.InventoryOnly => "inventory_only",
        _ => throw new ArgumentOutOfRangeException(nameof(detailLevel), detailLevel, "unknown index detail level"),
    };
}
