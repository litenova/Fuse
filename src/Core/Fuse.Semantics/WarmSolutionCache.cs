using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Fuse.Semantics;

/// <summary>
///     A per-root, host-owned cache of MSBuild-loaded Roslyn <see cref="Solution" />s (R42), so the tools that
///     need a design-time workspace - <c>fuse_refactor</c> and <c>fuse_workspace doctor</c>'s live load - reuse one
///     held solution instead of re-opening an <see cref="MSBuildWorkspace" /> and paying the whole design-time
///     load (measured 4-26s) on every call. Roslyn solutions are immutable snapshots, so a held solution is
///     forked per refactor for free.
/// </summary>
/// <remarks>
///     <para>
///         Correctness is preserved by a cheap freshness signature (the count, newest write time, and total size
///         of the tracked <c>.cs</c> files under the target's directory, excluding build and vendored trees): an
///         edit, addition, or deletion changes the signature and forces a fresh load, so a held solution is only
///         reused while the source it was loaded from is unchanged. A reused solution therefore yields the same
///         diff a cold per-call load would, and a changed tree pays the same cost as today (never worse).
///     </para>
///     <para>
///         Memory is bounded (a held solution is a full Roslyn compilation set, hundreds of MB on a large repo):
///         a hard LRU cap (<c>FUSE_WARM_SOLUTION_CAP</c>, default 3) evicts the least-recently-used root beyond
///         the cap, and an idle window (<c>FUSE_WARM_SOLUTION_IDLE_MINUTES</c>, default 30) evicts a root not
///         touched within it, disposing the underlying workspace each time. An evicted root falls back to a fresh
///         per-call load. This is a read/compile cache; it never writes the tree, so the D2 no-tree-write and
///         D13/R19 single-writer invariants are untouched.
///     </para>
/// </remarks>
public sealed class WarmSolutionCache : IDisposable
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".fuse", ".idea", "node_modules", ".corpus", "packages", "TestResults", ".vscode",
    };

    private readonly object _gate = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Dictionary<string, Entry> _byTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _watchedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new(); // front = most-recently-used
    private readonly Dictionary<string, LinkedListNode<string>> _lruIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _cap;
    private readonly TimeSpan _idleWindow;
    private readonly Func<DateTime> _clock;
    private readonly Func<string, CancellationToken, Task<LoadedWorkspace>> _loader;
    private readonly Func<string, object> _signature;
    private int _loadCount;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="WarmSolutionCache" /> class.
    /// </summary>
    /// <param name="cap">
    ///     The maximum number of roots held at once; the least-recently-used root beyond this is evicted. Defaults
    ///     to <c>FUSE_WARM_SOLUTION_CAP</c> (or 3).
    /// </param>
    /// <param name="idleWindow">
    ///     A root not accessed within this window is evicted on the next access. Defaults to
    ///     <c>FUSE_WARM_SOLUTION_IDLE_MINUTES</c> minutes (or 30).
    /// </param>
    /// <param name="clock">A clock for the idle sweep; defaults to <see cref="DateTime.UtcNow" />. Injectable for tests.</param>
    /// <param name="loader">
    ///     The workspace loader; defaults to opening an <see cref="MSBuildWorkspace" />. Injectable for tests so the
    ///     LRU, freshness, and eviction behavior can be exercised without an SDK.
    /// </param>
    /// <param name="signature">
    ///     The freshness signature of a target; defaults to a pruned <c>.cs</c> file scan. Injectable for tests.
    /// </param>
    public WarmSolutionCache(
        int? cap = null,
        TimeSpan? idleWindow = null,
        Func<DateTime>? clock = null,
        Func<string, CancellationToken, Task<LoadedWorkspace>>? loader = null,
        Func<string, object>? signature = null)
    {
        _cap = cap ?? ReadCapFromEnvironment();
        _idleWindow = idleWindow ?? ReadIdleFromEnvironment();
        _clock = clock ?? (() => DateTime.UtcNow);
        _loader = loader ?? DefaultLoadAsync;
        _signature = signature ?? (target => ComputeSignature(target));
    }

    /// <summary>The total number of real MSBuild solution/project opens this cache has performed (a cache-miss counter).</summary>
    public int LoadCount
    {
        get { lock (_gate) return _loadCount; }
    }

    /// <summary>The number of roots currently held.</summary>
    public int HeldCount
    {
        get { lock (_gate) return _byTarget.Count; }
    }

    /// <summary>
    ///     Returns a live Roslyn <see cref="Solution" /> for the target, reusing a held one when the tracked
    ///     source under the target's directory is unchanged, else opening a fresh workspace and caching it. The
    ///     returned solution is an immutable snapshot the caller forks freely.
    /// </summary>
    /// <param name="solutionOrProjectPath">The absolute path to the solution (<c>.sln</c>/<c>.slnx</c>) or project.</param>
    /// <param name="cancellationToken">A token to cancel the load.</param>
    /// <returns>The held or freshly-loaded solution and the load failures observed when it was opened.</returns>
    /// <exception cref="OperationCanceledException">The load was cancelled.</exception>
    public async Task<CachedSolution> OpenAsync(string solutionOrProjectPath, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(solutionOrProjectPath);

        // A watcher-attached root invalidates held entries on a settled change, so a no-change hit needs neither an
        // MSBuild load nor the fallback directory scan. One-shot and unobserved roots retain the signature check.
        lock (_gate)
        {
            if (IsWatcherAttachedLocked(full) && TryHitLocked(full, signature: null, requireMatchingSignature: false, out var cached))
                return cached;
        }

        var signature = _signature(full);

        // Miss or stale: serialize loads so two concurrent callers never both pay the full open.
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check under the load gate: another caller may have just loaded (or refreshed) this target.
            lock (_gate)
            {
                var requireMatchingSignature = !IsWatcherAttachedLocked(full);
                if (TryHitLocked(full, signature, requireMatchingSignature, out var cached))
                    return cached;
            }

            var loaded = await _loader(full, cancellationToken);

            lock (_gate)
            {
                // Replace any stale entry for this target (a changed signature), disposing its workspace.
                if (_byTarget.Remove(full, out var stale))
                    stale.Workspace.Dispose();

                _byTarget[full] = new Entry(loaded.Workspace, loaded.Solution, loaded.LoadFailures, signature, _clock());
                _loadCount++;
                TouchLruLocked(full);
                EnforceCapLocked();
                SweepIdleLocked();
            }

            return new CachedSolution(loaded.Solution, loaded.LoadFailures);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    ///     Evicts a target's held solution if present, disposing its workspace. Used by the watcher storm path and
    ///     by explicit invalidation; a normal edit is already caught by the freshness signature.
    /// </summary>
    /// <param name="solutionOrProjectPath">The absolute path to the solution or project to evict.</param>
    /// <returns>True when an entry was evicted; false when the target was not held.</returns>
    public bool Evict(string solutionOrProjectPath)
    {
        var full = Path.GetFullPath(solutionOrProjectPath);
        lock (_gate)
            return EvictLocked(full);
    }

    /// <summary>Evicts every held target under a workspace root.</summary>
    /// <param name="root">The workspace root whose held targets should be released.</param>
    /// <returns>The number of held workspaces disposed.</returns>
    public int EvictUnderRoot(string root)
    {
        var full = Path.GetFullPath(root);
        lock (_gate)
        {
            var affected = _byTarget.Keys.Where(target => IsUnderRoot(full, target)).ToList();
            foreach (var target in affected)
                EvictLocked(target);
            return affected.Count;
        }
    }

    /// <summary>
    ///     Registers a watcher-covered source root. While at least one registration covers a held target, clean
    ///     cache hits skip the directory signature scan and rely on <see cref="InvalidateWatcherRoot" /> to evict
    ///     the snapshot after a settled filesystem change. Disposing the returned registration restores the scan
    ///     fallback for that root.
    /// </summary>
    /// <param name="root">The absolute or relative workspace root watched by the caller.</param>
    /// <returns>A registration to dispose when the caller stops watching the root.</returns>
    public IDisposable AttachWatcher(string root)
    {
        var full = Path.GetFullPath(root);
        lock (_gate)
        {
            _watchedRoots.TryGetValue(full, out var registrations);
            _watchedRoots[full] = registrations + 1;
        }

        return new WatcherRegistration(this, full);
    }

    /// <summary>
    ///     Evicts all held targets under a watcher-covered root. Call this from the watcher's settled change event;
    ///     the next caller reloads the same way a changed freshness signature would in the no-watcher path.
    /// </summary>
    /// <param name="root">The workspace root whose settled watcher event fired.</param>
    public void InvalidateWatcherRoot(string root)
    {
        var full = Path.GetFullPath(root);
        lock (_gate)
        {
            if (!_watchedRoots.ContainsKey(full))
                return;

            var affected = _byTarget.Keys.Where(target => IsUnderRoot(full, target)).ToList();
            foreach (var target in affected)
                EvictLocked(target);
        }
    }

    /// <summary>
    ///     Returns whether a watcher path can change this cache's fallback freshness signature for the supplied
    ///     root. The watcher must ignore generated and build trees for the same reason <see cref="ComputeSignature" />
    ///     does: their writes do not make a held source solution stale.
    /// </summary>
    /// <param name="root">The workspace root whose source signature is tracked.</param>
    /// <param name="path">The changed file path from a watcher batch.</param>
    /// <returns>True for a tracked C# source file below the root; otherwise false.</returns>
    public static bool IsTrackedSourceFile(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !IsUnderRoot(fullRoot, fullPath))
            return false;

        var directory = Path.GetDirectoryName(Path.GetRelativePath(fullRoot, fullPath));
        return directory is null
            || !directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(ExcludedDirectories.Contains);
    }

    private bool TryHitLocked(string full, object? signature, bool requireMatchingSignature, out CachedSolution cached)
    {
        if (_byTarget.TryGetValue(full, out var entry)
            && (!requireMatchingSignature || entry.Signature.Equals(signature)))
        {
            entry.LastAccessUtc = _clock();
            TouchLruLocked(full);
            cached = new CachedSolution(entry.Solution, entry.LoadFailures);
            return true;
        }

        cached = default!;
        return false;
    }

    private bool IsWatcherAttachedLocked(string target) => _watchedRoots.Keys.Any(root => IsUnderRoot(root, target));

    private void DetachWatcher(string root)
    {
        lock (_gate)
        {
            if (!_watchedRoots.TryGetValue(root, out var registrations))
                return;

            if (registrations == 1)
                _watchedRoots.Remove(root);
            else
                _watchedRoots[root] = registrations - 1;
        }
    }

    private static bool IsUnderRoot(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target);
        return relative.Length == 0
            || (!Path.IsPathRooted(relative)
                && !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith("../", StringComparison.Ordinal));
    }

    private void TouchLruLocked(string full)
    {
        if (_lruIndex.TryGetValue(full, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
        else
        {
            _lruIndex[full] = _lru.AddFirst(full);
        }
    }

    private void EnforceCapLocked()
    {
        while (_byTarget.Count > _cap && _lru.Last is { } last)
            EvictLocked(last.Value);
    }

    private void SweepIdleLocked()
    {
        var now = _clock();
        var stale = _byTarget
            .Where(kvp => now - kvp.Value.LastAccessUtc > _idleWindow)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in stale)
            EvictLocked(key);
    }

    private bool EvictLocked(string full)
    {
        if (!_byTarget.Remove(full, out var entry))
            return false;
        if (_lruIndex.Remove(full, out var node))
            _lru.Remove(node);
        entry.Workspace.Dispose();
        return true;
    }

    // The default loader: registers the MSBuild locator once and opens the solution or project fresh, keeping the
    // workspace alive (the cache owns and disposes it). A locator or open failure propagates so the caller formats
    // its own abstain message.
    private static async Task<LoadedWorkspace> DefaultLoadAsync(string full, CancellationToken cancellationToken)
    {
        try
        {
            EnsureLocatorRegistered();
        }
        catch (Exception ex)
        {
            throw new MsBuildLocatorUnavailableException(ex);
        }

        var workspace = MSBuildWorkspace.Create();
        var failures = WorkspaceLoadFailures.Track(workspace);
        Solution solution;
        try
        {
            solution = full.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                       || full.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                ? await workspace.OpenSolutionAsync(full, cancellationToken: cancellationToken)
                : (await workspace.OpenProjectAsync(full, cancellationToken: cancellationToken)).Solution;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }

        return new LoadedWorkspace(workspace, solution, failures);
    }

    // The freshness signature: the count, newest write time, and total size of the tracked .cs files under the
    // target's directory, excluding build and vendored trees. Any edit (newer write time), addition (higher
    // count), or deletion (lower count) changes it, so a held solution is reused only while its source is
    // unchanged. Cheap (a pruned directory walk of file stats, milliseconds) relative to the seconds an MSBuild
    // load costs. Best-effort: an unreadable directory is skipped rather than failing the open.
    private static SourceSignature ComputeSignature(string target)
    {
        var directory = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
        if (directory is null)
            return default;

        var count = 0;
        long maxWriteTicks = 0;
        long totalBytes = 0;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subdirectories)
            {
                var name = Path.GetFileName(sub);
                if (ExcludedDirectories.Contains(name))
                    continue;
                // Skip reparse points (symlinks/junctions) to avoid cycles and vendored links.
                try
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                pending.Push(sub);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "*.cs");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    count++;
                    totalBytes += info.Length;
                    var ticks = info.LastWriteTimeUtc.Ticks;
                    if (ticks > maxWriteTicks)
                        maxWriteTicks = ticks;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return new SourceSignature(count, maxWriteTicks, totalBytes);
    }

    private static void EnsureLocatorRegistered()
        => MsBuildLocatorRegistration.EnsureRegistered();

    private static int ReadCapFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_WARM_SOLUTION_CAP");
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 3;
    }

    private static TimeSpan ReadIdleFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_WARM_SOLUTION_IDLE_MINUTES");
        return int.TryParse(value, out var parsed) && parsed > 0
            ? TimeSpan.FromMinutes(parsed)
            : TimeSpan.FromMinutes(30);
    }

    /// <summary>Disposes every held workspace.</summary>
    public void Dispose()
    {
        List<Entry> entries;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = _byTarget.Values.ToList();
            _byTarget.Clear();
            _watchedRoots.Clear();
            _lru.Clear();
            _lruIndex.Clear();
        }

        foreach (var entry in entries)
            entry.Workspace.Dispose();
        _loadGate.Dispose();
    }

    private sealed class Entry(Workspace workspace, Solution solution, IReadOnlyList<string> loadFailures, object signature, DateTime lastAccessUtc)
    {
        public Workspace Workspace { get; } = workspace;
        public Solution Solution { get; } = solution;
        public IReadOnlyList<string> LoadFailures { get; } = loadFailures;
        public object Signature { get; } = signature;
        public DateTime LastAccessUtc { get; set; } = lastAccessUtc;
    }

    private sealed class WatcherRegistration(WarmSolutionCache cache, string root) : IDisposable
    {
        private WarmSolutionCache? _cache = cache;

        public void Dispose()
        {
            var cache = Interlocked.Exchange(ref _cache, null);
            cache?.DetachWatcher(root);
        }
    }

    private readonly record struct SourceSignature(int Count, long MaxWriteTicks, long TotalBytes);
}

/// <summary>
///     A workspace loaded by <see cref="WarmSolutionCache" />: the held disposable workspace, its current
///     solution snapshot, and the load failures observed while opening it.
/// </summary>
/// <param name="Workspace">The disposable workspace the cache owns and disposes on eviction.</param>
/// <param name="Solution">The immutable solution snapshot.</param>
/// <param name="LoadFailures">The genuine MSBuild load failures observed at open; empty means every project loaded.</param>
public sealed record LoadedWorkspace(Workspace Workspace, Solution Solution, IReadOnlyList<string> LoadFailures);

/// <summary>
///     A Roslyn <see cref="Solution" /> returned by <see cref="WarmSolutionCache" />, with the MSBuild load
///     failures observed when the underlying workspace was opened.
/// </summary>
/// <param name="Solution">The immutable solution snapshot to fork or read.</param>
/// <param name="LoadFailures">
///     The genuine load failures (per <see cref="WorkspaceLoadFailures" />) observed at open; empty means every
///     project loaded. A refactor abstains when this is non-empty.
/// </param>
public sealed record CachedSolution(Solution Solution, IReadOnlyList<string> LoadFailures);
