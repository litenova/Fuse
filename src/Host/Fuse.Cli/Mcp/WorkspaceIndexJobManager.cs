using System.Collections.Concurrent;
using Fuse.Collection.FileSystem;
using Fuse.Reduction.Caching;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Keeps a single index worker per repository root. A caller can stop waiting without stopping the shared job;
///     only <see cref="CancelAsync" /> changes the worker lifetime.
/// </summary>
public sealed class WorkspaceIndexJobManager : IWorkspaceIndexJobManager, IDisposable
{
    private const int MaxWarnings = 20;
    private readonly ConcurrentDictionary<string, ManagedJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly IWorkspaceIndexJobExecutor _executor;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="WorkspaceIndexJobManager" /> class.
    /// </summary>
    /// <param name="executor">The index pipeline executor.</param>
    /// <param name="timeProvider">The time source used for elapsed and ETA reporting.</param>
    public WorkspaceIndexJobManager(IWorkspaceIndexJobExecutor executor, TimeProvider? timeProvider = null)
    {
        _executor = executor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<IndexJobStartResult> StartOrJoinAsync(IndexJobRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeRequest(request);

        while (true)
        {
            if (_jobs.TryGetValue(normalized.Root, out var existing) && existing.IsActive)
            {
                var decision = existing.TryJoin(normalized, _timeProvider);
                return Task.FromResult(decision);
            }

            var created = new ManagedJob(normalized, _timeProvider);
            if (existing is not null && !_jobs.TryUpdate(normalized.Root, created, existing))
                continue;
            if (existing is null && !_jobs.TryAdd(normalized.Root, created))
                continue;

            created.Start(_executor, _shutdown.Token, _timeProvider);
            return Task.FromResult(new IndexJobStartResult(created.Snapshot(_timeProvider), Joined: false, Conflict: false));
        }
    }

    /// <inheritdoc />
    public IndexJobSnapshot? GetStatus(string root)
    {
        var normalizedRoot = NormalizeRoot(root);
        return _jobs.TryGetValue(normalizedRoot, out var job)
            ? job.Snapshot(_timeProvider)
            : null;
    }

    /// <inheritdoc />
    public Task<IndexJobSnapshot?> CancelAsync(string root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedRoot = NormalizeRoot(root);
        if (!_jobs.TryGetValue(normalizedRoot, out var job) || !job.IsActive)
            return Task.FromResult<IndexJobSnapshot?>(null);

        job.RequestCancellation(_timeProvider);
        return Task.FromResult<IndexJobSnapshot?>(job.Snapshot(_timeProvider));
    }

    /// <inheritdoc />
    public async Task<IndexJobSnapshot?> WaitForCompletionAsync(string root, CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizeRoot(root);
        if (!_jobs.TryGetValue(normalizedRoot, out var job))
            return null;

        await job.Completion.WaitAsync(cancellationToken);
        return job.Snapshot(_timeProvider);
    }

    /// <inheritdoc />
    public async Task<IndexJobSnapshot?> WaitForSyntaxReadyAsync(string root, CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizeRoot(root);
        if (!_jobs.TryGetValue(normalizedRoot, out var job))
            return null;

        await job.SyntaxReady.WaitAsync(cancellationToken);
        return job.Snapshot(_timeProvider);
    }

    /// <inheritdoc />
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        var active = _jobs.Values.Where(job => job.IsActive).Select(job => job.Completion).ToArray();
        if (active.Length > 0)
            await Task.WhenAll(active).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await ShutdownAsync(shutdownTimeout.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    /// <summary>Synchronously stops jobs for DI containers that do not support asynchronous disposal.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static IndexJobRequest NormalizeRequest(IndexJobRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Root);
        var root = NormalizeRoot(request.Root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);

        var capture = request.CaptureBundlePath is null
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.CaptureBundlePath));
        return request with { Root = root, CaptureBundlePath = capture };
    }

    private static string NormalizeRoot(string root) =>
        WorkspaceIdentityResolver.TryResolveRepositoryRoot(root, out var repositoryRoot)
            ? repositoryRoot
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private sealed class ManagedJob
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _syntaxReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _warnings = [];
        private readonly string _jobId = Guid.NewGuid().ToString("N");
        private readonly DateTimeOffset _startedAt;
        private IndexDepth _targetDepth;
        private IndexJobState _state = IndexJobState.Queued;
        private IndexPhase _phase = IndexPhase.Inventory;
        private long _completedUnits;
        private long? _totalUnits;
        private string? _currentItem;
        private IndexCountSnapshot _counts = IndexCountSnapshot.Empty;
        private IndexStorageSnapshot _storage = IndexStorageSnapshot.Empty;
        private string? _errorCode;
        private string? _errorMessage;
        private long _phaseStartedTimestamp;
        private long _completedAtPhaseStart;
        private Task _completion = Task.CompletedTask;

        public ManagedJob(IndexJobRequest request, TimeProvider timeProvider)
        {
            Request = request;
            _targetDepth = request.Depth;
            _startedAt = timeProvider.GetUtcNow();
            _phaseStartedTimestamp = timeProvider.GetTimestamp();
        }

        public IndexJobRequest Request { get; }

        public Task Completion => _completion;

        public Task SyntaxReady => _syntaxReady.Task;

        public bool IsActive
        {
            get
            {
                lock (_sync)
                    return _state is IndexJobState.Queued or IndexJobState.Running or IndexJobState.Cancelling;
            }
        }

        public void Start(IWorkspaceIndexJobExecutor executor, CancellationToken shutdownToken, TimeProvider timeProvider)
        {
            lock (_sync)
            {
                _state = IndexJobState.Running;
                _completion = RunAsync(executor, shutdownToken, timeProvider);
            }
        }

        public IndexJobStartResult TryJoin(IndexJobRequest request, TimeProvider timeProvider)
        {
            lock (_sync)
            {
                if (request.Force)
                    return Conflict(timeProvider);

                if (!SameCapture(Request.CaptureBundlePath, request.CaptureBundlePath))
                    return Conflict(timeProvider);

                if (request.Depth == IndexDepth.Semantic)
                    _targetDepth = IndexDepth.Semantic;

                return new IndexJobStartResult(SnapshotCore(timeProvider), Joined: true, Conflict: false);
            }
        }

        public void RequestCancellation(TimeProvider timeProvider)
        {
            lock (_sync)
            {
                if (_state is not (IndexJobState.Queued or IndexJobState.Running))
                    return;
                _state = IndexJobState.Cancelling;
                _currentItem = "cancellation requested";
            }

            _cancellation.Cancel();
        }

        public IndexJobSnapshot Snapshot(TimeProvider timeProvider)
        {
            lock (_sync)
                return SnapshotCore(timeProvider);
        }

        private async Task RunAsync(IWorkspaceIndexJobExecutor executor, CancellationToken shutdownToken, TimeProvider timeProvider)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token, shutdownToken);
            IProgress<IndexJobProgress> progress = new InlineProgress<IndexJobProgress>(
                update => UpdateProgress(update, timeProvider));
            try
            {
                SemanticIndexResult result;
                if (Request.CaptureBundlePath is not null)
                {
                    result = await executor.ExecuteAsync(_jobId, Request, progress, linked.Token);
                    UpdateResult(result, timeProvider);
                }
                else
                {
                    // Every source job commits a usable syntax index first. A semantic request, whether it was
                    // present at start or joined during syntax extraction, then continues through compiler work.
                    result = await executor.ExecuteAsync(
                        _jobId,
                        Request with { Depth = IndexDepth.Syntax },
                        progress,
                        linked.Token);
                    UpdateResult(result, timeProvider);
                    _syntaxReady.TrySetResult();

                    // A semantic join can race the end of syntax extraction. Only mark the syntax job completed
                    // while holding the same lock that TryJoin uses; otherwise a join could report success after
                    // this worker already skipped the semantic stages.
                    while (CurrentDepth() != IndexDepth.Semantic)
                    {
                        if (TryMarkSyntaxOnlyCompleted(timeProvider))
                        {
                            progress.Report(new IndexJobProgress(IndexPhase.Finalization, CompletedUnits: 1, TotalUnits: 1));
                            return;
                        }
                    }

                    progress.Report(new IndexJobProgress(IndexPhase.SemanticPreparation, CurrentItem: "semantic analysis requested"));
                    result = await executor.ExecuteAsync(
                        _jobId,
                        Request with { Depth = IndexDepth.Semantic, Force = false },
                        progress,
                        linked.Token);
                    UpdateResult(result, timeProvider);
                }

                progress.Report(new IndexJobProgress(IndexPhase.Finalization, CompletedUnits: 1, TotalUnits: 1));
                lock (_sync)
                {
                    _state = IndexJobState.Completed;
                    _currentItem = null;
                    _storage = ReadStorage(Request.Root);
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                lock (_sync)
                {
                    _state = IndexJobState.Cancelled;
                    _currentItem = null;
                    _storage = ReadStorage(Request.Root);
                }
                _syntaxReady.TrySetResult();
            }
            catch (IndexJobValidationException ex)
            {
                lock (_sync)
                {
                    _state = IndexJobState.Failed;
                    _currentItem = null;
                    _errorCode = "index_invalid_request";
                    _errorMessage = ex.Message;
                    _storage = ReadStorage(Request.Root);
                }
                _syntaxReady.TrySetResult();
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _state = IndexJobState.Failed;
                    _currentItem = null;
                    _errorCode = "index_failed";
                    _errorMessage = ex.Message;
                    _storage = ReadStorage(Request.Root);
                }
                _syntaxReady.TrySetResult();
            }
            finally
            {
                _syntaxReady.TrySetResult();
                _cancellation.Dispose();
            }
        }

        private void UpdateProgress(IndexJobProgress update, TimeProvider timeProvider)
        {
            lock (_sync)
            {
                var currentRank = PhaseRank(_phase);
                var updateRank = PhaseRank(update.Phase);
                if (updateRank < currentRank)
                    return;

                if (_phase != update.Phase)
                {
                    _phase = update.Phase;
                    _completedUnits = 0;
                    _totalUnits = null;
                    _phaseStartedTimestamp = timeProvider.GetTimestamp();
                    _completedAtPhaseStart = 0;
                }

                _completedUnits = Math.Max(_completedUnits, update.CompletedUnits);
                _totalUnits = update.TotalUnits is null
                    ? _totalUnits
                    : Math.Max(_totalUnits ?? 0, update.TotalUnits.Value);
                _currentItem = update.CurrentItem;
                if (!string.IsNullOrWhiteSpace(update.Warning) && _warnings.Count < MaxWarnings)
                    _warnings.Add(update.Warning);
            }
        }

        private static int PhaseRank(IndexPhase phase) => phase switch
        {
            IndexPhase.Inventory => 1,
            IndexPhase.SyntaxExtraction => 2,
            IndexPhase.SyntaxPersistence => 3,
            IndexPhase.SemanticPreparation => 4,
            IndexPhase.SemanticExtraction => 5,
            IndexPhase.SemanticPersistence => 6,
            IndexPhase.Finalization => 7,
            _ => 0,
        };

        private void UpdateResult(SemanticIndexResult result, TimeProvider timeProvider)
        {
            lock (_sync)
            {
                _counts = IndexCountSnapshot.From(result);
                foreach (var diagnostic in result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning))
                {
                    if (_warnings.Count == MaxWarnings)
                        break;
                    _warnings.Add($"{diagnostic.Code}: {diagnostic.Message}");
                }
            }
        }

        private IndexJobStartResult Conflict(TimeProvider timeProvider)
        {
            var snapshot = SnapshotCore(timeProvider) with
            {
                ErrorCode = "index_job_conflict",
                ErrorMessage = $"Index job {_jobId} is active. Run 'fuse index cancel \"{Request.Root}\"' before forcing a new index.",
            };
            return new IndexJobStartResult(snapshot, Joined: false, Conflict: true);
        }

        private IndexDepth CurrentDepth()
        {
            lock (_sync)
                return _targetDepth;
        }

        private bool TryMarkSyntaxOnlyCompleted(TimeProvider timeProvider)
        {
            lock (_sync)
            {
                if (_targetDepth != IndexDepth.Syntax)
                    return false;

                _state = IndexJobState.Completed;
                _currentItem = null;
                _storage = ReadStorage(Request.Root);
                return true;
            }
        }

        private IndexJobSnapshot SnapshotCore(TimeProvider timeProvider)
        {
            var phaseCount = _targetDepth == IndexDepth.Semantic ? 7 : 4;
            var phaseNumber = _phase switch
            {
                IndexPhase.Inventory => 1,
                IndexPhase.SyntaxExtraction => 2,
                IndexPhase.SyntaxPersistence => 3,
                IndexPhase.SemanticPreparation => 4,
                IndexPhase.SemanticExtraction => 5,
                IndexPhase.SemanticPersistence => 6,
                IndexPhase.Finalization => phaseCount,
                _ => 1,
            };
            if (_targetDepth == IndexDepth.Syntax && phaseNumber > 3)
                phaseNumber = phaseCount;

            double? percent = _totalUnits is > 0
                ? Math.Clamp(_completedUnits * 100d / _totalUnits.Value, 0d, 100d)
                : null;
            TimeSpan? eta = null;
            if (_totalUnits is > 0 && _completedUnits >= 5)
            {
                var elapsed = timeProvider.GetElapsedTime(_phaseStartedTimestamp);
                var perUnit = elapsed.TotalMilliseconds / Math.Max(1, _completedUnits - _completedAtPhaseStart);
                eta = TimeSpan.FromMilliseconds(Math.Max(0, (_totalUnits.Value - _completedUnits) * perUnit));
            }

            return new IndexJobSnapshot(
                _jobId,
                Request.Root,
                _state,
                _phase,
                phaseNumber,
                phaseCount,
                _completedUnits,
                _totalUnits,
                percent,
                eta,
                _currentItem,
                _startedAt,
                timeProvider.GetUtcNow() - _startedAt,
                _counts,
                _storage,
                _warnings.ToArray(),
                _errorCode,
                _errorMessage);
        }

        private static bool SameCapture(string? first, string? second) =>
            string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

        private static IndexStorageSnapshot ReadStorage(string root)
        {
            try
            {
                var database = FuseStorePaths.ResolveDatabasePath(root);
                var wal = database + "-wal";
                var sharedMemory = database + "-shm";
                var fuseDirectory = Path.GetDirectoryName(database)!;
                return new IndexStorageSnapshot(
                    SizeOf(database),
                    SizeOf(wal),
                    SizeOf(sharedMemory),
                    Directory.Exists(fuseDirectory)
                        ? Directory.EnumerateFiles(fuseDirectory, "*", SearchOption.TopDirectoryOnly).Sum(SizeOf)
                        : 0);
            }
            catch (IOException)
            {
                return IndexStorageSnapshot.Empty;
            }
        }

        private static long SizeOf(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
    }
}
