using System.Collections.Concurrent;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection;

/// <summary>
///     Enumerates and filters files from the file system based on collection options.
/// </summary>
/// <remarks>
///     Applies all registered <see cref="IFileFilter" /> implementations in registration order.
///     Results are sorted by normalized relative path for deterministic ordering.
/// </remarks>
public sealed class FileCollectionPipeline
{
    private readonly IFileSystem _fileSystem;
    private readonly GitIgnoreParser _gitIgnoreParser;
    private readonly IReadOnlyList<IFileFilter> _filters;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FileCollectionPipeline" /> class.
    /// </summary>
    /// <param name="fileSystem">The file system used to enumerate files.</param>
    /// <param name="gitIgnoreParser">The parser used to load <c>.gitignore</c> patterns.</param>
    /// <param name="filters">The filters to apply in registration order.</param>
    public FileCollectionPipeline(
        IFileSystem fileSystem,
        GitIgnoreParser gitIgnoreParser,
        IEnumerable<IFileFilter> filters)
    {
        _fileSystem = fileSystem;
        _gitIgnoreParser = gitIgnoreParser;
        _filters = filters.ToList();
    }

    /// <summary>
    ///     Collects all files matching the specified options using the processor count for parallelism.
    /// </summary>
    /// <param name="options">The collection options for the current run.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     A <see cref="CollectionResult" /> containing included files sorted by path
    ///     and the number of candidates evaluated.
    /// </returns>
    public Task<CollectionResult> CollectAsync(
        CollectionOptions options,
        CancellationToken cancellationToken = default) =>
        CollectAsync(options, Environment.ProcessorCount, cancellationToken);

    /// <summary>
    ///     Collects all files matching the specified options.
    /// </summary>
    /// <param name="options">The collection options for the current run.</param>
    /// <param name="parallelism">The maximum degree of parallelism for candidate evaluation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     A <see cref="CollectionResult" /> containing included files sorted by path
    ///     and the number of candidates evaluated.
    /// </returns>
    public async Task<CollectionResult> CollectAsync(
        CollectionOptions options,
        int parallelism,
        CancellationToken cancellationToken = default)
    {
        // Explicit-file mode (the reduce path): collect exactly the caller-supplied paths and skip enumeration
        // and filters. The caller chose these files deliberately, so include/exclude rules do not apply.
        if (options.ExplicitFiles is { Count: > 0 })
            return CollectExplicitFiles(options);

        var searchOption = options.Recursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        var gitIgnorePatterns = options.RespectGitIgnore
            ? await _gitIgnoreParser.ParseAsync(options.SourceDirectory, cancellationToken)
            : [];

        // Build a run-local filter list, substituting a fresh GitIgnoreFilter that carries this run's patterns
        // for the shared placeholder. The pipeline is effectively a singleton (a transient captured by the
        // singleton orchestrator), so mutating a shared filter's pattern set would let concurrent runs against
        // different repositories race; every other filter is stateless and is reused as-is.
        var runFilters = new IFileFilter[_filters.Count];
        for (var i = 0; i < _filters.Count; i++)
        {
            runFilters[i] = _filters[i] is GitIgnoreFilter
                ? new GitIgnoreFilter(gitIgnorePatterns)
                : _filters[i];
        }

        var rootDirectory = Path.GetFullPath(options.SourceDirectory);

        // Skip file reparse points (symlinks) during enumeration; one extra stat per candidate. A file reached by
        // descending through a symlinked or junctioned directory also escapes the root even though its lexical
        // path looks under root, so reject any file with a reparse-point ancestor directory between it and the
        // root (cached per directory, so each is stat'd at most once).
        var reparseDirectoryCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var nestedRepositoryCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // A caller-supplied candidate set (for example a git-native file listing) replaces the directory walk but
        // still passes every filter below. Its relative entries resolve against the root; only existing files
        // survive, so a stale listing degrades the set rather than throwing.
        var candidateSource = options.CandidateFiles is not null
            ? options.CandidateFiles
                .Select(path => Path.IsPathRooted(path) ? path : Path.Combine(rootDirectory, path))
                .Where(path => _fileSystem.GetFileInfo(path).Exists)
            : _fileSystem.EnumerateFiles(options.SourceDirectory, "*.*", searchOption);

        var filePaths = candidateSource
            .Select(Path.GetFullPath)
            .Where(path => IsPathUnderRoot(rootDirectory, path))
            .Where(path => !HasReparsePointAttribute(_fileSystem.GetFileInfo(path)))
            .Where(path => !CrossesReparsePointDirectory(rootDirectory, path, reparseDirectoryCache))
            .Where(path => !options.ExcludeNestedRepositories
                           || !CrossesNestedRepository(rootDirectory, path, nestedRepositoryCache))
            .Select((path, index) => (path, index))
            .ToArray();

        var includedFiles = new ConcurrentBag<(int Index, SourceFile File)>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(filePaths, parallelOptions, (item, _) =>
        {
            var candidate = new FileCandidate(
                item.path,
                _fileSystem.GetRelativePath(options.SourceDirectory, item.path),
                _fileSystem.GetFileInfo(item.path));

            if (runFilters.All(filter => filter.Include(candidate, options)))
                includedFiles.Add((item.index, new SourceFile(candidate)));

            return ValueTask.CompletedTask;
        });

        var sortedFiles = includedFiles
            .OrderBy(entry => entry.Index)
            .Select(entry => entry.File)
            .OrderBy(file => file.NormalizedRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // (explicit-file mode returns earlier; see CollectExplicitFiles)

        return new CollectionResult(sortedFiles, filePaths.Length);
    }

    // Builds source files from a caller-supplied path list, bypassing directory enumeration and the filters.
    // Paths are resolved relative to SourceDirectory when not rooted; paths outside the normalized root and
    // missing paths are skipped so a stale or out-of-scope entry degrades the set rather than failing the run.
    // Input order is preserved so the caller controls emission order.
    private CollectionResult CollectExplicitFiles(CollectionOptions options)
    {
        var files = new List<SourceFile>();
        var candidateCount = 0;
        var rootDirectory = Path.GetFullPath(options.SourceDirectory);

        foreach (var raw in options.ExplicitFiles!)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            candidateCount++;
            var fullPath = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(rootDirectory, raw));

            if (!IsPathUnderRoot(rootDirectory, fullPath))
                continue;

            var fileInfo = _fileSystem.GetFileInfo(fullPath);
            if (!fileInfo.Exists)
                continue;

            var relativePath = _fileSystem.GetRelativePath(rootDirectory, fullPath);
            files.Add(new SourceFile(new FileCandidate(fullPath, relativePath, fileInfo)));
        }

