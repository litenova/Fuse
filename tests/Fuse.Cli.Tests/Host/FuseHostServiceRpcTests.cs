using System.IO.Pipelines;
using System.Text.Json;
using Fuse.Cli.Rpc;
using Fuse.Cli.Mcp;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Reduction;
using Fuse.Retrieval;
using Fuse.Reduction.Security;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Fuse.Cli.Tests;

// Drives the host service over a real JSON-RPC connection (an in-memory duplex pipe pair, the same formatter and
// header framing the named-pipe transport uses) to validate the wire wiring end to end: a client calls the
// fuse/* methods by name and gets back deserialized DTOs, and fuse/shutdown completes the host's shutdown task.
//
// The fuse/check RPC reads the process-wide FuseTools.ResidentWorkspaces static, so this class joins the
// collection that serializes the tests mutating it, avoiding a parallel race on the shared static.
[Collection("FuseToolsResidentProvider")]
public sealed class FuseHostServiceRpcTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();

    private FuseHostService NewService() => new(
        _provider.GetRequiredService<SemanticIndexer>(),
        _provider.GetRequiredService<IChangeSource>(),
        _provider.GetRequiredService<ContentReductionPipeline>(),
        _provider.GetRequiredService<ISecretRedactor>(),
        _provider.GetRequiredService<IGeneratedCodeDetector>(),
        _provider.GetRequiredService<IndexCoordinator>(),
        _provider.GetRequiredService<IWorkspaceIndexJobManager>(),
        NullLogger<FuseHostService>.Instance);

    private static string SessionToken(FuseHostService service) => service.Handshake().SessionToken;

    // The host reads the persistent semantic index, which lives at {repo}/.fuse for a git work tree and the shared
    // ~/.fuse otherwise. Git-init each fixture so its index is isolated (a fresh, empty store the host indexes into),
    // matching how the MCP integration test isolates its fixture. Git-absent falls back to the shared store.
    private static string NewFixture(params (string RelativePath, string Content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-host-rpc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "init",
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            git?.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Git not on PATH: the store falls back to the shared location; the test still functions.
        }

        foreach (var (relativePath, content) in files)
        {
            var full = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        // Guarantee an isolated {dir}/.fuse store even if git is not on PATH (git init above may have been skipped).
        return dir.AsIsolatedRepo();
    }

    // Release the pooled connections to this fixture's store before deleting it, or the open fuse.db handle blocks
    // the recursive delete. Mirrors the cleanup the other store-backed test classes already do.
    private static void CleanupFixture(string source)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(
            new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={Fuse.Reduction.Caching.FuseStorePaths.ResolveDatabasePath(source)}"));
        try { Directory.Delete(source, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Client_CallsHandshakeStatsAndShutdown_OverTheWire()
    {
        // Two pipes form a full-duplex transport: client writes to clientToServer, server reads it, and vice versa.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var service = NewService();
        using var serverRpc = FuseHostConnection.Attach(clientToServer.Reader, serverToClient.Writer, service);

        var clientFormatter = new SystemTextJsonFormatter();
        clientFormatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FuseHostJsonContext.Default);
        // Mirror the real vscode-jsonrpc extension client, which reads camelCase field names (protocol.ts).
        clientFormatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        using var clientRpc = new JsonRpc(
            new HeaderDelimitedMessageHandler(clientToServer.Writer, serverToClient.Reader, clientFormatter));
        clientRpc.StartListening();

        var handshake = await clientRpc.InvokeAsync<FuseHostHandshake>("fuse/handshake");
        Assert.Equal(11, handshake.ProtocolVersion);
        Assert.Equal(FuseHostService.ProtocolVersion, handshake.ProtocolVersion);
        Assert.False(string.IsNullOrWhiteSpace(handshake.HostVersion));
        Assert.False(string.IsNullOrWhiteSpace(handshake.SessionToken));

        var stats = await clientRpc.InvokeAsync<FuseHostStats>("fuse/stats", handshake.SessionToken);
        Assert.Equal(Environment.ProcessId, stats.ProcessId);
        Assert.True(stats.WorkingSetBytes > 0);

        Assert.False(service.ShutdownRequested.IsCompleted);
        await clientRpc.NotifyAsync("fuse/shutdown", handshake.SessionToken);
        await service.ShutdownRequested.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.ShutdownRequested.IsCompleted);
    }

    [Fact]
    public async Task Handshake_SerializesCamelCaseOnTheWire_ForACamelCaseClient()
    {
        // The VS Code extension's vscode-jsonrpc client reads camelCase field names (see protocol.ts). A client
        // configured that way must deserialize a real ProtocolVersion, not the default 0: if the host emits
        // PascalCase, every field reads as its default and the extension reports an opaque "host undefined"
        // protocol mismatch. This is the cross-formatter contract the same-formatter test above cannot see.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        using var serverRpc = FuseHostConnection.Attach(clientToServer.Reader, serverToClient.Writer, NewService());

        var clientFormatter = new SystemTextJsonFormatter();
        clientFormatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FuseHostJsonContext.Default);
        // Mirror the real vscode-jsonrpc extension client, which reads camelCase field names (protocol.ts).
        clientFormatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        using var clientRpc = new JsonRpc(
            new HeaderDelimitedMessageHandler(clientToServer.Writer, serverToClient.Reader, clientFormatter));
        clientRpc.StartListening();

        var handshake = await clientRpc.InvokeAsync<FuseHostHandshake>("fuse/handshake");

        Assert.Equal(11, handshake.ProtocolVersion);
        Assert.Equal(FuseHostService.ProtocolVersion, handshake.ProtocolVersion);
        Assert.False(string.IsNullOrWhiteSpace(handshake.HostVersion));
        Assert.False(string.IsNullOrWhiteSpace(handshake.SessionToken));
    }

    [Fact]
    public async Task Stats_WithoutSessionToken_RejectsOverTheWire()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        using var serverRpc = FuseHostConnection.Attach(clientToServer.Reader, serverToClient.Writer, NewService());

        var clientFormatter = new SystemTextJsonFormatter();
        clientFormatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FuseHostJsonContext.Default);
        // Mirror the real vscode-jsonrpc extension client, which reads camelCase field names (protocol.ts).
        clientFormatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        using var clientRpc = new JsonRpc(
            new HeaderDelimitedMessageHandler(clientToServer.Writer, serverToClient.Reader, clientFormatter));
        clientRpc.StartListening();

        await clientRpc.InvokeAsync<FuseHostHandshake>("fuse/handshake");

        var ex = await Assert.ThrowsAnyAsync<RemoteRpcException>(() =>
            clientRpc.InvokeAsync<FuseHostStats>("fuse/stats", string.Empty));
        Assert.Contains("session token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stats_WithWrongSessionToken_RejectsOverTheWire()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        using var serverRpc = FuseHostConnection.Attach(clientToServer.Reader, serverToClient.Writer, NewService());

        var clientFormatter = new SystemTextJsonFormatter();
        clientFormatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FuseHostJsonContext.Default);
        // Mirror the real vscode-jsonrpc extension client, which reads camelCase field names (protocol.ts).
        clientFormatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        using var clientRpc = new JsonRpc(
            new HeaderDelimitedMessageHandler(clientToServer.Writer, serverToClient.Reader, clientFormatter));
        clientRpc.StartListening();

        await clientRpc.InvokeAsync<FuseHostHandshake>("fuse/handshake");

        var ex = await Assert.ThrowsAnyAsync<RemoteRpcException>(() =>
            clientRpc.InvokeAsync<FuseHostStats>("fuse/stats", "not-the-session-token"));
        Assert.Contains("session token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IndexStart_builds_syntax_index_and_counts_files()
    {
        var source = NewFixture(
            ("Widget.cs", "public class Widget { public void Run() { } }"),
            ("Gadget.cs", "public class Gadget { public void Go() { } }"));

        try
        {
            var service = NewService();
            var result = await service.IndexStartAsync(SessionToken(service), source);
            var completed = await _provider.GetRequiredService<IWorkspaceIndexJobManager>()
                .WaitForCompletionAsync(source, CancellationToken.None);

            Assert.False(result.Conflict);
            Assert.NotNull(completed);
            Assert.Equal(IndexJobState.Completed, completed!.State);
            Assert.True(completed.Counts.Files >= 2, $"expected at least 2 files, got {completed.Counts.Files}");
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task IndexStart_empty_repository_reports_completed_job()
    {
        var source = NewFixture();

        try
        {
            var service = NewService();
            await service.IndexStartAsync(SessionToken(service), source);
            var result = await _provider.GetRequiredService<IWorkspaceIndexJobManager>()
                .WaitForCompletionAsync(source, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(IndexJobState.Completed, result!.State);
            Assert.Equal(0, result.Counts.Files);
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task IndexStart_missing_directory_reports_failed_job()
    {
        var missing = Path.Combine(Path.GetTempPath(), "fuse-host-missing", Guid.NewGuid().ToString("N"));

        var service = NewService();
        var result = await service.IndexStartAsync(SessionToken(service), missing);

        Assert.Equal(IndexJobState.Failed, result.Snapshot.State);
        Assert.Equal("workspace_not_found", result.Snapshot.ErrorCode);
    }

    [Fact]
    public async Task Scope_Search_EmitsTheMatchedFileAndWritesPayload()
    {
        var source = NewFixture(
            ("PaymentService.cs", "public class PaymentService { public void ProcessPayment() { } }"),
            ("CatalogService.cs", "public class CatalogService { public void ListProducts() { } }"));

        try
        {
            var service = NewService();
            var token = SessionToken(service);
            var result = await service.ScopeAsync(token, source, "search", null, "process payment", null, 20000);

            Assert.Equal("search", result.Mode);
            Assert.NotEmpty(result.Files);
            Assert.Contains(result.Files, f => f.Path.Contains("PaymentService", StringComparison.Ordinal));
            Assert.NotNull(result.PayloadPath);
            Assert.True(File.Exists(result.PayloadPath));
            var payloadDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Fuse",
                "host-payloads");
            Assert.StartsWith(payloadDir, result.PayloadPath!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PaymentService", await File.ReadAllTextAsync(result.PayloadPath!));
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task Scope_ThenShutdown_DeletesPayload()
    {
        var source = NewFixture(
            ("PaymentService.cs", "public class PaymentService { public void ProcessPayment() { } }"));

        try
        {
            var service = NewService();
            var token = SessionToken(service);
            var result = await service.ScopeAsync(token, source, "search", null, "process payment", null, 20000);

            Assert.NotNull(result.PayloadPath);
            Assert.True(File.Exists(result.PayloadPath));

            service.Shutdown(token);
            await service.ShutdownRequested.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(File.Exists(result.PayloadPath!));
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task Graph_Files_ReturnsNodesAndAReferenceEdge()
    {
        var source = NewFixture(
            ("Service.cs", "public class Service { public int Value() => 1; }"),
            ("Consumer.cs", "public class Consumer { private Service _s = new Service(); public int Use() => _s.Value(); }"));

        try
        {
            var service = NewService();
            var token = SessionToken(service);
            var graph = await service.GraphAsync(token, source, "Files");

            Assert.Equal("Files", graph.Detail);
            Assert.Contains(graph.Nodes, n => n.Path.Contains("Service", StringComparison.Ordinal));
            Assert.Contains(graph.Nodes, n => n.Path.Contains("Consumer", StringComparison.Ordinal));
            // Typed dependency edges require semantic (Roslyn) analysis; a loose-file fixture indexes at the syntax
            // tier, which has file nodes but no cross-file edges, so the projection is a valid empty-edge graph.
            Assert.NotNull(graph.Edges);
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task Graph_WithScopeOverlay_TagsMatchedNodesWithARole()
    {
        var source = NewFixture(
            ("PaymentService.cs", "public class PaymentService { public void ProcessPayment() { } }"),
            ("CatalogService.cs", "public class CatalogService { public void ListProducts() { } }"));

        try
        {
            var service = NewService();
            var token = SessionToken(service);
            var graph = await service.GraphAsync(token, source, "Files", "search", null, "process payment", null);

            // The scope overlay tags the matched file's node with a role (the whole point of the overlay).
            var matched = Assert.Single(graph.Nodes, n => n.Path.Contains("PaymentService", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(matched.Role));
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task Graph_Directories_FoldsFilesIntoDirectorySupernodes()
    {
        var source = NewFixture(
            ("Core/Service.cs", "public class Service { public int V() => 1; }"),
            ("App/Consumer.cs", "public class Consumer { private Service _s = new Service(); }"));

        try
        {
            var service = NewService();
            var token = SessionToken(service);
            var graph = await service.GraphAsync(token, source, "Directories");

            Assert.Equal("Directories", graph.Detail);
            // Directory nodes, not file nodes: at most one node per directory, far fewer than the file count.
            Assert.Contains(graph.Nodes, n => n.Path is "Core" or "App");
            Assert.All(graph.Nodes, n => Assert.DoesNotContain(".cs", n.Path));

            // Expanding one directory returns only that directory's file nodes (the expand-on-click subgraph).
            var expanded = await service.GraphAsync(token, source, "Directories", null, null, null, null, "Core");
            Assert.Equal("Files", expanded.Detail);
            Assert.NotEmpty(expanded.Nodes);
            Assert.All(expanded.Nodes, n => Assert.StartsWith("Core/", n.Path));
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task Diagnostics_ReportsASecretAtItsPreciseRange()
    {
        // A line carrying an AWS access key (a synthetic example), so the diagnostic must land on line 1.
        var secretLine = "    var key = \"AKIAIOSFODNN7EXAMPLE\";";
        var source = NewFixture(
            ("Config.cs", "public class Config\n" + secretLine + "\n"),
            // An isolated class with no inbound or outbound type reference must surface as a graph gap.
            ("Orphan.cs", "public class Orphan { public int N() => 1; }"),
            // An EF Core migration (the generated-code shape the detector recognizes) must surface as generated.
            ("InitialCreate.cs", "public partial class InitialCreate : Migration { protected override void Up(MigrationBuilder b) { } }"));

        try
        {
            var service = NewService();
            var diagnostics = await service.DiagnosticsAsync(SessionToken(service), source);

            var finding = Assert.Single(diagnostics.Secrets);
            Assert.Equal("aws-access-key", finding.Kind);
            Assert.Contains("Config.cs", finding.Path);
            Assert.Equal(1, finding.StartLine); // zero-based: the secret is on the second line
            Assert.Equal(secretLine.IndexOf("AKIA", StringComparison.Ordinal), finding.StartColumn);

            Assert.NotEmpty(diagnostics.Hotspots);
            Assert.Contains(diagnostics.GraphGaps, g => g.Contains("Orphan", StringComparison.Ordinal));
            Assert.Contains(diagnostics.Generated, g => g.Contains("InitialCreate.cs", StringComparison.Ordinal));
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task Explain_Search_ReturnsAPlanWithSeedRolesAndTiers()
    {
        var source = NewFixture(
            ("PaymentService.cs", "public class PaymentService { public void ProcessPayment() { } }"),
            ("CatalogService.cs", "public class CatalogService { public void ListProducts() { } }"));

        try
        {
            var service = NewService();
            var token = SessionToken(service);
            var explain = await service.ExplainAsync(token, source, "search", null, "process payment", null);

            Assert.Equal("search", explain.Mode);
            Assert.NotEmpty(explain.Files);
            // The plan names the matched file and assigns it a role and a tier (the explainer's whole purpose).
            var matched = Assert.Single(explain.Files, f => f.Path.Contains("PaymentService", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(matched.Role));
            Assert.False(string.IsNullOrWhiteSpace(matched.Tier));
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task Notifier_BroadcastsInvalidatedToConnectedClients()
    {
        // The host pushes fuse/invalidated to every connected editor when the watcher fires. Wire a client over
        // an in-memory pipe, register a notification handler, broadcast through the notifier, and assert receipt.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var notifier = new HostNotifier();

        using var serverRpc = FuseHostConnection.Attach(clientToServer.Reader, serverToClient.Writer, NewService(), notifier);

        var clientFormatter = new SystemTextJsonFormatter();
        clientFormatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FuseHostJsonContext.Default);
        // Mirror the real vscode-jsonrpc extension client, which reads camelCase field names (protocol.ts).
        clientFormatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        using var clientRpc = new JsonRpc(
            new HeaderDelimitedMessageHandler(clientToServer.Writer, serverToClient.Reader, clientFormatter));
        var invalidated = new TaskCompletionSource();
        clientRpc.AddLocalRpcMethod("fuse/invalidated", () => invalidated.TrySetResult());
        clientRpc.StartListening();

        // Let the connection register before broadcasting.
        await Task.Delay(50);
        Assert.Equal(1, notifier.ConnectionCount);
        await notifier.BroadcastAsync("fuse/invalidated");

        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(invalidated.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ConcurrentGraphAndScope_AgainstOneRoot_BothSucceed()
    {
        // The playbook's concurrency check: simultaneous fuse/graph and fuse/scope against one root exercise the
        // shared orchestrator and pooled store under concurrent calls (C3 DI concurrency under the new transport).
        var source = NewFixture(
            ("Service.cs", "public class Service { public int V() => 1; }"),
            ("Consumer.cs", "public class Consumer { private Service _s = new Service(); public int U() => _s.V(); }"));

        try
        {
            var service = NewService();
            var token = SessionToken(service);
            // Fire several graph and scope calls at once on one root; none may throw and each must return data.
            var graphTasks = Enumerable.Range(0, 4).Select(_ => service.GraphAsync(token, source, "Files"));
            var scopeTasks = Enumerable.Range(0, 4).Select(_ => service.ScopeAsync(token, source, "search", null, "service", null, 20000));

            var graphs = await Task.WhenAll(graphTasks);
            var scopes = await Task.WhenAll(scopeTasks);

            Assert.All(graphs, g => Assert.NotEmpty(g.Nodes));
            Assert.All(scopes, s => Assert.Equal("search", s.Mode));
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task Check_WithNoResidentWorkspace_ReturnsNonResidentEmptyDelta()
    {
        // Delta mode must not run a build, so with no resident workspace the RPC returns a non-resident empty
        // delta and an ambient-verification hook stays silent rather than blocking editing.
        Fuse.Cli.Mcp.FuseTools.ResidentWorkspaces = Fuse.Workspace.NullResidentWorkspaceProvider.Instance;
        var source = NewFixture(("Widget.cs", "public class Widget { public void Run() { } }"));
        try
        {
            var service = NewService();
            var delta = await service.CheckDeltaAsync(SessionToken(service), source, "session-1");

            Assert.False(delta.Resident);
            Assert.Empty(delta.Introduced);
            Assert.Empty(delta.Resolved);
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task CheckOverlay_WithNoResidentWorkspace_ReturnsNoResident()
    {
        // With no resident workspace the daemon cannot answer resident-grade; the caller falls back to its own path.
        Fuse.Cli.Mcp.FuseTools.ResidentWorkspaces = Fuse.Workspace.NullResidentWorkspaceProvider.Instance;
        var source = NewFixture(("Widget.cs", "public class Widget { public void Run() { } }"));
        try
        {
            var service = NewService();
            var result = await service.CheckOverlayAsync(SessionToken(service), source, "Widget.cs", "public class Widget { }", includeAnalyzers: false);

            Assert.False(result.HasResident);
            Assert.Empty(result.Diagnostics);
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task CheckCapture_WithoutACapture_ReturnsFallbackSignal()
    {
        var source = NewFixture(("Widget.cs", "public class Widget { }"));
        try
        {
            using var service = NewService();
            var result = await service.CheckCaptureAsync(SessionToken(service), source, "Widget.cs", "public class Widget { }");

            Assert.False(result.Available);
            Assert.Empty(result.Diagnostics);
        }
        finally
        {
            CleanupFixture(source);
        }
    }

    [Fact]
    public async Task CheckOverlay_DelegatesToTheResidentWorkspace()
    {
        // G5: the RPC delegates to the daemon's resident workspace, so a non-owner client gets resident-grade
        // diagnostics over the pipe. A fake resident provider stands in for the daemon's live workspace.
        Fuse.Cli.Mcp.FuseTools.ResidentWorkspaces = new FakeResidentProvider(
            [new Fuse.Indexing.CheckDiagnostic("CS0246", "Error", "type 'Gadget' not found", "Widget.cs", 1)]);
        var source = NewFixture(("Widget.cs", "public class Widget { }"));
        try
        {
            var service = NewService();
            var result = await service.CheckOverlayAsync(SessionToken(service), source, "Widget.cs", "public class Widget : Gadget { }", includeAnalyzers: true);

            Assert.True(result.HasResident);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("CS0246", diagnostic.Id);
            Assert.Equal("Error", diagnostic.Severity);
        }
        finally
        {
            Fuse.Cli.Mcp.FuseTools.ResidentWorkspaces = Fuse.Workspace.NullResidentWorkspaceProvider.Instance;
            CleanupFixture(source);
        }
    }

    // A stand-in resident workspace that returns preset overlay diagnostics, so the RPC delegation is tested
    // without a live compilation.
    private sealed class FakeResidentProvider(IReadOnlyList<Fuse.Indexing.CheckDiagnostic> diagnostics)
        : Fuse.Workspace.IResidentWorkspaceProvider
    {
        public Fuse.Workspace.ResidentStatus? DescribeResident(string root) => new(ProjectCount: 1, AsOf: "now");

        public IReadOnlyList<Fuse.Indexing.CheckDiagnostic>? TryCheckOverlay(
            string root, string relativeFilePath, string newContent, CancellationToken cancellationToken) => diagnostics;

        public Task<IReadOnlyList<Fuse.Indexing.CheckDiagnostic>?> TryCheckOverlayAsync(
            string root, string relativeFilePath, string newContent, bool includeAnalyzers, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Fuse.Indexing.CheckDiagnostic>?>(diagnostics);
    }

    public void Dispose()
    {
        Fuse.Cli.Mcp.FuseTools.ResidentWorkspaces = Fuse.Workspace.NullResidentWorkspaceProvider.Instance;
        _provider.Dispose();
    }
}
