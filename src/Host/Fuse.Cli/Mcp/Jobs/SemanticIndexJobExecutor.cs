using Fuse.Indexing;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Runs syntax, semantic, and capture index stages through the repository write coordinator.
/// </summary>
public sealed class SemanticIndexJobExecutor : IWorkspaceIndexJobExecutor
{
    private readonly IndexCoordinator _coordinator;
    private readonly SemanticIndexer _indexer;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticIndexJobExecutor" /> class.
    /// </summary>
    /// <param name="coordinator">The root writer coordinator.</param>
    /// <param name="indexer">The syntax and compiler indexer.</param>
    /// <param name="timeProvider">The time source used for persisted job timestamps.</param>
    public SemanticIndexJobExecutor(
        IndexCoordinator coordinator,
        SemanticIndexer indexer,
        TimeProvider? timeProvider = null)
    {
        _coordinator = coordinator;
        _indexer = indexer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<SemanticIndexResult> ExecuteAsync(
        string jobId,
        IndexJobRequest request,
        IProgress<IndexJobProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new IndexJobProgress(IndexPhase.Inventory, CurrentItem: "opening index"));
        return await _coordinator.OpenForWriteAsync(
            request.Root,
            async (store, ct) => await ExecuteWithStoreAsync(store, jobId, request, progress, ct),
            cancellationToken);
    }

    private async Task<SemanticIndexResult> ExecuteWithStoreAsync(
        WorkspaceIndexStore store,
        string jobId,
        IndexJobRequest request,
        IProgress<IndexJobProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await MarkInterruptedRunAsync(store, jobId, progress, cancellationToken);
            await MarkAsync(store, jobId, request, IndexPhase.Inventory, "running", cancellationToken);
            if (request.Force)
            {
                progress.Report(new IndexJobProgress(IndexPhase.Inventory, CurrentItem: "discarding derived index"));
                await store.ResetAsync(cancellationToken);
            }

            SemanticIndexResult result;
            if (request.CaptureBundlePath is not null)
            {
                progress.Report(new IndexJobProgress(IndexPhase.SemanticPreparation, CurrentItem: "reading capture bundle"));
                await MarkAsync(store, jobId, request, IndexPhase.SemanticPreparation, "running", cancellationToken);
                var capture = ReadCapture(request.CaptureBundlePath);
                progress.Report(new IndexJobProgress(IndexPhase.SemanticExtraction, CurrentItem: "rehydrating capture graph"));
                await MarkAsync(store, jobId, request, IndexPhase.SemanticExtraction, "running", cancellationToken);
                result = await _indexer.IndexFromCaptureGraphAsync(
                    request.Root, store, capture, cancellationToken, request.CaptureBundlePath);
                progress.Report(new IndexJobProgress(IndexPhase.SemanticPersistence, CompletedUnits: 1, TotalUnits: 1));
            }
            else if (request.Depth == IndexDepth.Semantic)
            {
                progress.Report(new IndexJobProgress(IndexPhase.SemanticPreparation, CurrentItem: "resolving compiler workspace"));
                await MarkAsync(store, jobId, request, IndexPhase.SemanticPreparation, "running", cancellationToken);
                // Syntax rows stay readable while compiler analysis is active. The flag describes live work only;
                // normal syntax indexing is a completed depth and leaves it clear.
                await store.SetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, "1", cancellationToken);
                result = await _indexer.UpgradeToSemanticAsync(
                    request.Root,
                    store,
                    cancellationToken,
                    ToJobProgress(progress));
                progress.Report(new IndexJobProgress(IndexPhase.SemanticPersistence, CompletedUnits: 1, TotalUnits: 1));
            }
            else
            {
                result = await _indexer.IndexSyntaxFirstAsync(
                    request.Root,
                    store,
                    cancellationToken,
                    ToJobProgress(progress));
                progress.Report(new IndexJobProgress(
                    IndexPhase.SyntaxPersistence,
                    CompletedUnits: Math.Max(1, result.FileCount),
                    TotalUnits: Math.Max(1, result.FileCount),
                    CurrentItem: "syntax index ready"));
            }

