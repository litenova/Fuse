using System.Diagnostics;
using System.Text.Json;
using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     A pool of long-lived build-capture check workers, one per captured compiler log (R48). Without a resident
///     workspace (the store-backed default), an oracle <c>fuse_check</c> otherwise spawns a fresh worker that
///     rehydrates the compilation from the complog, checks one file, and exits - repeated on every edit in an agent
///     loop, several seconds each. This pool keeps a worker alive per complog, rehydrating once and answering many
///     checks against the held compilation (each speculative edit forks the in-memory document), so the second and
///     later checks in a session skip the rehydrate. Bounded and idle-evicted; a cold or absent worker leaves the
///     caller to fall back to the spawn-per-call path, so it is never worse.
/// </summary>
/// <remarks>
///     Check honesty is preserved exactly: the pooled worker runs the identical fork-and-diagnostics logic
///     (<c>CheckHeld</c>, the same code <c>CheckFromLog</c> uses) over the held compilations, so a pooled verdict
///     equals a spawn-per-call verdict. Requests to one worker are serialized (one check at a time over its
///     stdio), and the held compilations are immutable Roslyn snapshots forked per check.
/// </remarks>
public sealed class PooledCheckWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byComplog = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _cap;
    private readonly TimeSpan _idleWindow;
    private readonly Func<DateTime> _clock;
    private readonly Func<string, ICheckWorkerChannel>? _channelFactory;
    private readonly string? _workerDllPath;
    private int _spawnCount;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="PooledCheckWorker" /> class.
    /// </summary>
    /// <param name="cap">The maximum number of live workers; the least-recently-used beyond this is stopped. Defaults to <c>FUSE_CHECK_WORKER_CAP</c> (or 2).</param>
    /// <param name="idleWindow">A worker not used within this window is stopped on the next access. Defaults to <c>FUSE_CHECK_WORKER_IDLE_MINUTES</c> minutes (or 30).</param>
    /// <param name="clock">A clock for the idle sweep; defaults to <see cref="DateTime.UtcNow" />. Injectable for tests.</param>
    /// <param name="channelFactory">The worker-channel factory; defaults to spawning the real worker process. Injectable so the pool bookkeeping can be tested without an SDK.</param>
    public PooledCheckWorker(
        int? cap = null,
        TimeSpan? idleWindow = null,
        Func<DateTime>? clock = null,
        Func<string, ICheckWorkerChannel>? channelFactory = null)
    {
        _cap = cap ?? ReadCapFromEnvironment();
        _idleWindow = idleWindow ?? ReadIdleFromEnvironment();
        _clock = clock ?? (() => DateTime.UtcNow);
        _channelFactory = channelFactory;
        _workerDllPath = channelFactory is null ? BuildCaptureClient.ResolveWorkerPath() : "injected";
    }

    /// <summary>Whether a worker can be started (a worker dll is configured, or a channel factory is injected).</summary>
    public bool IsAvailable => _channelFactory is not null || (!string.IsNullOrWhiteSpace(_workerDllPath) && File.Exists(_workerDllPath));

    /// <summary>The number of workers started this process (a spawn counter so a test can assert reuse).</summary>
    public int SpawnCount
    {
        get { lock (_gate) return _spawnCount; }
    }

    /// <summary>The number of live workers currently held.</summary>
    public int HeldCount
    {
        get { lock (_gate) return _byComplog.Count; }
    }

    /// <summary>
    ///     Checks a proposed single-file edit against a pooled worker for the complog, starting the worker (and
    ///     rehydrating the complog once) on first use and reusing it after. Returns null when no worker is
    ///     available or the pooled request fails, so the caller falls back to the spawn-per-call path.
    /// </summary>
    /// <param name="complogPath">The absolute path to the captured compiler log.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <param name="ownerRoot">
    ///     The repository root that owns this held worker, when known. The owner is only used for root-scoped
    ///     budget eviction; it does not affect worker reuse or the compiler verdict.
    /// </param>
    /// <returns>The check verdict, or null to fall back to spawn-per-call.</returns>
    public async Task<CheckResult?> TryCheckAsync(
        string complogPath,
        string relativeFilePath,
        string newContent,
        CancellationToken cancellationToken,
        string? ownerRoot = null)
    {
        if (!IsAvailable)
            return null;

        var full = Path.GetFullPath(complogPath);
        Entry entry;
        try
        {
            entry = await GetOrStartAsync(full, ownerRoot, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Evict(full);
            return null;
        }

        await entry.Lock.WaitAsync(cancellationToken);
        try
        {
            var contentPath = Path.Combine(Path.GetTempPath(), $"fuse-pooled-check-{Guid.NewGuid():N}.cs");
            await File.WriteAllTextAsync(contentPath, newContent, cancellationToken);
            try
            {
                var request = JsonSerializer.Serialize(
                    new CheckRequest(relativeFilePath, contentPath), BuildCaptureJsonContext.Default.CheckRequest);
                var responseLine = await entry.Channel.RequestAsync(request, cancellationToken);
                var result = JsonSerializer.Deserialize(responseLine, BuildCaptureJsonContext.Default.CheckResult);
                return result;
            }
            finally
            {
                try { File.Delete(contentPath); } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The worker died or misbehaved; drop it and let the caller fall back to spawn-per-call.
            Evict(full);
            return null;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    private async Task<Entry> GetOrStartAsync(string full, string? ownerRoot, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_byComplog.TryGetValue(full, out var existing))
            {
                existing.LastAccessUtc = _clock();
                TouchLruLocked(full);
                return existing;
            }
        }

        var channel = (_channelFactory ?? RealChannelFactory)(full);
        try
        {
            await channel.StartAsync(cancellationToken);
        }
        catch
        {
            channel.Dispose();
            throw;
        }

        lock (_gate)
        {
            if (_byComplog.TryGetValue(full, out var raced))
            {
                // Another caller started one first; keep it and drop ours.
                channel.Dispose();
                raced.LastAccessUtc = _clock();
                TouchLruLocked(full);
                return raced;
            }

            var entry = new Entry(channel, _clock(), ownerRoot is null ? null : Path.GetFullPath(ownerRoot));
            _byComplog[full] = entry;
            _spawnCount++;
            TouchLruLocked(full);
            EnforceCapLocked();
            SweepIdleLocked();
            return entry;
        }
    }

    private ICheckWorkerChannel RealChannelFactory(string complogPath) =>
        new ProcessCheckWorkerChannel(_workerDllPath!, complogPath);

    /// <summary>Stops and removes a worker for a complog if present.</summary>
    /// <param name="complogPath">The complog whose worker to stop.</param>
    /// <returns>True when a worker was stopped; false when none was held.</returns>
    public bool Evict(string complogPath)
    {
        var full = Path.GetFullPath(complogPath);
        lock (_gate)
            return EvictLocked(full);
    }

    /// <summary>Stops every worker held by this pool.</summary>
    /// <returns>The number of workers stopped.</returns>
    public int EvictAll()
    {
        lock (_gate)
        {
            var keys = _byComplog.Keys.ToList();
            foreach (var key in keys)
                EvictLocked(key);
            return keys.Count;
        }
    }

    /// <summary>Stops every worker explicitly owned by one repository root.</summary>
    /// <param name="root">The repository root whose workers should be released.</param>
    /// <returns>The number of workers stopped.</returns>
    public int EvictOwnedBy(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        lock (_gate)
        {
            var keys = _byComplog
                .Where(pair => string.Equals(pair.Value.OwnerRoot, fullRoot, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();
            foreach (var key in keys)
                EvictLocked(key);
            return keys.Count;
        }
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
        while (_byComplog.Count > _cap && _lru.Last is { } last)
            EvictLocked(last.Value);
    }

    private void SweepIdleLocked()
    {
        var now = _clock();
        var stale = _byComplog.Where(kvp => now - kvp.Value.LastAccessUtc > _idleWindow).Select(kvp => kvp.Key).ToList();
        foreach (var key in stale)
            EvictLocked(key);
    }

    private bool EvictLocked(string full)
    {
        if (!_byComplog.Remove(full, out var entry))
            return false;
        if (_lruIndex.Remove(full, out var node))
            _lru.Remove(node);
        entry.Channel.Dispose();
        return true;
    }

    private static int ReadCapFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_CHECK_WORKER_CAP");
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 2;
    }

    private static TimeSpan ReadIdleFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_CHECK_WORKER_IDLE_MINUTES");
        return int.TryParse(value, out var parsed) && parsed > 0 ? TimeSpan.FromMinutes(parsed) : TimeSpan.FromMinutes(30);
    }

    /// <summary>Stops every live worker.</summary>
    public void Dispose()
    {
        List<Entry> entries;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = _byComplog.Values.ToList();
            _byComplog.Clear();
            _lru.Clear();
            _lruIndex.Clear();
        }

        foreach (var entry in entries)
            entry.Channel.Dispose();
    }

    private sealed class Entry(ICheckWorkerChannel channel, DateTime lastAccessUtc, string? ownerRoot)
    {
        public ICheckWorkerChannel Channel { get; } = channel;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public DateTime LastAccessUtc { get; set; } = lastAccessUtc;
        public string? OwnerRoot { get; } = ownerRoot;
    }
}