        return new CollectionResult(files, candidateCount);
    }

    // Rejects resolved paths that escape the collection root via ".." segments or point at a different drive.
    private static bool IsPathUnderRoot(string rootDirectory, string fullPath)
    {
        var relativePath = Path.GetRelativePath(rootDirectory, fullPath);
        return !relativePath.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);
    }

    // True when any ancestor directory strictly between the file and the collection root is a nested version-
    // control root (a git worktree, submodule, or embedded clone). Such a file belongs to a different checkout
    // and must not be collected as part of this workspace. Mirrors the reparse-point walk: the root's own .git is
    // never considered (the loop stops above the root), and each directory is stat'd at most once via the cache.
    private static bool CrossesNestedRepository(string rootDirectory, string filePath, Dictionary<string, bool> cache)
    {
        var directory = Path.GetDirectoryName(filePath);
        while (directory is not null
               && directory.Length > rootDirectory.Length
               && directory.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (!cache.TryGetValue(directory, out var isRepo))
            {
                isRepo = WorkspaceExclusions.IsVcsRoot(directory);
                cache[directory] = isRepo;
            }

            if (isRepo)
                return true;

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    // Directory enumeration can follow junctions and symlinks; skip reparse-point entries themselves.
    private static bool HasReparsePointAttribute(FileInfo fileInfo) =>
        fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);

    // True when any ancestor directory strictly between the file and the collection root is a reparse point (a
    // directory symlink or junction). Such a file was reached by descending through the link, so it escapes the
    // root even though its lexical path is under it. Cached per directory so each is stat'd at most once; a
    // non-existent or inaccessible directory is treated as not-a-reparse-point, which leaves an in-memory or fake
    // file system (whose paths are not on disk) unaffected.
    private static bool CrossesReparsePointDirectory(string rootDirectory, string filePath, Dictionary<string, bool> cache)
    {
        var directory = Path.GetDirectoryName(filePath);
        while (directory is not null
               && directory.Length > rootDirectory.Length
               && directory.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (!cache.TryGetValue(directory, out var isReparse))
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    isReparse = info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    isReparse = false;
                }

                cache[directory] = isReparse;
            }

            if (isReparse)
                return true;

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }
}
