using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Fuse.Cli.Rpc;
using Fuse.Cli.Mcp;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Reduction;
using Fuse.Reduction.Security;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;

namespace Fuse.Cli.Tests.Host;

// R12: host RPC outcome assertions (F-026). These pin client-visible outcomes and wire errors, not per-method
// wiring smokes. Served-root rejection over the transport is one outcome test here; per-method coverage lives in
// FuseHostServedRootTests (R7).
[Collection("FuseToolsResidentProvider")]
public sealed class FuseHostRpcOutcomeTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();

    private FuseHostService NewService(string? servedRoot = null) => new(
        _provider.GetRequiredService<SemanticIndexer>(),
        _provider.GetRequiredService<IChangeSource>(),
        _provider.GetRequiredService<ContentReductionPipeline>(),
        _provider.GetRequiredService<ISecretRedactor>(),
        _provider.GetRequiredService<IGeneratedCodeDetector>(),
        _provider.GetRequiredService<IndexCoordinator>(),
        _provider.GetRequiredService<IWorkspaceIndexJobManager>(),
        NullLogger<FuseHostService>.Instance,
        servedRoot);

    [Fact]
    public async Task Protocol_mismatch_client_treats_stale_host_as_absent()
    {
        var root = UniqueRoot();
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeTargetAsync(root, new StaleHandshakeTarget(), ready, cts.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

            var serving = await FuseHostClient.IsServingAsync(root, TimeSpan.FromSeconds(2), cts.Token);
            var overlay = await FuseHostClient.TryCheckOverlayAsync(
                root, "A.cs", "class A {}", includeAnalyzers: false, TimeSpan.FromSeconds(2), cts.Token);
            var delta = await FuseHostClient.TryCheckDeltaAsync(root, "hook", TimeSpan.FromSeconds(2), cts.Token);

            Assert.False(serving);
            Assert.Null(overlay);
            Assert.Null(delta);
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Served_root_mismatch_returns_invalid_params_over_the_wire()
    {
        Fuse.Cli.Mcp.FuseTools.ResidentWorkspaces = Fuse.Workspace.NullResidentWorkspaceProvider.Instance;
        var served = UniqueRoot();
        var other = UniqueRoot();
        Directory.CreateDirectory(served);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(served, "Widget.cs"), "public class Widget { }");

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        using var serverRpc = FuseHostConnection.Attach(
            clientToServer.Reader, serverToClient.Writer, NewService(served));

        var clientFormatter = new SystemTextJsonFormatter();
        clientFormatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FuseHostJsonContext.Default);
        clientFormatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        using var clientRpc = new JsonRpc(
            new HeaderDelimitedMessageHandler(clientToServer.Writer, serverToClient.Reader, clientFormatter));
        clientRpc.StartListening();

        var handshake = await clientRpc.InvokeAsync<FuseHostHandshake>("fuse/handshake");
        var ex = await Assert.ThrowsAnyAsync<RemoteRpcException>(() =>
            clientRpc.InvokeAsync<IndexJobStartResult>("fuse/indexStart", handshake.SessionToken, other));

        Assert.Contains("served root", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
    }

    private static string UniqueRoot() =>
        Path.Combine(Path.GetTempPath(), "fuse-host-rpc-outcome", Guid.NewGuid().ToString("N"));

    private static async Task ServeTargetAsync(
        string root, object target, TaskCompletionSource ready, CancellationToken cancellationToken)
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
                using var rpc = AttachTarget(PipeReader.Create(server), PipeWriter.Create(server), target);
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
                using var rpc = AttachTarget(PipeReader.Create(stream), PipeWriter.Create(stream), target);
                await rpc.Completion.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            try { File.Delete(socketPath); } catch (IOException) { }
        }
    }

    private static JsonRpc AttachTarget(PipeReader reader, PipeWriter writer, object target)
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FuseHostJsonContext.Default);
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        var handler = new HeaderDelimitedMessageHandler(writer, reader, formatter);
        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(target, new JsonRpcTargetOptions { AllowNonPublicInvocation = true });
        rpc.StartListening();
        return rpc;
    }

    private sealed class StaleHandshakeTarget
    {
        [JsonRpcMethod("fuse/handshake")]
        public FuseHostHandshake Handshake() =>
            new("test", FuseHostService.ProtocolVersion - 1, "token");
    }

    public void Dispose()
    {
        Fuse.Cli.Mcp.FuseTools.ResidentWorkspaces = Fuse.Workspace.NullResidentWorkspaceProvider.Instance;
        _provider.Dispose();
    }
}
