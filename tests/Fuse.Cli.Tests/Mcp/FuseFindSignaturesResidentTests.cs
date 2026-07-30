using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Fuse.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// U1b: fuse_find kind=signatures routes resident-first. When a live resident workspace serves the root it resolves
// a qualified name (including a referenced package's API) from the compiler's real metadata, so the signature is
// answered from the resident compilation rather than the store (which never indexed the package). With the default
// null provider the routing is a no-op and the store-backed signature lookup is unchanged.
public sealed class FuseFindSignaturesResidentTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();

    public void Dispose()
    {
        _provider.Dispose();
    }

    [Fact]
    public async Task Signatures_are_answered_from_the_resident_metadata_when_a_workspace_is_live()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var changeSource = _provider.GetRequiredService<IChangeSource>();
        var work = Path.Combine(Path.GetTempPath(), "fuse-find-sig-resident-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.Combine(work, ".git"));
        var coordinator = new IndexCoordinator();
        var jobs = new WorkspaceIndexJobManager(new SemanticIndexJobExecutor(coordinator, indexer));
        try
        {
            // A minimal source file so the store index builds; the resident answer supersedes it for the query.
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"),
                "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

            var root = Path.GetFullPath(work);
            var runtime = new FuseMcpRuntime(
                new LocalIndexAccessProvider(coordinator, jobs),
                new StubSignatureProvider(root,
                [
                    new ResidentSignature(
                        "public static string Serialize<TValue>(TValue value)", "Method",
                        "System.Text.Json.JsonSerializer", "System.Text.Json"),
                ]),
                coordinator,
                jobs,
                new WarmSolutionCache(),
                new PooledCheckWorker(),
                new OwnedProcessRunner());

            var output = await FindToolOperations.ExecuteAsync(
                indexer, changeSource, "System.Text.Json.JsonSerializer.Serialize", work, kind: "signatures",
                cancellationToken: CancellationToken.None,
                runtime: runtime);

            Assert.Contains("Serialize<TValue>", output);
            Assert.Contains("resident (metadata: System.Text.Json)", output);
        }
        finally
        {
            await jobs.DisposeAsync();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Resident_signatures_are_returned_while_the_syntax_index_is_building()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var changeSource = _provider.GetRequiredService<IChangeSource>();
        var work = Path.Combine(Path.GetTempPath(), "fuse-find-sig-resident-deferred", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.Combine(work, ".git"));
        var root = Path.GetFullPath(work);
        var coordinator = new IndexCoordinator();
        var jobs = new WorkspaceIndexJobManager(new SemanticIndexJobExecutor(coordinator, indexer));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"),
                "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

            var runtime = new FuseMcpRuntime(
                new DeferredIndexAccessProvider(root, jobs),
                new StubSignatureProvider(root,
                [
                    new ResidentSignature(
                        "public static string Serialize<TValue>(TValue value)", "Method",
                        "System.Text.Json.JsonSerializer", "System.Text.Json"),
                ]),
                coordinator,
                jobs,
                new WarmSolutionCache(),
                new PooledCheckWorker(),
                new OwnedProcessRunner());

            var output = await FindToolOperations.ExecuteAsync(
                indexer,
                changeSource,
                "System.Text.Json.JsonSerializer.Serialize",
                work,
                kind: "signatures",
                cancellationToken: CancellationToken.None,
                runtime: runtime);

            Assert.Contains("Serialize<TValue>", output);
        }
        finally
        {
            await jobs.DisposeAsync();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Signatures_fall_back_to_the_store_when_no_resident_workspace_serves_the_root()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var changeSource = _provider.GetRequiredService<IChangeSource>();
        var work = Path.Combine(Path.GetTempPath(), "fuse-find-sig-resident-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.Combine(work, ".git"));
        var coordinator = new IndexCoordinator();
        var jobs = new WorkspaceIndexJobManager(new SemanticIndexJobExecutor(coordinator, indexer));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"),
                "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

            // The assertion targets store fallback, not the MCP cold-read deadline. A bounded longer wait keeps
            // the test independent from unrelated full-suite CPU pressure.
            var runtime = new FuseMcpRuntime(
                new LocalIndexAccessProvider(coordinator, jobs, TimeSpan.FromSeconds(30)),
                NullResidentWorkspaceProvider.Instance,
                coordinator,
                jobs,
                new WarmSolutionCache(),
                new PooledCheckWorker(),
                new OwnedProcessRunner());
            var output = await FindToolOperations.ExecuteAsync(
                indexer,
                changeSource,
                "Widget",
                work,
                kind: "signatures",
                cancellationToken: CancellationToken.None,
                runtime: runtime);

            Assert.Contains("Widget", output);
            Assert.DoesNotContain("resident (metadata:", output);
        }
        finally
        {
            await jobs.DisposeAsync();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class StubSignatureProvider(string root, IReadOnlyList<ResidentSignature> signatures)
        : IResidentWorkspaceProvider
    {
        public ResidentStatus? DescribeResident(string queried) =>
            string.Equals(queried, root, StringComparison.OrdinalIgnoreCase) ? new ResidentStatus(1, "test") : null;

        public IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
            string queried, string relativeFilePath, string newContent, CancellationToken cancellationToken) => null;

        public IReadOnlyList<ResidentSignature>? TryGetSignature(
            string queried, string symbolName, int limitPerName, CancellationToken cancellationToken) =>
            string.Equals(queried, root, StringComparison.OrdinalIgnoreCase) ? signatures : null;
    }

    private sealed class DeferredIndexAccessProvider(string root, IWorkspaceIndexJobManager jobs) : IIndexAccessProvider
    {
        public Task<IndexJobStartResult> StartSyntaxAsync(
            SemanticIndexer indexer, string path, CancellationToken cancellationToken) =>
            jobs.StartOrJoinAsync(
                new IndexJobRequest(root, IndexDepth.Syntax, Force: false, CaptureBundlePath: null),
                cancellationToken);

        public Task<WorkspaceIndexStore> OpenIndexedAsync(
            SemanticIndexer indexer, string path, CancellationToken cancellationToken) =>
            throw new ColdStartInProgressException(root);

        public Task<SemanticIndexResult> IndexAsync(
            SemanticIndexer indexer, string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
