using System.Text;
using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     Extracts and persists syntax-tier file, symbol, search, and route facts. The stage commits bounded batches
///     and leaves completed work durable so a cancelled repository job resumes from the current inventory.
/// </summary>
internal sealed class SyntaxIndexStage
{
    private const int ExtractionParallelism = 4;
    private const string PendingBatchMetaKey = "pending_syntax_batch";
    private readonly LanguageSyntaxProviderRegistry _providers;
    private readonly SyntaxSymbolExtractor _syntaxSymbols;
    private readonly SyntaxRouteExtractor _routeExtractor;

    internal SyntaxIndexStage(
        LanguageSyntaxProviderRegistry providers,
        SyntaxSymbolExtractor syntaxSymbols,
        SyntaxRouteExtractor routeExtractor)
    {
        _providers = providers;
        _syntaxSymbols = syntaxSymbols;
        _routeExtractor = routeExtractor;
    }

    internal async Task<int> ReindexFileAsync(
        string root,
        IndexedFileRecord file,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken)
    {
        await store.DeleteFileDataAsync(file.NormalizedPath, cancellationToken);
        var provider = _providers.ForExtension(file.Extension);
        await store.UpsertFilesAsync([file with { Language = provider?.Language }], cancellationToken);
        if (provider is null || file.DetailLevel == IndexDetailLevel.InventoryOnly)
            return 0;

        var content = await File.ReadAllTextAsync(Path.Combine(root, file.Path), cancellationToken);
        var extracted = provider.Extract(file.NormalizedPath, content);
        await store.UpsertSymbolsAsync(extracted.Symbols, cancellationToken);
        await store.UpsertChunksAsync(RetainChunksForDetail(file, extracted.Chunks).ToList(), cancellationToken);
        if (string.Equals(file.Extension, ".cs", StringComparison.OrdinalIgnoreCase))
            await store.UpsertRoutesAsync(_routeExtractor.Extract(file.NormalizedPath, content), cancellationToken);
        return extracted.Symbols.Count;
    }

