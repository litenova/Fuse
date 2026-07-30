using Fuse.Indexing;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Runs syntax, semantic, and capture index stages through the repository write coordinator.
/// </summary>
public sealed class SemanticIndexJobExecutor : IWorkspaceIndexJobExecutor
{
    private const string JobIdMetaKey = "index_job_id";
    private const string JobPhaseMetaKey = "index_job_phase";
    private const string JobDepthMetaKey = "index_job_depth";
    private const string JobStartedMetaKey = "index_job_started_at";
    private const string JobStateMetaKey = "index_job_state";
    private const string JobErrorCodeMetaKey = "index_job_error_code";
    private const string JobErrorMessageMetaKey = "index_job_error_message";
    private readonly IndexCoordinator _coordinator;
    private readonly SemanticIndexer _indexer;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticIndexJobExecutor" /> class.
    /// </summary>
    /// <param name="coordinator">The root writer coordinator.</param>
    /// <param name="indexer">The syntax and compiler indexer.</param>
    public SemanticIndexJobExecutor(IndexCoordinator coordinator, SemanticIndexer indexer)
    {
        _coordinator = coordinator;
        _indexer = indexer;
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

    private static async Task MarkAsync(
        IWorkspaceIndexStore store,
        string jobId,
        IndexJobRequest request,
        IndexPhase phase,
        string state,
        CancellationToken cancellationToken,
        string? errorCode = null,
        string? errorMessage = null)
    {
        await store.SetMetaAsync(JobIdMetaKey, jobId, cancellationToken);
        await store.SetMetaAsync(JobPhaseMetaKey, phase.ToString(), cancellationToken);
        await store.SetMetaAsync(JobDepthMetaKey, request.Depth.ToString(), cancellationToken);
        await store.SetMetaAsync(JobStartedMetaKey, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
        await store.SetMetaAsync(JobStateMetaKey, state, cancellationToken);
        await store.SetMetaAsync(JobErrorCodeMetaKey, errorCode ?? string.Empty, cancellationToken);
        await store.SetMetaAsync(JobErrorMessageMetaKey, errorMessage ?? string.Empty, cancellationToken);
    }
}

/// <summary>
///     Identifies invalid job input that callers can correct without treating the index pipeline as a failure.
/// </summary>
public sealed class IndexJobValidationException : Exception
{
    /// <summary>Initializes a new validation exception.</summary>
    /// <param name="message">The direct correction message.</param>
    public IndexJobValidationException(string message) : base(message)
    {
    }
}
