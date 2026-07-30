using Fuse.Collection;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Workspace;

namespace Fuse.Cli.Services;

/// <summary>
///     The concrete <see cref="IResidentWorkspaceProvider" /> backing a live resident workspace for one repository
///     root (S1): it answers the availability description and resident-grade overlay checks the read tools consult,
///     and it applies watcher batches to keep the held compilations current. This is the provider the serve/host
///     places this service in the host-owned MCP runtime; constructing the workspace and subscribing it to the file
///     watcher is the serve wiring that uses this service.
/// </summary>
/// <remarks>
///     The service owns the <see cref="ResidentWorkspace" /> and disposes it. It tracks a revision that increments
///     on each applied batch, reported as the availability stamp so a client can see the resident state advance.
///     Applying a batch reads file content from disk but never writes the tree (the updater's contract).
/// </remarks>
public sealed class ResidentWorkspaceService : IResidentWorkspaceProvider, IDisposable
{
    private readonly string _root;
    private readonly ResidentWorkspace _workspace;
    private readonly ResidentWorkspaceUpdater _updater = new();
    private readonly FileHashService _hashes = new();
    private readonly GitFileEnumerator _gitFiles = new();
    private readonly object _gate = new();
    private int _revision;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ResidentWorkspaceService" /> class.
    /// </summary>
    /// <param name="root">The absolute repository root this service serves.</param>
    /// <param name="workspace">The live resident workspace; the service takes ownership and disposes it.</param>
    public ResidentWorkspaceService(string root, ResidentWorkspace workspace)
    {
        _root = System.IO.Path.GetFullPath(root);
        _workspace = workspace;
    }

    /// <inheritdoc />
    public ResidentStatus? DescribeResident(string root)
    {
        if (!Matches(root))
            return null;
        lock (_gate)
            return new ResidentStatus(_workspace.Projects.Count, $"revision {_revision}");
    }

