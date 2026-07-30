using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

public sealed class FuseRefactorRuntimeTests
{
    [Fact]
    public async Task Refactor_uses_the_runtime_warm_solution_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-refactor-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var projectPath = Path.Combine(root, "Widget.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(Path.Combine(root, "Widget.cs"), "public sealed class Widget { }");

        var cache = new WarmSolutionCache(
            loader: (_, _) => Task.FromResult(CreateLoadedWorkspace(root)),
            signature: _ => 0);
        var coordinator = new IndexCoordinator();
        var jobs = new WorkspaceIndexJobManager(new UnusedIndexExecutor());
        var runtime = new FuseMcpRuntime(
            new LocalIndexAccessProvider(coordinator, jobs),
            NullResidentWorkspaceProvider.Instance,
            coordinator,
            jobs,
            cache,
            new PooledCheckWorker(),
            new OwnedProcessRunner());

        try
        {
            var output = await RefactorToolOperations.RefactorCoreAsync(
                root,
                "Widget",
                "Gadget",
                "rename",
                string.Empty,
                string.Empty,
                string.Empty,
                "default",
                string.Empty,
                string.Empty,
                string.Empty,
                CancellationToken.None,
                routeToHost: false,
                runtime: runtime);

            Assert.Contains("staged rename", output);
            Assert.Equal(1, cache.LoadCount);
        }
        finally
        {
            await jobs.DisposeAsync();
            cache.Dispose();
            runtime.PooledCheckWorkers.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static LoadedWorkspace CreateLoadedWorkspace(string root)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Widget", "Widget", LanguageNames.CSharp)
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(documentId, "Widget.cs", SourceText.From("public sealed class Widget { }"), filePath: Path.Combine(root, "Widget.cs"));
        return new LoadedWorkspace(workspace, solution, []);
    }

    private sealed class UnusedIndexExecutor : IWorkspaceIndexJobExecutor
    {
        public Task<SemanticIndexResult> ExecuteAsync(
            string jobId,
            IndexJobRequest request,
            IProgress<IndexJobProgress> progress,
            CancellationToken cancellationToken) =>
            Task.FromException<SemanticIndexResult>(new InvalidOperationException("Index execution is not expected in this test."));
    }
}
