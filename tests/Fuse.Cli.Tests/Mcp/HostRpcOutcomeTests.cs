using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net.Sockets;
using Fuse.Cli.Mcp;
using Fuse.Cli.Rpc;
using Fuse.Indexing;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Reduction.Security;
using Fuse.Retrieval;
using Fuse.Semantics;
using Fuse.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// R12: host RPC outcome assertions on the MCP side. Bulk reconcile rebuilds before store-backed reads;
// fuse_check oracle grade is delivered when MCP delegates checkOverlay to a shared daemon over the pipe.
[Collection("FuseToolsResidentProvider")]
public sealed class HostRpcOutcomeTests : IDisposable
{
    private const int StormFileCount = 301;

    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();

    [Fact]
    public async Task Storm_reconcile_rebuilds_before_read_header()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var root = Path.Combine(Path.GetTempPath(), "fuse-storm-header", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        GitInitQuiet(root);

        try
        {
            for (var i = 0; i < StormFileCount; i++)
                await File.WriteAllTextAsync(Path.Combine(root, $"T{i}.cs"), $"namespace Storm; public class Type{i} {{ }}");

            var databasePath = FuseStorePaths.ResolveDatabasePath(root);
            await using (var seed = new WorkspaceIndexStore(databasePath))
            {
                await seed.InitializeAsync(CancellationToken.None);
                await indexer.IndexAsync(root, seed, CancellationToken.None);
            }

            for (var i = 0; i < StormFileCount; i++)
                await File.WriteAllTextAsync(
                    Path.Combine(root, $"T{i}.cs"),
                    $"namespace Storm; public class Type{i} {{ public void M() {{ }} }}");

            var output = await FuseTools.FuseImpactAsync(
                indexer, symbol: "Type0", path: root, cancellationToken: CancellationToken.None);

            Assert.StartsWith("index_state: ready", output);
            Assert.Contains($"files_indexed: {StormFileCount}", output);
            Assert.Contains("up to date", output);
            Assert.DoesNotContain("results may lag the working tree", output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task FuseCheck_reports_oracle_grade_via_daemon_checkOverlay_rpc()
    {
        var indexer = _provider.GetRequiredService<SemanticIndexer>();
        var work = Path.Combine(Path.GetTempPath(), "fuse-check-rpc-grade", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        work.AsIsolatedRepo();
        await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"),
            "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The daemon gets its own resident provider (its live workspace in production). Passing it explicitly keeps
        // it distinct from the client's Remote provider below: both sides share one process here, so a single static
        // provider could not represent a real two-process daemon/client split.
        var daemonResident = new FakeResidentProvider(work, [
            new CheckDiagnostic("CS1061", "Error", "'Widget' does not contain a definition for 'Nope'", "Widget.cs", 1),
        ]);

        var service = new FuseHostService(
            indexer,
            _provider.GetRequiredService<IChangeSource>(),
            _provider.GetRequiredService<ContentReductionPipeline>(),
            _provider.GetRequiredService<ISecretRedactor>(),
            _provider.GetRequiredService<IGeneratedCodeDetector>(),
            _provider.GetRequiredService<IndexCoordinator>(),
            _provider.GetRequiredService<IWorkspaceIndexJobManager>(),
            NullLogger<FuseHostService>.Instance,
            work,
            daemonResident);

        var serverTask = ServeHostAsync(work, service, ready, cts.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            var runtime = FuseMcpRuntime.CreateIsolated(indexer, new RemoteResidentWorkspaceProvider());

            var output = await FuseTools.FuseCheckAsync(
                indexer,
                work,
                "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => Nope; }",
                cancellationToken: CancellationToken.None,
                runtime: runtime);

            Assert.Contains("verification grade: oracle", output);
            Assert.Contains("CS1061", output);
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static void GitInitQuiet(string dir)
    {
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
        }
    }

    private static async Task ServeHostAsync(
        string root, FuseHostService service, TaskCompletionSource ready, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var server = new NamedPipeServerStream(
                HostEndpoint.PipeName(root), PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            await using (server)
            {
                ready.TrySetResult();
                await server.WaitForConnectionAsync(cancellationToken);
                using var rpc = FuseHostConnection.Attach(PipeReader.Create(server), PipeWriter.Create(server), service);
                await rpc.Completion.WaitAsync(cancellationToken);
            }
            return;
        }

        var socketPath = HostEndpoint.SocketPath(root);
        if (File.Exists(socketPath))
            File.Delete(socketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        ready.TrySetResult();
        try
        {
            var accepted = await listener.AcceptAsync(cancellationToken);
            var stream = new NetworkStream(accepted, ownsSocket: true);
            await using (stream)
            {
                using var rpc = FuseHostConnection.Attach(PipeReader.Create(stream), PipeWriter.Create(stream), service);
                await rpc.Completion.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            try { File.Delete(socketPath); } catch (IOException) { }
        }
    }

    private sealed class FakeResidentProvider(string root, IReadOnlyList<CheckDiagnostic> diagnostics)
        : IResidentWorkspaceProvider
    {
        public ResidentStatus? DescribeResident(string queried) =>
            string.Equals(queried, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                ? new ResidentStatus(1, "test")
                : null;

        public IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
            string queried, string relativeFilePath, string newContent, CancellationToken cancellationToken) =>
            string.Equals(queried, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ? diagnostics : null;

        public Task<IReadOnlyList<CheckDiagnostic>?> TryCheckOverlayAsync(
            string queried, string relativeFilePath, string newContent, bool includeAnalyzers, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CheckDiagnostic>?>(
                string.Equals(queried, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ? diagnostics : null);
    }

    public void Dispose()
    {
        _provider.Dispose();
    }
}