/// <summary>
///     A channel to one long-lived build-capture check worker (R48): starts it (rehydrating the complog once) and
///     sends it speculative-check requests, one at a time, reading one verdict per request. Abstracted so the pool
///     bookkeeping can be tested with an in-process fake instead of a real worker process.
/// </summary>
public interface ICheckWorkerChannel : IDisposable
{
    /// <summary>Starts the worker and waits until it has rehydrated and reported ready.</summary>
    /// <param name="cancellationToken">A token to cancel the start.</param>
    /// <returns>A task that completes when the worker is ready to answer requests.</returns>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Sends one request line and returns the one response line.</summary>
    /// <param name="requestLine">The serialized <see cref="CheckRequest" /> line.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The response line (a serialized <see cref="CheckResult" />).</returns>
    Task<string> RequestAsync(string requestLine, CancellationToken cancellationToken);
}

// The real channel: spawns `dotnet <workerDll> --serve-check <complog>` and talks to it over stdin/stdout. The
// worker writes a "ready" line once rehydration completes, then one CheckResult line per request. stderr is drained
// in the background so the worker never blocks on a full pipe.
internal sealed class ProcessCheckWorkerChannel(string workerDllPath, string complogPath) : ICheckWorkerChannel
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
    private Process? _process;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(workerDllPath);
        psi.ArgumentList.Add("--serve-check");
        psi.ArgumentList.Add(complogPath);

        var process = new Process { StartInfo = psi };
        process.Start();
        _process = process;
        _ = process.StandardError.ReadToEndAsync(cancellationToken); // drain stderr so the child never blocks.

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ReadyTimeout);
        var ready = await process.StandardOutput.ReadLineAsync(timeoutCts.Token);
        if (ready is null || !ready.Contains("\"ready\""))
            throw new InvalidOperationException("build-capture check worker did not report ready");
    }

    public async Task<string> RequestAsync(string requestLine, CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited)
            throw new InvalidOperationException("build-capture check worker is not running");

        await _process.StandardInput.WriteLineAsync(requestLine.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        var response = await _process.StandardOutput.ReadLineAsync(timeoutCts.Token);
        return response ?? throw new InvalidOperationException("build-capture check worker produced no response");
    }

    public void Dispose()
    {
        if (_process is null)
            return;
        try
        {
            if (!_process.HasExited)
            {
                try { _process.StandardInput.WriteLine("quit"); _process.StandardInput.Flush(); } catch (IOException) { }
                if (!_process.WaitForExit(2000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }
}
