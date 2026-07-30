using Fuse.Semantics;
using Fuse.Workspace;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Holds the host-owned services used by MCP tool handlers. A host builds one instance from dependency
///     injection, so daemon delegation and resident workspaces stay scoped to that host instead of changing
///     process-wide tool state.
/// </summary>
public sealed class FuseMcpRuntime
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseMcpRuntime" /> class.
    /// </summary>
    /// <param name="indexAccess">The local or daemon-backed index access path.</param>
    /// <param name="residentWorkspaces">The host-owned resident compiler workspace provider.</param>
    /// <param name="indexCoordinator">The process-owned coordinator for store-only operations.</param>
    /// <param name="indexJobs">The process-owned repository job manager.</param>
    /// <param name="warmSolutions">The process-owned held MSBuild solution cache.</param>
    /// <param name="pooledCheckWorkers">The process-owned compiler-check worker pool.</param>
    public FuseMcpRuntime(
        IIndexAccessProvider indexAccess,
        IResidentWorkspaceProvider residentWorkspaces,
        IndexCoordinator indexCoordinator,
        IWorkspaceIndexJobManager indexJobs,
        WarmSolutionCache warmSolutions,
        PooledCheckWorker pooledCheckWorkers)
    {
        IndexAccess = indexAccess;
        ResidentWorkspaces = residentWorkspaces;
        IndexCoordinator = indexCoordinator;
        IndexJobs = indexJobs;
        WarmSolutions = warmSolutions;
        PooledCheckWorkers = pooledCheckWorkers;
    }

    /// <summary>The local or remote index access path selected for this host.</summary>
    public IIndexAccessProvider IndexAccess { get; }

    /// <summary>The resident workspace provider selected for this host.</summary>
    public IResidentWorkspaceProvider ResidentWorkspaces { get; }

    /// <summary>The process-owned store coordinator for read-only and small scoped writes.</summary>
    public IndexCoordinator IndexCoordinator { get; }

    /// <summary>The repository-owned job manager used for progress and lifecycle state.</summary>
    public IWorkspaceIndexJobManager IndexJobs { get; }

    /// <summary>The process-owned held MSBuild solution cache.</summary>
    public WarmSolutionCache WarmSolutions { get; }

    /// <summary>The process-owned pool of compiler capture check workers.</summary>
    public PooledCheckWorker PooledCheckWorkers { get; }

    /// <summary>
    ///     Creates an isolated runtime for direct unit calls to a tool method. Production hosts must obtain the
    ///     runtime from dependency injection so their jobs and resident state share one lifetime.
    /// </summary>
    /// <param name="indexer">The indexer used by the isolated job executor.</param>
    /// <param name="residentWorkspaces">An optional test resident provider.</param>
    /// <returns>An isolated runtime with no process-wide mutable services.</returns>
    public static FuseMcpRuntime CreateIsolated(
        SemanticIndexer indexer,
        IResidentWorkspaceProvider? residentWorkspaces = null)
    {
        var coordinator = new IndexCoordinator();
        var jobs = new WorkspaceIndexJobManager(new SemanticIndexJobExecutor(coordinator, indexer));
        return new FuseMcpRuntime(
            new LocalIndexAccessProvider(coordinator, jobs),
            residentWorkspaces ?? NullResidentWorkspaceProvider.Instance,
            coordinator,
            jobs,
            new WarmSolutionCache(),
            new PooledCheckWorker());
    }
}
