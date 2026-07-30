using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net.Sockets;
using Fuse.Cli.Rpc;
using Fuse.Cli.Mcp;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Reduction;
using Fuse.Reduction.Security;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fuse.Cli.Tests;

// S3: FuseHostClient connects to a running host over the real UI transport (named pipe on Windows, Unix socket
// elsewhere), handshakes, and invokes fuse/check. With no host it returns null (the hook stays silent). These
// exercise the actual transport, not the in-memory pipe pair the RPC tests use.
[Collection("FuseToolsResidentProvider")]
public sealed class FuseHostClientTests : IDisposable
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

    [Fact]
    public async Task No_host_serving_the_root_returns_null()
    {
        Fuse.Cli.Mcp.FuseTools.ResidentWorkspaces = Fuse.Workspace.NullResidentWorkspaceProvider.Instance;
        // A root no host is serving: the client probes the endpoint, finds nothing, and returns null quickly.
        var root = Path.Combine(Path.GetTempPath(), "fuse-client-nohost", Guid.NewGuid().ToString("N"));

        var delta = await FuseHostClient.TryCheckDeltaAsync(root, "s1", TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(delta);
    }

    [Fact]
    public async Task TryStatsAsync_over_real_transport_reports_host_version_and_working_set()
    {
        Fuse.Cli.Mcp.FuseTools.ResidentWorkspaces = Fuse.Workspace.NullResidentWorkspaceProvider.Instance;
        var root = Path.Combine(Path.GetTempPath(), "fuse-client-stats", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var service = NewService();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeOneAsync(root, service, ready, cts.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            var stats = await FuseHostClient.TryStatsAsync(root, TimeSpan.FromSeconds(5), cts.Token);

            Assert.NotNull(stats);
            Assert.False(string.IsNullOrWhiteSpace(stats!.HostVersion));
            Assert.True(stats.WorkingSetBytes > 0);
            Assert.True(stats.UptimeMs >= 0);
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Connects_to_a_running_host_and_gets_the_delta()
    {
        Fuse.Cli.Mcp.FuseTools.ResidentWorkspaces = Fuse.Workspace.NullResidentWorkspaceProvider.Instance;
        var root = Path.Combine(Path.GetTempPath(), "fuse-client-host", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var service = NewService();

        // Start a one-shot host endpoint for this root, matching how HostCommand serves. Wait until the endpoint
        // is listening before connecting, since a real host is always up before a hook fires (the client's
        // existence pre-check is a point-in-time check, not a poll).
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeOneAsync(root, service, ready, cts.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            var delta = await FuseHostClient.TryCheckDeltaAsync(root, "s1", TimeSpan.FromSeconds(5), cts.Token);

            // No resident workspace is wired, so the delta round-trips as non-resident and empty - which proves the
            // client connected, handshook, invoked fuse/check, and deserialized the DTO over the real transport.
            Assert.NotNull(delta);
            Assert.False(delta!.Resident);
            Assert.Empty(delta.Introduced);
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // Accepts a single connection on the root's endpoint and serves the host service until cancelled. Signals
    // `ready` once the endpoint exists (the pipe is created / the socket is bound), so the client connects only
    // after the host is listening.
    private static async Task ServeOneAsync(string root, FuseHostService service, TaskCompletionSource ready, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var server = new NamedPipeServerStream(
                HostEndpoint.PipeName(root), PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            await using (server)
            {
                ready.TrySetResult(); // the pipe now exists in the \\.\pipe\ namespace
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
        ready.TrySetResult(); // the socket file now exists
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

    public void Dispose()
    {
        Fuse.Cli.Mcp.FuseTools.ResidentWorkspaces = Fuse.Workspace.NullResidentWorkspaceProvider.Instance;
        _provider.Dispose();
    }
}