    /// <inheritdoc />
    public IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
        string root, string relativeFilePath, string newContent, CancellationToken cancellationToken)
    {
        if (!Matches(root))
            return null;
        lock (_gate)
            return _workspace.CheckOverlay(relativeFilePath, newContent, cancellationToken);
    }

    /// <inheritdoc />
    public IReadOnlyList<CheckDiagnostic>? TryGetCurrentDiagnostics(string root, CancellationToken cancellationToken)
    {
        if (!Matches(root))
            return null;
        lock (_gate)
            return _workspace.GetDiagnostics(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CheckDiagnostic>?> TryCheckOverlayAsync(
        string root, string relativeFilePath, string newContent, bool includeAnalyzers, CancellationToken cancellationToken)
    {
        if (!Matches(root))
            return null;
        // The overlay forks the held compilation and (optionally) runs analyzers off the shared state; the resident
        // workspace itself is not mutated, so this does not need the write gate the batch/projection paths take.
        return await _workspace.CheckOverlayAsync(relativeFilePath, newContent, includeAnalyzers, cancellationToken);
    }

    /// <inheritdoc />
    public IReadOnlyList<ResidentSignature>? TryGetSignature(
        string root, string symbolName, int limitPerName, CancellationToken cancellationToken)
    {
        if (!Matches(root))
            return null;
        lock (_gate)
            return _workspace.GetSignatures(symbolName, limitPerName, cancellationToken);
    }

    /// <summary>
    ///     Applies a coalesced watcher batch to the held workspace, advancing the revision.
    /// </summary>
    /// <param name="batch">The file changes from the watcher.</param>
    /// <param name="cancellationToken">A token to cancel the update.</param>
    /// <returns>The update result (applied, added, removed, skipped).</returns>
    public ResidentUpdateResult ApplyBatch(IReadOnlyList<WorkspaceFileChange> batch, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var result = _updater.Apply(_workspace, batch, cancellationToken);
            if (result.Applied + result.Added + result.Removed > 0)
                _revision++;
            return result;
        }
    }

    /// <summary>
    ///     Projects the resident state of the projects touched by a set of changed files into the store (S1 step
    ///     4), so a symbol or edge the edit introduced or removed is queryable through the store-backed read tools
    ///     without a full re-index. Maps each changed C# file to the held compilation that contains it, then
    ///     re-projects each affected project via <see cref="SemanticIndexer.ProjectFromCompilationsAsync" />.
    /// </summary>
    /// <remarks>
    ///     This is a store-write: per the single-writer invariant only the serve watcher for this root calls it,
    ///     and the resident read path skips the N6 reconcile so the two do not both write the store. It reads file
    ///     content from disk (chunk extraction), so the edit must already be applied to the resident state and to
    ///     disk.
    /// </remarks>
    /// <param name="indexer">The semantic indexer providing the projection.</param>
    /// <param name="store">The index store to project into.</param>
    /// <param name="changedAbsolutePaths">The absolute paths of the changed files.</param>
    /// <param name="cancellationToken">A token to cancel the projection.</param>
    /// <returns>The number of projects re-projected (zero when no changed file maps to a held project).</returns>
    public async Task<int> ProjectChangedAsync(
        SemanticIndexer indexer, IWorkspaceIndexStore store, IReadOnlyList<string> changedAbsolutePaths, CancellationToken cancellationToken)
    {
        List<ResidentProject> affected;
        lock (_gate)
        {
            var byPath = new Dictionary<string, ResidentProject>(StringComparer.OrdinalIgnoreCase);
            foreach (var changed in changedAbsolutePaths)
            {
                if (!changed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    continue;
                var normalizedChanged = System.IO.Path.GetFullPath(changed).Replace('\\', '/');
                foreach (var project in _workspace.Projects)
                {
                    if (project.Compilation.SyntaxTrees.Any(t =>
                        t.FilePath.Replace('\\', '/').Equals(normalizedChanged, StringComparison.OrdinalIgnoreCase)))
                    {
                        byPath[project.ProjectFilePath] = project;
                        break;
                    }
                }
            }

            affected = byPath.Values.ToList();
        }

        if (affected.Count == 0)
            return 0;

        var compilations = affected
            .Select(p => (p.ProjectFilePath, p.Compilation))
            .ToList();
        var excludedDirectories = new HashSet<string>(
            WorkspaceExclusions.LoadDirectoryNames(_root),
            StringComparer.OrdinalIgnoreCase);
        var sourcePaths = affected
            .SelectMany(p => p.Compilation.SyntaxTrees)
            .Select(t => t.FilePath)
            .Where(path => IsIndexableSourceFile(path, excludedDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var gitInventory = await _gitFiles.TryDescribeAsync(_root, cancellationToken);
        var files = new List<IndexedFileRecord>(sourcePaths.Count);
        foreach (var sourcePath in sourcePaths)
            files.Add(await ToFileRecordAsync(sourcePath, gitInventory, cancellationToken));

        await indexer.ProjectFromCompilationsAsync(_root, store, compilations, files, cancellationToken);
        return affected.Count;
    }

    private bool IsIndexableSourceFile(string path, IReadOnlySet<string> excludedDirectories)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return false;

        var relative = System.IO.Path.GetRelativePath(_root, System.IO.Path.GetFullPath(path));
        if (System.IO.Path.IsPathRooted(relative)
            || string.Equals(relative, "..", StringComparison.Ordinal)
            || relative.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{System.IO.Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }

        return !relative.Split(
                [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(excludedDirectories.Contains);
    }

    // Builds a minimal file record for a resident source file. Its identity must follow WorkspaceFileScanner:
    // clean tracked files use Git blob ids, while dirty and untracked files use streaming SHA-256. Otherwise a
    // resident projection makes every clean file look dirty to the next N6 reconcile.
    private async Task<IndexedFileRecord> ToFileRecordAsync(
        string absolutePath,
        GitWorkspaceInventory? gitInventory,
        CancellationToken cancellationToken)
    {
        var normalized = System.IO.Path.GetRelativePath(_root, absolutePath).Replace('\\', '/');
        var info = new FileInfo(absolutePath);
        var cleanBlobId = gitInventory?.GetCleanBlobId(normalized);
        var contentHash = !info.Exists
            ? string.Empty
            : cleanBlobId is null
                ? await _hashes.ComputeSha256FileAsync(absolutePath, cancellationToken)
                : $"git:{cleanBlobId}";
        return new IndexedFileRecord(
            Path: absolutePath,
            NormalizedPath: normalized,
            Extension: ".cs",
            SizeBytes: info.Exists ? info.Length : 0,
            MtimeUtcTicks: info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
            ContentHash: contentHash);
    }

    private bool Matches(string root) =>
        string.Equals(System.IO.Path.GetFullPath(root), _root, StringComparison.OrdinalIgnoreCase);

    /// <summary>Disposes the held resident workspace.</summary>
    public void Dispose() => _workspace.Dispose();
}
