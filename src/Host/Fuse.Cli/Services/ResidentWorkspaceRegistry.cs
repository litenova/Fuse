using System.Diagnostics;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Workspace;

namespace Fuse.Cli.Services;

/// <summary>
///     The process-wide resident-workspace provider (S1): lazily builds and caches a
///     <see cref="ResidentWorkspaceService" /> per repository root, so the serve/host process can answer
///     resident-grade reads and apply watcher batches for each root it serves. It is the non-null provider the
///     serve/host registers it through dependency injection; the serve/host is responsible only for warming a
///     root (<see cref="WarmAsync" />) and feeding it watcher batches (<see cref="ApplyBatch" />).
/// </summary>
/// <remarks>
///     Warming a root runs the repository build once with a binary log and rehydrates the compilations from it,
///     so it is expensive and explicit; reads never trigger a build (an unwarmed root reports store-backed). The
///     registry keeps each warmed root's binlog on disk for the workspace's lifetime (the rehydrated compilation
///     reads it lazily) and deletes it on <see cref="Dispose" /> along with the services.
/// </remarks>
public sealed class ResidentWorkspaceRegistry : IResidentWorkspaceProvider, IDisposable
{
    private readonly TimeSpan _buildTimeout;
    private readonly object _gate = new();
    private readonly Dictionary<string, ResidentWorkspaceService> _byRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _binlogs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Initializes a new instance of the <see cref="ResidentWorkspaceRegistry" /> class.
    /// </summary>
    /// <param name="buildTimeout">The maximum time to allow a warm build; defaults to 5 minutes.</param>
    public ResidentWorkspaceRegistry(TimeSpan? buildTimeout = null) =>
        _buildTimeout = buildTimeout ?? TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Warms a repository root: builds it once with a binary log and holds the rehydrated resident workspace,
    ///     so subsequent reads for that root are resident-grade. Idempotent: a root already warmed is a no-op.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="cancellationToken">A token to cancel the warm build.</param>
    /// <returns>True when the root is (now or already) resident; false when it could not be built or captured.</returns>
    public async Task<bool> WarmAsync(string root, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(root);
        lock (_gate)
        {
            if (_byRoot.ContainsKey(full))
                return true;
        }

        var discovery = await new DotNetWorkspaceDiscoverer().DiscoverAsync(full, cancellationToken);
        var target = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (target is null)
            return false;

        var binlog = Path.Combine(Path.GetTempPath(), $"fuse-resident-{Guid.NewGuid():N}.binlog");
        var built = await RunBuildAsync(target, binlog, cancellationToken);
        if (!built || !File.Exists(binlog))
        {
            TryDelete(binlog);
            return false;
        }

        ResidentWorkspaceService service;
        try
        {
            service = new ResidentWorkspaceService(full, ResidentWorkspace.LoadFromBinlog(binlog, cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            TryDelete(binlog);
            return false;
        }

        lock (_gate)
        {
            if (_byRoot.ContainsKey(full))
            {
                // Another warm raced us; keep the existing one and drop ours.
                service.Dispose();
                TryDelete(binlog);
                return true;
            }

            _byRoot[full] = service;
            _binlogs[full] = binlog;
        }

        return true;
    }

    /// <summary>
    ///     Applies a watcher batch to the resident workspace serving a root, if it is warmed.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="batch">The coalesced file changes.</param>
    /// <param name="cancellationToken">A token to cancel the update.</param>
    /// <returns>The update result, or null when the root is not warmed.</returns>
    public ResidentUpdateResult? ApplyBatch(string root, IReadOnlyList<WorkspaceFileChange> batch, CancellationToken cancellationToken)
    {
        var service = Resolve(root);
        return service?.ApplyBatch(batch, cancellationToken);
    }

    /// <summary>
    ///     Projects the resident state of the projects touched by a batch of changed files for a root into the
    ///     store (S1 step 4), delegating to that root's <see cref="ResidentWorkspaceService" />; a no-op when the
    ///     root is not warmed.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="indexer">The semantic indexer providing the projection.</param>
    /// <param name="store">The index store to project into.</param>
    /// <param name="changedAbsolutePaths">The absolute paths of the changed files.</param>
    /// <param name="cancellationToken">A token to cancel the projection.</param>
    /// <returns>The number of projects re-projected, or 0 when the root is not warmed.</returns>
    public async Task<int> ProjectChangedAsync(
        string root, Fuse.Semantics.SemanticIndexer indexer, Fuse.Indexing.IWorkspaceIndexStore store,
        IReadOnlyList<string> changedAbsolutePaths, CancellationToken cancellationToken)
    {
        var service = Resolve(root);
        return service is null ? 0 : await service.ProjectChangedAsync(indexer, store, changedAbsolutePaths, cancellationToken);
    }

    /// <inheritdoc />
    public ResidentStatus? DescribeResident(string root) => Resolve(root)?.DescribeResident(root);

    /// <inheritdoc />
    public IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
        string root, string relativeFilePath, string newContent, CancellationToken cancellationToken) =>
        Resolve(root)?.TryCheckOverlay(root, relativeFilePath, newContent, cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<CheckDiagnostic>? TryGetCurrentDiagnostics(string root) =>
        Resolve(root)?.TryGetCurrentDiagnostics(root);

    /// <inheritdoc />
    public Task<IReadOnlyList<CheckDiagnostic>?> TryCheckOverlayAsync(
        string root, string relativeFilePath, string newContent, bool includeAnalyzers, CancellationToken cancellationToken) =>
        Resolve(root) is { } service
            ? service.TryCheckOverlayAsync(root, relativeFilePath, newContent, includeAnalyzers, cancellationToken)
            : Task.FromResult<IReadOnlyList<CheckDiagnostic>?>(null);

    /// <summary>
    ///     Evicts a root's resident workspace, so reads for it revert to store-backed (S1 storm handling): when a
    ///     bulk change outruns incremental update, the resident state is dropped rather than served stale, and the
    ///     store path (with its N6 reconcile) answers until the root is warmed again.
    /// </summary>
    /// <param name="root">The absolute repository root to evict.</param>
    /// <returns>True when a resident workspace was evicted; false when the root was not warmed.</returns>
    public bool Evict(string root)
    {
        var full = Path.GetFullPath(root);
        ResidentWorkspaceService? service;
        string? binlog;
        lock (_gate)
        {
            if (!_byRoot.Remove(full, out service))
                return false;
            _binlogs.Remove(full, out binlog);
        }

        service.Dispose();
        if (binlog is not null)
            TryDelete(binlog);
        return true;
    }

    private ResidentWorkspaceService? Resolve(string root)
    {
        var full = Path.GetFullPath(root);
        lock (_gate)
            return _byRoot.GetValueOrDefault(full);
    }

    // Runs `dotnet build <target> -bl:<binlog>` with a fixed, bounded argument list and the configured timeout,
    // capturing a binary log the resident workspace rehydrates. Returns whether the build process exited zero.
    private async Task<bool> RunBuildAsync(string target, string binlogPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(target) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add($"-bl:{binlogPath}");
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v:quiet");

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }

        // Drain the pipes so the child never blocks on a full buffer; the content is not needed (the binlog is).
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_buildTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return false;
        }

        // A binlog is emitted even on a nonzero exit (the compiler still ran on the buildable projects), so the
        // resident workspace can hold a partial graph; the caller checks the binlog exists.
        return process.ExitCode == 0 || File.Exists(binlogPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    /// <summary>Disposes every warmed resident workspace and deletes the retained binlogs.</summary>
    public void Dispose()
    {
        List<ResidentWorkspaceService> services;
        List<string> binlogs;
        lock (_gate)
        {
            services = _byRoot.Values.ToList();
            binlogs = _binlogs.Values.ToList();
            _byRoot.Clear();
            _binlogs.Clear();
        }

        foreach (var service in services)
            service.Dispose();
        foreach (var binlog in binlogs)
            TryDelete(binlog);
    }
}
