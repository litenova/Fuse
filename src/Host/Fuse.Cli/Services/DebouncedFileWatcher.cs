namespace Fuse.Cli.Services;

/// <summary>
///     Watches a directory tree and raises a single debounced notification after a burst of filesystem events
///     settles.
/// </summary>
/// <remarks>
///     Wraps a <see cref="FileSystemWatcher" /> over file name, last-write, and size changes. Events inside any
///     <c>.fuse</c> or <c>.git</c> directory are ignored. Index inventory invokes Git commands that can update Git
///     metadata; observing those writes would schedule another index job after every completed refresh. Each new
///     event cancels the pending debounce timer and restarts it, so <see cref="Changed" /> fires once per quiet
///     interval rather than once per raw event.
/// </remarks>
public sealed class DebouncedFileWatcher : IResidentBatchWatcher
{
    private const int DefaultDebounceMilliseconds = 500;

    private readonly FileSystemWatcher _watcher;
    private readonly int _debounceMilliseconds;
    private readonly CancellationToken _cancellationToken;
    private readonly WorkspaceFileChangeSet _changes = new();
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DebouncedFileWatcher" /> class.
    /// </summary>
    /// <param name="rootDirectory">The directory to watch.</param>
    /// <param name="recursive">Whether to include subdirectories.</param>
    /// <param name="debounceMilliseconds">Debounce delay after the last filesystem event.</param>
    /// <param name="cancellationToken">Token that cancels pending debounce work and is passed to <see cref="Changed" /> handlers.</param>
    public DebouncedFileWatcher(
        string rootDirectory,
        bool recursive,
        int debounceMilliseconds = DefaultDebounceMilliseconds,
        CancellationToken cancellationToken = default)
    {
        _debounceMilliseconds = debounceMilliseconds;
        _cancellationToken = cancellationToken;
        _watcher = new FileSystemWatcher(Path.GetFullPath(rootDirectory))
        {
            IncludeSubdirectories = recursive,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnRenamed;
    }

    /// <summary>
    ///     Raised on a background thread after filesystem changes settle for the debounce interval. The handler
    ///     receives a cancellation token and may run asynchronously.
    /// </summary>
    public event Func<CancellationToken, Task>? Changed;

    /// <summary>
    ///     Raised alongside <see cref="Changed" /> after changes settle, carrying the coalesced net change per
    ///     path over the debounce window (S1 step 3): the resident workspace applies one update per file from
    ///     this list. A rename is reported as a delete of the old path and a create of the new.
    /// </summary>
    public event Func<IReadOnlyList<WorkspaceFileChange>, CancellationToken, Task>? BatchChanged;

    /// <summary>
    ///     Stops watching, disposes the underlying <see cref="FileSystemWatcher" />, and cancels any pending
    ///     debounce timer.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        CancelDebounce();
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnorePath(e.FullPath))
            return;

        _changes.Add(ToChangeKind(e.ChangeType), e.FullPath);
        ScheduleDebounce();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnorePath(e.FullPath) && ShouldIgnorePath(e.OldFullPath))
            return;

        // A rename is a delete of the old path and a create of the new, so a resident update removes the old
        // document and adds the new one.
        if (!ShouldIgnorePath(e.OldFullPath))
            _changes.Add(FileChangeKind.Deleted, e.OldFullPath);
        if (!ShouldIgnorePath(e.FullPath))
            _changes.Add(FileChangeKind.Created, e.FullPath);
        ScheduleDebounce();
    }

    private static FileChangeKind ToChangeKind(WatcherChangeTypes changeType) => changeType switch
    {
        WatcherChangeTypes.Created => FileChangeKind.Created,
        WatcherChangeTypes.Deleted => FileChangeKind.Deleted,
        _ => FileChangeKind.Changed,
    };

    private void ScheduleDebounce()
    {
        CancelDebounce();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceMilliseconds, cts.Token);
                if (_cancellationToken.IsCancellationRequested)
                    return;

                // Drain the coalesced batch once and hand it to the batch handler; the path-less Changed handler
                // fires too, so existing consumers (whole-workspace re-index) are unaffected.
                var batch = _changes.Drain();
                if (BatchChanged is not null && batch.Count > 0)
                    await BatchChanged(batch, _cancellationToken);

                if (Changed is not null)
                    await Changed(_cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }, _cancellationToken);
    }

    private void CancelDebounce()
    {
        if (_debounceCts is null)
            return;

        _debounceCts.Cancel();
        _debounceCts.Dispose();
        _debounceCts = null;
    }

    private static bool ShouldIgnorePath(string path)
    {
        return IsPathInDirectory(path, ".fuse") || IsPathInDirectory(path, ".git");
    }

    private static bool IsPathInDirectory(string path, string directoryName)
    {
        var segment = $"{Path.DirectorySeparatorChar}{directoryName}{Path.DirectorySeparatorChar}";
        return path.Contains(segment, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith($"{Path.DirectorySeparatorChar}{directoryName}", StringComparison.OrdinalIgnoreCase);
    }
}
