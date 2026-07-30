using Fuse.Indexing;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The in-process index access path: one writer queue per root plus the cross-process writer mutex (R14).
///     Used by daemon-less CLI, <c>FUSE_DAEMON=0</c> serve, and as the fallback when no daemon answers.
/// </summary>
public sealed class LocalIndexAccessProvider : IIndexAccessProvider
{
    /// <summary>The shared local provider used when MCP is not delegating to a daemon.</summary>
    public static LocalIndexAccessProvider Instance { get; } = new();

    /// <inheritdoc />
    public Task<WorkspaceIndexStore> OpenIndexedAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken) =>
        IndexCoordinator.Default.OpenIndexedAsync(
            indexer,
            path,
            FuseTools.BackgroundSemanticUpgradeEnabled,
            FuseTools.UpgradeSupervisor,
            FuseTools.ScheduleSemanticUpgrade,
            residentRoot => FuseTools.ResidentWorkspaces.DescribeResident(residentRoot) is not null,
            cancellationToken);

    /// <inheritdoc />
    public Task<SemanticIndexResult> IndexAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        return IndexCoordinator.Default.OpenForWriteAsync(
            root,
            (writeStore, ct) => indexer.IndexSyntaxFirstAsync(root, writeStore, ct),
            cancellationToken);
    }
}