    internal async Task<SemanticIndexResult> IndexChunkedAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken,
        IProgress<SemanticIndexProgress>? progress)
    {
        await store.ReplaceTfmAvailabilityAsync([], cancellationToken);
        var taggedFiles = files
            .Select(file => file with { Language = _providers.ForExtension(file.Extension)?.Language })
            .ToList();
        for (var index = 0; index < taggedFiles.Count; index += SemanticIndexer.UpgradeCommitFileBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.UpsertFilesAsync(
                taggedFiles.Skip(index).Take(SemanticIndexer.UpgradeCommitFileBatchSize).ToList(),
                cancellationToken);
        }

        var perFile = new (List<SymbolRecord> Symbols, List<ChunkRecord> Chunks, List<RouteRecord> Routes)?[files.Count];
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = ExtractionParallelism,
        };
        var completedFiles = 0;
        progress?.Report(new SemanticIndexProgress(
            SemanticIndexStage.SyntaxExtraction,
            0,
            files.Count,
            "extracting source declarations"));
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), parallelOptions, async (index, token) =>
        {
            var file = files[index];
            if (file.DetailLevel == IndexDetailLevel.InventoryOnly)
                return;
            var provider = _providers.ForExtension(file.Extension);
            if (provider is null)
                return;

            var content = await File.ReadAllTextAsync(Path.Combine(root, file.Path), token);
            var extracted = provider.Extract(file.NormalizedPath, content);
            var routes = file.Extension == ".cs"
                ? _routeExtractor.Extract(file.NormalizedPath, content).ToList()
                : [];
            perFile[index] = (
                extracted.Symbols.ToList(),
                RetainChunksForDetail(file, extracted.Chunks).ToList(),
                routes);
            var completed = Interlocked.Increment(ref completedFiles);
            progress?.Report(new SemanticIndexProgress(
                SemanticIndexStage.SyntaxExtraction,
                completed,
                files.Count,
                file.NormalizedPath));
        });

        var symbols = new List<SymbolRecord>();
        var chunks = new List<ChunkRecord>();
        var routes = new List<RouteRecord>();
        foreach (var entry in perFile)
        {
            if (entry is not { } extracted)
                continue;
            symbols.AddRange(extracted.Symbols);
            chunks.AddRange(extracted.Chunks);
            routes.AddRange(extracted.Routes);
        }

        await store.UpsertSymbolsAsync(symbols, cancellationToken);
        for (var index = 0; index < chunks.Count; index += SemanticIndexer.UpgradeCommitFileBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = chunks.Skip(index).Take(SemanticIndexer.UpgradeCommitFileBatchSize).ToList();
            if (batch.Count > 0)
                await store.UpsertChunksAsync(batch, cancellationToken);
        }

        await store.UpsertRoutesAsync(routes, cancellationToken);
        progress?.Report(new SemanticIndexProgress(
            SemanticIndexStage.SyntaxPersistence,
            files.Count,
            files.Count,
            "syntax facts persisted"));
        return new SemanticIndexResult(
            "syntax",
            files.Count,
            0,
            symbols.Count,
            chunks.Count,
            routes.Count,
            snapshot.Diagnostics);
    }

    internal async Task<(List<ChunkRecord> Chunks, List<RouteRecord> Routes)> ExtractChunksAndRoutesChunkedAsync(
        IWorkspaceIndexStore store,
        string root,
        IReadOnlyList<IndexedFileRecord> files,
        bool dropChunkSymbolIds,
        CancellationToken cancellationToken)
    {
        var allChunks = new List<ChunkRecord>();
        var allRoutes = new List<RouteRecord>();
        for (var index = 0; index < files.Count; index += SemanticIndexer.UpgradeCommitFileBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = files.Skip(index).Take(SemanticIndexer.UpgradeCommitFileBatchSize).ToList();
            var (chunks, routes) = await ExtractChunksAndRoutesAsync(root, batch, dropChunkSymbolIds, cancellationToken);
            allChunks.AddRange(chunks);
            allRoutes.AddRange(routes);
            if (chunks.Count > 0)
                await store.UpsertChunksAsync(chunks, cancellationToken);
        }

        return (allChunks, allRoutes);
    }

    internal async Task<SemanticIndexResult> IndexIncrementallyAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken,
        IProgress<SemanticIndexProgress>? progress)
    {
        var stored = await store.GetAllFileHashesAsync(cancellationToken);
        var pendingPaths = await ReadPendingPathsAsync(store, cancellationToken);
        var currentPaths = files.Select(file => file.NormalizedPath).ToHashSet(StringComparer.Ordinal);
        var changed = files
            .Where(file => !stored.TryGetValue(file.NormalizedPath, out var storedHash)
                || !string.Equals(storedHash, file.ContentHash, StringComparison.Ordinal)
                || pendingPaths is null
                || pendingPaths.Contains(file.NormalizedPath))
            .OrderBy(file => file.NormalizedPath, StringComparer.Ordinal)
            .ToArray();
        var removed = stored.Keys
            .Where(path => !currentPaths.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var totalWork = changed.Length + removed.Length;
        var completedWork = 0;
        progress?.Report(new SemanticIndexProgress(
            SemanticIndexStage.SyntaxExtraction,
            0,
            totalWork,
            totalWork == 0 ? "source hashes are current" : "extracting changed source declarations"));
        var ftsReplacements = 0;
        foreach (var batch in BatchFiles(changed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.SetMetaAsync(
                PendingBatchMetaKey,
                EncodePendingPaths(batch.Select(file => file.NormalizedPath)),
                cancellationToken);
            await store.ClearFileDataAsync(batch.Select(file => file.NormalizedPath).ToArray(), cancellationToken);
            var batchResult = await IndexAllAsync(
                root,
                store,
                batch,
                snapshot,
                cancellationToken,
                resetTfmAvailability: false);
            ftsReplacements += batchResult.ChunkCount;
            completedWork += batch.Count;
            progress?.Report(new SemanticIndexProgress(
                SemanticIndexStage.SyntaxExtraction,
                completedWork,
                totalWork,
                batch[^1].NormalizedPath));
        }

        foreach (var path in removed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.DeleteFileAsync(path, cancellationToken);
            completedWork++;
            progress?.Report(new SemanticIndexProgress(
                SemanticIndexStage.SyntaxExtraction,
                completedWork,
                totalWork,
                path));
        }

        await store.SetMetaAsync(PendingBatchMetaKey, string.Empty, cancellationToken);
        await store.SetMetaAsync("last_index_file_upserts", changed.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        await store.SetMetaAsync("last_index_fts_replacements", ftsReplacements.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        progress?.Report(new SemanticIndexProgress(
            SemanticIndexStage.SyntaxPersistence,
            totalWork == 0 ? 1 : totalWork,
            totalWork == 0 ? 1 : totalWork,
            totalWork == 0 ? "no source rows changed" : "syntax facts persisted"));

        var state = await store.GetStateAsync(cancellationToken);
        var routeCount = await store.GetRouteCountAsync(cancellationToken);
        return new SemanticIndexResult(
            "syntax",
            state.FileCount,
            0,
            state.SymbolCount,
            state.ChunkCount,
            routeCount,
            snapshot.Diagnostics);
    }

    internal async Task<SemanticIndexResult> IndexAllAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken,
        bool resetTfmAvailability = true)
    {
        if (resetTfmAvailability)
            await store.ReplaceTfmAvailabilityAsync([], cancellationToken);
        var taggedFiles = files
            .Select(file => file with { Language = _providers.ForExtension(file.Extension)?.Language })
            .ToList();
        await store.UpsertFilesAsync(taggedFiles, cancellationToken);

        var perFile = new (List<SymbolRecord> Symbols, List<ChunkRecord> Chunks, List<RouteRecord> Routes)?[files.Count];
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = ExtractionParallelism,
        };
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), parallelOptions, async (index, token) =>
        {
            var file = files[index];
            if (file.DetailLevel == IndexDetailLevel.InventoryOnly)
                return;
            var provider = _providers.ForExtension(file.Extension);
            if (provider is null)
                return;

            var content = await File.ReadAllTextAsync(Path.Combine(root, file.Path), token);
            var extracted = provider.Extract(file.NormalizedPath, content);
            var routes = file.Extension == ".cs"
                ? _routeExtractor.Extract(file.NormalizedPath, content).ToList()
                : [];
            perFile[index] = (
                extracted.Symbols.ToList(),
                RetainChunksForDetail(file, extracted.Chunks).ToList(),
                routes);
        });

        var symbols = new List<SymbolRecord>();
        var chunks = new List<ChunkRecord>();
        var routes = new List<RouteRecord>();
        foreach (var entry in perFile)
        {
            if (entry is not { } extracted)
                continue;
            symbols.AddRange(extracted.Symbols);
            chunks.AddRange(extracted.Chunks);
            routes.AddRange(extracted.Routes);
        }

        await store.UpsertSymbolsAsync(symbols, cancellationToken);
        await store.UpsertChunksAsync(chunks, cancellationToken);
        await store.UpsertRoutesAsync(routes, cancellationToken);
        return new SemanticIndexResult(
            "syntax",
            files.Count,
            0,
            symbols.Count,
            chunks.Count,
            routes.Count,
            snapshot.Diagnostics);
    }

    internal async Task<(List<ChunkRecord> Chunks, List<RouteRecord> Routes)> ExtractChunksAndRoutesAsync(
        string root,
        IReadOnlyList<IndexedFileRecord> files,
        bool dropChunkSymbolIds,
        CancellationToken cancellationToken)
    {
        var chunks = new List<ChunkRecord>();
        var routes = new List<RouteRecord>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Extension != ".cs" || file.DetailLevel == IndexDetailLevel.InventoryOnly)
                continue;

            var content = await File.ReadAllTextAsync(Path.Combine(root, file.Path), cancellationToken);
            if (string.IsNullOrEmpty(content))
                continue;

            Microsoft.CodeAnalysis.SyntaxNode syntaxRoot;
            try
            {
                syntaxRoot = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(content).GetRoot();
            }
            catch
            {
                continue;
            }

            var extracted = _syntaxSymbols.Extract(file.NormalizedPath, syntaxRoot);
            foreach (var chunk in RetainChunksForDetail(file, extracted.Chunks))
                chunks.Add(dropChunkSymbolIds ? chunk with { SymbolId = null } : chunk);
            routes.AddRange(_routeExtractor.Extract(file.NormalizedPath, syntaxRoot));
        }

        return (chunks, routes);
    }

    private static async Task<IReadOnlySet<string>?> ReadPendingPathsAsync(
        IWorkspaceIndexMetadataStore store,
        CancellationToken cancellationToken)
    {
        var encoded = await store.GetMetaAsync(PendingBatchMetaKey, cancellationToken);
        if (string.IsNullOrEmpty(encoded))
            return new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return decoded.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string EncodePendingPaths(IEnumerable<string> paths) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\0', paths)));

    private static IEnumerable<IReadOnlyList<IndexedFileRecord>> BatchFiles(IReadOnlyList<IndexedFileRecord> files)
    {
        var batch = new List<IndexedFileRecord>(SemanticIndexer.SyntaxCommitFileBatchSize);
        long sourceBytes = 0;
        foreach (var file in files)
        {
            var exceedsFileLimit = batch.Count >= SemanticIndexer.SyntaxCommitFileBatchSize;
            var exceedsByteLimit = batch.Count > 0
                && sourceBytes + file.SizeBytes > SemanticIndexer.SyntaxCommitSourceBatchBytes;
            if (exceedsFileLimit || exceedsByteLimit)
            {
                yield return batch;
                batch = new List<IndexedFileRecord>(SemanticIndexer.SyntaxCommitFileBatchSize);
                sourceBytes = 0;
            }

            batch.Add(file);
            sourceBytes += file.SizeBytes;
        }

        if (batch.Count > 0)
            yield return batch;
    }

    private static IEnumerable<ChunkRecord> RetainChunksForDetail(
        IndexedFileRecord file,
        IEnumerable<ChunkRecord> chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return file.DetailLevel == IndexDetailLevel.Declarations
                ? chunk with
                {
                    Body = null,
                    Comments = null,
                    Signature = DeclarationOnlySignature(chunk.Signature),
                }
                : chunk;
        }
    }

    private static string? DeclarationOnlySignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return signature;
        var blockStart = signature.IndexOf('{');
        var expressionBodyStart = signature.IndexOf("=>", StringComparison.Ordinal);
        var end = blockStart switch
        {
            >= 0 when expressionBodyStart >= 0 => Math.Min(blockStart, expressionBodyStart),
            >= 0 => blockStart,
            _ => expressionBodyStart,
        };
        return end < 0 ? signature : signature[..end].TrimEnd();
    }
}
