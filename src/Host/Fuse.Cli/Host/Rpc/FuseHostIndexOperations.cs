using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using Microsoft.Extensions.Logging;

namespace Fuse.Cli.Rpc;

// Focused index lifecycle operations behind the JSON-RPC adapter.
internal sealed class FuseHostIndexOperations
{
    private readonly FuseHostService _host;

    internal FuseHostIndexOperations(FuseHostService host)
    {
        _host = host;
    }

    internal async Task<OpenIndexedResultDto> OpenIndexedAsync(string root)
    {
        if (!Directory.Exists(root))
            return new OpenIndexedResultDto("not_indexed", "workspace directory not found", 0, null);

        var job = _host.IndexJobs.GetStatus(root);
        try
        {
            await using var store = new WorkspaceIndexStore(FuseStorePaths.ResolveDatabasePath(root));
            if (await store.OpenForReadAsync(_host.LifetimeToken) is WorkspaceIndexReadOpenStatus.Ready)
            {
                var state = await store.GetStateAsync(_host.LifetimeToken);
                var manifest = await WorkspaceIndexManifest.ValidateAsync(root, store, _host.LifetimeToken);
                if (manifest.Ready && await _host.Indexer.IsInventoryCurrentAsync(root, store, _host.LifetimeToken))
                    return new OpenIndexedResultDto("ready", null, state.FileCount, state.Mode, job);
            }
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            _host.Logger.LogDebug(exception, "The index was not readable for {Root}.", root);
        }

        var started = await _host.IndexJobs.StartOrJoinAsync(
            new IndexJobRequest(root, IndexDepth.Syntax, Force: false, CaptureBundlePath: null),
            _host.LifetimeToken);
        return new OpenIndexedResultDto(
            "index_rebuilding",
            "syntax index is refreshing; call fuse/indexStatus for progress",
            0,
            null,
            started.Snapshot);
    }

    internal static IndexJobSnapshot MissingWorkspaceSnapshot(string root)
    {
        var now = DateTimeOffset.UtcNow;
        return new IndexJobSnapshot(
            JobId: string.Empty,
            Root: root,
            State: IndexJobState.Failed,
            Phase: IndexPhase.Inventory,
            PhaseNumber: 1,
            PhaseCount: 4,
            CompletedUnits: 0,
            TotalUnits: null,
            PhasePercent: null,
            EstimatedRemaining: null,
            CurrentItem: null,
            StartedAt: now,
            Elapsed: TimeSpan.Zero,
            Counts: IndexCountSnapshot.Empty,
            Storage: IndexStorageSnapshot.Empty,
            Warnings: [],
            ErrorCode: "workspace_not_found",
            ErrorMessage: $"Workspace directory does not exist: {root}");
    }

    internal static async Task<WorkspaceIndexStore> OpenStoreAsync(string root, CancellationToken cancellationToken)
    {
        var store = new WorkspaceIndexStore(FuseStorePaths.ResolveDatabasePath(root));
        await store.InitializeAsync(cancellationToken);
        return store;
    }
}
