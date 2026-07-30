using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     Persists compiler-backed workspace facts after syntax inventory is available.
/// </summary>
internal sealed class SemanticIndexWriter
{
    private readonly SemanticGraphExtractor _graph;
    private readonly SyntaxIndexStage _syntaxStage;
    private readonly LanguageSyntaxProviderRegistry _syntaxProviders;

    internal SemanticIndexWriter(
        SemanticGraphExtractor graph,
        SyntaxIndexStage syntaxStage,
        LanguageSyntaxProviderRegistry syntaxProviders)
    {
        _graph = graph;
        _syntaxStage = syntaxStage;
        _syntaxProviders = syntaxProviders;
    }

    internal async Task<SemanticIndexResult> WriteSemanticAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await store.ReplaceTfmAvailabilityAsync([], cancellationToken);
        var projects = _graph.BuildProjectRecords(snapshot, cancellationToken);
        await store.UpsertProjectsAsync(projects, cancellationToken);

        var linkedFiles = LinkFiles(files, SemanticGraphExtractor.BuildFileProjectMap(root, snapshot));
        await store.UpsertFilesAsync(linkedFiles, cancellationToken);

        var symbols = new List<SymbolRecord>();
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            symbols.AddRange(_graph.ExtractSymbols(project, root, cancellationToken));
        }

        await store.UpsertSymbolsAsync(symbols, cancellationToken);
        var (chunks, syntaxRoutes) = await _syntaxStage.ExtractChunksAndRoutesAsync(
            root, files, dropChunkSymbolIds: true, cancellationToken);
        await store.UpsertChunksAsync(chunks, cancellationToken);
        await store.UpsertRoutesAsync(syntaxRoutes, cancellationToken);

        var graph = _graph.AnalyzeWorkspace(root, snapshot, cancellationToken);
        await store.UpsertNodesAsync(graph.Nodes, cancellationToken);
        await store.UpsertEdgesAsync(graph.Edges, cancellationToken);
        await store.UpsertRoutesAsync(graph.Routes, cancellationToken);
        await store.UpsertDiRegistrationsAsync(graph.DiRegistrations, cancellationToken);
        await store.UpsertOptionsBindingsAsync(graph.OptionsBindings, cancellationToken);

        var diagnostics = snapshot.Diagnostics.Concat(graph.Diagnostics).ToList();
        var mode = HasLoadWarnings(snapshot) ? "partial" : "semantic";
        return new SemanticIndexResult(
            mode,
            linkedFiles.Count,
            projects.Count,
            symbols.Count,
            chunks.Count,
            syntaxRoutes.Count + graph.Routes.Count,
            diagnostics);
    }

    internal async Task<SemanticIndexResult> WriteSemanticChunkedAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken,
        IProgress<SemanticIndexProgress>? progress)
    {
        await store.ReplaceTfmAvailabilityAsync([], cancellationToken);
        var projects = _graph.BuildProjectRecords(snapshot, cancellationToken);
        await store.UpsertProjectsAsync(projects, cancellationToken);

        var linkedFiles = LinkFiles(files, SemanticGraphExtractor.BuildFileProjectMap(root, snapshot));
        for (var index = 0; index < linkedFiles.Count; index += SemanticIndexer.UpgradeCommitFileBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.UpsertFilesAsync(
                linkedFiles.Skip(index).Take(SemanticIndexer.UpgradeCommitFileBatchSize).ToList(),
                cancellationToken);
        }

        var symbols = new List<SymbolRecord>();
        progress?.Report(new SemanticIndexProgress(
            SemanticIndexStage.SemanticExtraction,
            0,
            snapshot.Projects.Count,
            "extracting compiler symbols"));
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectSymbols = _graph.ExtractSymbols(project, root, cancellationToken).ToList();
            symbols.AddRange(projectSymbols);
            await store.UpsertSymbolsAsync(projectSymbols, cancellationToken);
        }

        var (chunks, syntaxRoutes) = await _syntaxStage.ExtractChunksAndRoutesChunkedAsync(
            store, root, files, dropChunkSymbolIds: true, cancellationToken);
        await store.UpsertRoutesAsync(syntaxRoutes, cancellationToken);

        var semanticRoutes = new List<RouteRecord>();
        var graphDiagnostics = new List<DiagnosticRecord>();
        var completedProjects = 0;
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var graph = _graph.AnalyzeProject(project, root, cancellationToken);
            semanticRoutes.AddRange(graph.Routes);
            graphDiagnostics.AddRange(graph.Diagnostics);

            await store.UpsertNodesAsync(graph.Nodes, cancellationToken);
            await store.UpsertEdgesAsync(graph.Edges, cancellationToken);
            await store.UpsertRoutesAsync(graph.Routes, cancellationToken);
            await store.UpsertDiRegistrationsAsync(graph.DiRegistrations, cancellationToken);
            await store.UpsertOptionsBindingsAsync(graph.OptionsBindings, cancellationToken);
            completedProjects++;
            progress?.Report(new SemanticIndexProgress(
                SemanticIndexStage.SemanticExtraction,
                completedProjects,
                snapshot.Projects.Count,
                project.Name));
        }

        progress?.Report(new SemanticIndexProgress(
            SemanticIndexStage.SemanticPersistence,
            1,
            1,
            "semantic facts persisted"));

        var diagnostics = snapshot.Diagnostics.Concat(graphDiagnostics).ToList();
        var mode = HasLoadWarnings(snapshot) ? "partial" : "semantic";
        return new SemanticIndexResult(
            mode,
            linkedFiles.Count,
            projects.Count,
            symbols.Count,
            chunks.Count,
            syntaxRoutes.Count + semanticRoutes.Count,
            diagnostics);
    }

    internal async Task<SemanticIndexResult> WriteCaptureAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        CaptureResult capture,
        CancellationToken cancellationToken,
        bool replaceTfmAvailability = true)
    {
        var union = CanonicalTfmUnion.Create(capture);
        var projects = _graph.BuildCaptureProjectRecords(union.Projects, cancellationToken);
        await store.UpsertProjectsAsync(projects, cancellationToken);

        var fileToProject = union.Symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol.ProjectPath))
            .GroupBy(symbol => symbol.FilePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ProjectPath!, StringComparer.Ordinal);
        var linkedFiles = LinkFiles(files, fileToProject);
        await store.UpsertFilesAsync(linkedFiles, cancellationToken);

        await store.UpsertSymbolsAsync(union.Symbols, cancellationToken);
        var (chunks, syntaxRoutes) = await _syntaxStage.ExtractChunksAndRoutesAsync(
            root, files, dropChunkSymbolIds: true, cancellationToken);
        await store.UpsertChunksAsync(chunks, cancellationToken);
        await store.UpsertRoutesAsync(syntaxRoutes, cancellationToken);

        await store.UpsertNodesAsync(union.Nodes, cancellationToken);
        await store.UpsertEdgesAsync(union.Edges, cancellationToken);
        await store.UpsertRoutesAsync(union.Routes, cancellationToken);
        await store.UpsertDiRegistrationsAsync(union.Registrations, cancellationToken);
        await store.UpsertOptionsBindingsAsync(union.Bindings, cancellationToken);
        if (replaceTfmAvailability)
            await store.ReplaceTfmAvailabilityAsync(union.Availability, cancellationToken);
        await store.SetMetaAsync("multi_tfm_union_loss", "0", cancellationToken);
        await store.SetMetaAsync(
            "multi_tfm_capture_invocations",
            union.CapturedProjectCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        await store.SetMetaAsync(
            "multi_tfm_unique_projects",
            union.Projects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        await store.SetMetaAsync(
            "multi_tfm_raw_entities",
            union.RawEntityCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        await store.SetMetaAsync(
            "multi_tfm_canonical_entities",
            union.CanonicalEntityCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);

        var mode = capture.Projects.Any(project => project.ErrorCount > 0) ? "partial" : "semantic";
        var diagnostics = new List<DiagnosticRecord>
        {
            new(
                DiagnosticSeverity.Info,
                "build-capture",
                $"Tier-1 canonical TFM union: {union.CapturedProjectCount} compiler invocation(s), " +
                $"{union.Projects.Count} project(s), {union.Symbols.Count} symbols, {union.Edges.Count} edges."),
        };
        return new SemanticIndexResult(
            mode,
            linkedFiles.Count,
            union.Projects.Count,
            union.Symbols.Count,
            chunks.Count,
            syntaxRoutes.Count + union.Routes.Count,
            diagnostics);
    }

    internal async Task<SemanticIndexResult> ProjectFromCompilationsAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<(string ProjectFilePath, Microsoft.CodeAnalysis.Compilation Compilation)> compilations,
        IReadOnlyList<IndexedFileRecord> files,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
            await store.DeleteFileDataAsync(file.NormalizedPath, cancellationToken);

        var captured = new List<CapturedProject>(compilations.Count);
        foreach (var (projectFilePath, compilation) in compilations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = new LoadedProject(
                Path.GetFileNameWithoutExtension(projectFilePath),
                projectFilePath,
                compilation.AssemblyName,
                compilation);
            var symbols = _graph.ExtractSymbols(project, root, cancellationToken);
            var graph = _graph.AnalyzeProject(project, root, cancellationToken);
            var errorCount = compilation.GetDiagnostics(cancellationToken)
                .Count(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            captured.Add(new CapturedProject(
                project.Name,
                project.FilePath,
                compilation.AssemblyName,
                errorCount,
                TypeCount: 0,
                SymbolCount: symbols.Count,
                NodeCount: graph.Nodes.Count,
                EdgeCount: graph.Edges.Count,
                symbols,
                graph.Nodes,
                graph.Edges,
                graph.Routes,
                graph.DiRegistrations,
                graph.OptionsBindings));
        }

        return await WriteCaptureAsync(
            root,
            store,
            files,
            CaptureResult.Ok(captured),
            cancellationToken,
            replaceTfmAvailability: false);
    }

    private List<IndexedFileRecord> LinkFiles(
        IReadOnlyList<IndexedFileRecord> files,
        IReadOnlyDictionary<string, string> fileToProject) =>
        files
            .Select(file => (fileToProject.TryGetValue(file.NormalizedPath, out var projectPath)
                ? file with { ProjectPath = projectPath }
                : file) with
            { Language = _syntaxProviders.ForExtension(file.Extension)?.Language })
            .ToList();

    private static bool HasLoadWarnings(RoslynWorkspaceSnapshot snapshot) =>
        snapshot.Diagnostics.Any(diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error);
}