            await MarkAsync(store, jobId, request, IndexPhase.Finalization, "completed", cancellationToken);
            await MaintainAsync(
                store,
                completed: true,
                progress: progress,
                reportingPhase: request.Depth == IndexDepth.Semantic
                    ? IndexPhase.SemanticPersistence
                    : IndexPhase.SyntaxPersistence,
                cancellationToken: cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using var terminalWrite = new CancellationTokenSource();
            await MarkAsync(store, jobId, request, IndexPhase.Finalization, "cancelled", terminalWrite.Token);
            await store.SetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, "0", terminalWrite.Token);
            await MaintainAsync(
                store,
                completed: false,
                progress: progress,
                reportingPhase: request.Depth == IndexDepth.Semantic
                    ? IndexPhase.SemanticPersistence
                    : IndexPhase.SyntaxPersistence,
                cancellationToken: terminalWrite.Token);
            throw;
        }
        catch (IndexJobValidationException ex)
        {
            using var terminalWrite = new CancellationTokenSource();
            await MarkAsync(
                store,
                jobId,
                request,
                IndexPhase.Finalization,
                "failed",
                terminalWrite.Token,
                "index_invalid_request",
                ex.Message);
            await store.SetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, "0", terminalWrite.Token);
            throw;
        }
        catch
        {
            using var terminalWrite = new CancellationTokenSource();
            await MarkAsync(store, jobId, request, IndexPhase.Finalization, "failed", terminalWrite.Token, "index_failed", "Index job failed. Run 'fuse index status' for details.");
            await store.SetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, "0", terminalWrite.Token);
            throw;
        }
    }

    private static async Task MaintainAsync(
        WorkspaceIndexStore store,
        bool completed,
        IProgress<IndexJobProgress> progress,
        IndexPhase reportingPhase,
        CancellationToken cancellationToken)
    {
        try
        {
            var replacedRowsRaw = await store.GetMetaAsync("last_index_fts_replacements", cancellationToken);
            var replacedRows = int.TryParse(replacedRowsRaw, out var parsed) ? Math.Max(0, parsed) : 0;
            var maintenance = await store.MaintainAfterIndexAsync(completed, replacedRows, cancellationToken);
            if (maintenance.FullTextOptimized || maintenance.FullTextMerged || maintenance.VacuumedPages > 0)
            {
                progress.Report(new IndexJobProgress(
                    reportingPhase,
                    CurrentItem: "maintaining SQLite index"));
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            progress.Report(new IndexJobProgress(
                reportingPhase,
                Warning: $"database maintenance skipped: {ex.SqliteErrorCode}"));
        }
        catch (IOException)
        {
            progress.Report(new IndexJobProgress(
                reportingPhase,
                Warning: "database maintenance skipped: I/O error"));
        }
    }

    private static CaptureResult ReadCapture(string bundlePath)
    {
        var manifest = CaptureBundleIo.ReadManifest(bundlePath);
        if (manifest is null)
            throw new IndexJobValidationException($"No capture bundle exists at {bundlePath}.");
        if (!manifest.IsCompatibleWithRunningBuild)
            throw new IndexJobValidationException($"Capture bundle is incompatible: {manifest.IncompatibilityReason}");
        var graph = CaptureBundleIo.ReadGraph(bundlePath);
        if (graph is null || !graph.Succeeded)
            throw new IndexJobValidationException($"Capture bundle has no readable extracted graph at {bundlePath}.");
        return graph;
    }

    private static IProgress<SemanticIndexProgress> ToJobProgress(IProgress<IndexJobProgress> progress) =>
        new InlineProgress<SemanticIndexProgress>(update => progress.Report(new IndexJobProgress(
            update.Stage switch
            {
                SemanticIndexStage.Inventory => IndexPhase.Inventory,
                SemanticIndexStage.SyntaxExtraction => IndexPhase.SyntaxExtraction,
                SemanticIndexStage.SyntaxPersistence => IndexPhase.SyntaxPersistence,
                SemanticIndexStage.SemanticPreparation => IndexPhase.SemanticPreparation,
                SemanticIndexStage.SemanticExtraction => IndexPhase.SemanticExtraction,
                SemanticIndexStage.SemanticPersistence => IndexPhase.SemanticPersistence,
                SemanticIndexStage.Finalization => IndexPhase.Finalization,
                _ => throw new ArgumentOutOfRangeException(nameof(update), update.Stage, "unknown semantic index stage"),
            },
            update.CompletedUnits,
            update.TotalUnits,
            update.CurrentItem)));

    private async Task MarkInterruptedRunAsync(
        IWorkspaceIndexStore store,
        string jobId,
        IProgress<IndexJobProgress> progress,
        CancellationToken cancellationToken)
    {
        var state = await store.GetMetaAsync(IndexJobMetaKeys.State, cancellationToken);
        if (!string.Equals(state, "running", StringComparison.Ordinal)
            && !string.Equals(state, "cancelling", StringComparison.Ordinal))
        {
            return;
        }

        var previousJobId = await store.GetMetaAsync(IndexJobMetaKeys.JobId, cancellationToken);
        if (string.IsNullOrWhiteSpace(previousJobId)
            || string.Equals(previousJobId, jobId, StringComparison.Ordinal))
        {
            return;
        }

        var previousPhase = await store.GetMetaAsync(IndexJobMetaKeys.Phase, cancellationToken) ?? IndexPhase.Inventory.ToString();
        var previousDepth = await store.GetMetaAsync(IndexJobMetaKeys.Depth, cancellationToken) ?? IndexDepth.Syntax.ToString();
        var previousStarted = await store.GetMetaAsync(IndexJobMetaKeys.StartedAt, cancellationToken) ?? string.Empty;
        await store.SetMetaAsync(IndexJobMetaKeys.InterruptedJobId, previousJobId, cancellationToken);
        await store.SetMetaAsync(IndexJobMetaKeys.InterruptedPhase, previousPhase, cancellationToken);
        await store.SetMetaAsync(IndexJobMetaKeys.InterruptedDepth, previousDepth, cancellationToken);
        await store.SetMetaAsync(IndexJobMetaKeys.InterruptedStartedAt, previousStarted, cancellationToken);
        await store.SetMetaAsync(IndexJobMetaKeys.InterruptedState, "interrupted", cancellationToken);
        progress.Report(new IndexJobProgress(
            IndexPhase.Inventory,
            CurrentItem: "resuming committed index batches",
            Warning: $"previous index job {previousJobId} was interrupted during {previousPhase}; resuming committed batches"));
    }

    private async Task MarkAsync(
        IWorkspaceIndexStore store,
        string jobId,
        IndexJobRequest request,
        IndexPhase phase,
        string state,
        CancellationToken cancellationToken,
        string? errorCode = null,
        string? errorMessage = null)
    {
        var existingJobId = await store.GetMetaAsync(IndexJobMetaKeys.JobId, cancellationToken);
        var startedAt = string.Equals(existingJobId, jobId, StringComparison.Ordinal)
            ? await store.GetMetaAsync(IndexJobMetaKeys.StartedAt, cancellationToken)
            : null;
        await store.SetMetaAsync(IndexJobMetaKeys.JobId, jobId, cancellationToken);
        await store.SetMetaAsync(IndexJobMetaKeys.Phase, phase.ToString(), cancellationToken);
        await store.SetMetaAsync(IndexJobMetaKeys.Depth, request.Depth.ToString(), cancellationToken);
        await store.SetMetaAsync(
            IndexJobMetaKeys.StartedAt,
            string.IsNullOrWhiteSpace(startedAt) ? _timeProvider.GetUtcNow().ToString("O") : startedAt,
            cancellationToken);
        await store.SetMetaAsync(IndexJobMetaKeys.State, state, cancellationToken);
        await store.SetMetaAsync(IndexJobMetaKeys.ErrorCode, errorCode ?? string.Empty, cancellationToken);
        await store.SetMetaAsync(IndexJobMetaKeys.ErrorMessage, errorMessage ?? string.Empty, cancellationToken);
    }
}
