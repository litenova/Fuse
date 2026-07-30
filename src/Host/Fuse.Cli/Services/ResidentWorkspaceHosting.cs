using Fuse.Cli.Mcp;
using Fuse.Workspace;

namespace Fuse.Cli.Services;

/// <summary>
///     Wires the resident workspace (S1) into a resident host process (<c>mcp serve</c> or <c>fuse host</c>): it
///     warms the host-owned <see cref="ResidentWorkspaceRegistry" /> for the served root in
///     the background so startup is never blocked by the build, and drives incremental updates from a file
///     watcher's coalesced batches. It is opt-in for now (the <c>FUSE_RESIDENT</c> flag), default off, so a host
///     that does not opt in behaves exactly as before; promotion to default-on is the S1 latency gate.
/// </summary>
public static class ResidentWorkspaceHosting
{
    // A change batch above this many files is treated as a bulk change that outran incremental update: the root is
    // evicted to store-backed (whose N6 reconcile handles staleness) rather than served stale.
    private const int StormThreshold = 300;

    /// <summary>Whether the resident workspace is opted in for this process (the <c>FUSE_RESIDENT</c> flag).</summary>
    /// <returns>True when <c>FUSE_RESIDENT</c> is set to a truthy value (1/true/yes/on).</returns>
    public static bool OptIn()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_RESIDENT");
        return value is not null
            && (value.Equals("1", StringComparison.Ordinal)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Enables the resident workspace for a root over an existing file watcher: warms the supplied host-owned
    ///     registry in the background and subscribes the watcher's batch to it.
    /// </summary>
    /// <param name="root">The absolute repository root the host serves.</param>
    /// <param name="watcher">The host's file watcher; its <see cref="IResidentBatchWatcher.BatchChanged" /> drives updates.</param>
    /// <param name="registry">The resident workspace registry owned by this host's dependency container.</param>
    /// <param name="indexJobs">The host-owned repository job manager used to refresh persisted syntax data.</param>
    /// <param name="log">A sink for non-fatal diagnostics (stderr), or null.</param>
    /// <param name="cancellationToken">The host's lifetime token.</param>
    /// <returns>
    ///     A disposable that, on host shutdown, disposes the registry (and its held workspaces) and restores the
    ///     default null provider. The caller owns the watcher's lifetime.
    /// </returns>
    public static IDisposable Enable(
        string root,
        IResidentBatchWatcher watcher,
        ResidentWorkspaceRegistry registry,
        IWorkspaceIndexJobManager indexJobs,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);

        _ = Task.Run(async () =>
        {
            try
            {
                if (!await registry.WarmAsync(fullRoot, cancellationToken))
                    log?.Invoke($"resident workspace: {fullRoot} did not build; serving store-backed.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke($"resident workspace warm failed: {ex.Message}");
            }
        }, cancellationToken);

        watcher.BatchChanged += async (batch, batchToken) =>
        {
            if (batch.Count > StormThreshold)
            {
                registry.Evict(fullRoot); // A bulk change outran incremental update; fall back to store-backed.
                return;
            }

            var result = registry.ApplyBatch(fullRoot, batch, batchToken);
            if (result is null || result.Applied + result.Added + result.Removed == 0)
                return;

            // Persisted syntax data must use the same job owner as CLI and MCP indexing. The resident workspace
            // remains current for resident-grade reads while the syntax refresh runs for store-backed readers.
            try
            {
                await indexJobs.StartOrJoinAsync(
                    new IndexJobRequest(fullRoot, IndexDepth.Syntax, Force: false, CaptureBundlePath: null),
                    batchToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke($"resident syntax refresh request failed: {ex.Message}");
            }
        };

        return new ResidentScope(registry);
    }

    // Disposes the registry's held workspaces on host shutdown. The watcher is owned by the caller and disposed
    // there.
    private sealed class ResidentScope(ResidentWorkspaceRegistry registry) : IDisposable
    {
        public void Dispose()
        {
            registry.Dispose();
        }
    }
}
