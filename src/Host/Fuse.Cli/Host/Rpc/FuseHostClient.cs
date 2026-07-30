using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Fuse.Cli.Mcp;
using StreamJsonRpc;

namespace Fuse.Cli.Rpc;

/// <summary>
///     Opt-in stderr logging for hook RPC failures when <c>FUSE_HOOK_VERBOSE</c> is set.
/// </summary>
internal static class FuseHookVerbose
{
    /// <summary>The environment variable that enables hook RPC failure logging.</summary>
    internal const string EnvironmentVariable = "FUSE_HOOK_VERBOSE";

    /// <summary>Whether hook RPC failure logging is enabled for this process.</summary>
    /// <returns>True when <see cref="EnvironmentVariable" /> is set to a truthy value.</returns>
    internal static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return value is not null
            && (value.Equals("1", StringComparison.Ordinal)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Writes one stderr line naming the RPC method and failure code when verbose mode is on.</summary>
    /// <param name="method">The RPC method that failed (or was about to run).</param>
    /// <param name="errorCode">A stable failure code (<c>no_host</c>, <c>protocol_mismatch</c>, or <c>rpc_error</c>).</param>
    internal static void LogRpcFailure(string method, string errorCode)
    {
        if (!IsEnabled())
            return;

        Console.Error.WriteLine($"fuse hook rpc: {method} failed ({errorCode})");
    }
}

/// <summary>
///     A short-lived client to a running <c>fuse host</c> / <c>fuse mcp serve</c> over the UI transport, used by
///     the S3 ambient-verification commands (<c>fuse check --delta</c>, <c>fuse gate</c>) to fetch the
///     <c>fuse/check</c> delta from the resident process. It connects to the deterministic endpoint for a root
///     (<see cref="HostEndpoint" />), handshakes, invokes <c>fuse/check</c>, and disposes the connection.
/// </summary>
/// <remarks>
///     Never throws for the absence of a host: when no process is serving the root (the endpoint is missing or the
///     connect times out) or the host protocol does not match, it returns <c>null</c>, so a hook exits silently and
///     never blocks editing. The connect timeout bounds the no-host probe; a live host on the local machine
///     connects well inside it. Set <c>FUSE_HOOK_VERBOSE=1</c> to log swallowed RPC failures to stderr.
/// </remarks>
public static class FuseHostClient
{
    /// <summary>
    ///     Reads a daemon handshake without requiring the running protocol to match this binary. Lifecycle callers
    ///     use this only to retire a stale daemon before starting the matching host.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="connectTimeout">How long to wait for a connection.</param>
    /// <param name="cancellationToken">A token to cancel the probe.</param>
    /// <returns>The running handshake, or null when no daemon answered.</returns>
    public static async Task<FuseHostHandshake?> TryHandshakeAsync(
        string root,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        Stream? stream = null;
        try
        {
            stream = await ConnectAsync(root, connectTimeout, cancellationToken);
            if (stream is null)
                return null;

            using var rpc = CreateRpc(stream, CreateFormatter());
            rpc.StartListening();
            return await rpc.InvokeWithCancellationAsync<FuseHostHandshake>("fuse/handshake", [], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (stream is not null)
                await stream.DisposeAsync();
        }
    }

    /// <summary>Reads daemon process statistics without applying the current protocol gate.</summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="connectTimeout">How long to wait for a connection.</param>
    /// <param name="cancellationToken">A token to cancel the probe.</param>
    /// <returns>The remote process statistics, or null when the call did not complete.</returns>
    public static Task<FuseHostStats?> TryStatsIgnoringProtocolAsync(
        string root,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken) =>
        TryInvokeIgnoringProtocolAsync(
            root,
            connectTimeout,
            (rpc, handshake, ct) => rpc.InvokeWithCancellationAsync<FuseHostStats>(
                "fuse/stats", [handshake.SessionToken], ct),
            cancellationToken);

    /// <summary>Asks a daemon to stop without applying the current protocol gate.</summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="connectTimeout">How long to wait for a connection.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>True when the daemon accepted the shutdown notification.</returns>
    public static async Task<bool> TryShutdownIgnoringProtocolAsync(
        string root,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        var accepted = await TryInvokeIgnoringProtocolAsync(
            root,
            connectTimeout,
            async (rpc, handshake, ct) =>
            {
                await rpc.NotifyAsync("fuse/shutdown", [handshake.SessionToken]).WaitAsync(ct);
                return true;
            },
            cancellationToken);
        return accepted == true;
    }

    /// <summary>
    ///     Asks the running host for the check delta of a session, or returns <c>null</c> when no compatible host
    ///     serves the root.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="session">The check-session id whose baseline the delta is measured against.</param>
    /// <param name="connectTimeout">How long to wait for a connection before giving up (the no-host probe bound).</param>
    /// <param name="cancellationToken">A token to cancel the call.</param>
    /// <returns>The delta from <c>fuse/check</c>, or <c>null</c> when no compatible host serves the root.</returns>
    public static Task<CheckDeltaDto?> TryCheckDeltaAsync(
        string root, string session, TimeSpan connectTimeout, CancellationToken cancellationToken) =>
        TryInvokeAsync(
            root,
            "fuse/check",
            connectTimeout,
            (rpc, handshake, ct) => rpc.InvokeWithCancellationAsync<CheckDeltaDto>(
                "fuse/check", [handshake.SessionToken, root, session], ct),
            cancellationToken);

    /// <summary>
    ///     Asks the daemon serving a root to typecheck a proposed single-file edit against its live resident
    ///     workspace (G5), or returns null when no compatible daemon serves the root. This is how a non-owner
    ///     process gets a resident-grade check over the pipe without holding its own workspace.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="includeAnalyzers">Whether to also run the repository's analyzers and nullable warnings.</param>
    /// <param name="connectTimeout">How long to wait for a connection before concluding no daemon serves the root.</param>
    /// <param name="cancellationToken">A token to cancel the call.</param>
    /// <returns>The overlay result, or null when no compatible daemon serves the root.</returns>
    public static Task<CheckOverlayResultDto?> TryCheckOverlayAsync(
        string root, string relativeFilePath, string newContent, bool includeAnalyzers, TimeSpan connectTimeout, CancellationToken cancellationToken) =>
        TryInvokeAsync(
            root,
            "fuse/checkOverlay",
            connectTimeout,
            (rpc, handshake, ct) => rpc.InvokeWithCancellationAsync<CheckOverlayResultDto>(
                "fuse/checkOverlay", [handshake.SessionToken, root, relativeFilePath, newContent, includeAnalyzers], ct),
            cancellationToken);

    /// <summary>
    ///     Fetches a running daemon's stats for a root (G5), or returns null when no compatible daemon serves it.
    ///     Used by <c>fuse workspace status</c> to name the daemon's PID, uptime, and memory so a developer can see
    ///     and stop it. Never throws for the absence of a daemon.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="connectTimeout">How long to wait for a connection before concluding no daemon serves the root.</param>
    /// <param name="cancellationToken">A token to cancel the call.</param>
    /// <returns>The daemon stats, or null when no compatible daemon serves the root.</returns>
    public static Task<FuseHostStats?> TryStatsAsync(
        string root, TimeSpan connectTimeout, CancellationToken cancellationToken) =>
        TryInvokeAsync(
            root,
            "fuse/stats",
            connectTimeout,
            (rpc, handshake, ct) => rpc.InvokeWithCancellationAsync<FuseHostStats>(
                "fuse/stats", [handshake.SessionToken], ct),
            cancellationToken);

    /// <summary>
    ///     Asks the daemon to prepare the workspace index for store-backed reads (R19), or returns null when no
    ///     compatible daemon serves the root. Never throws for the absence of a daemon.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="connectTimeout">How long to wait for a connection before concluding no daemon serves the root.</param>
    /// <param name="cancellationToken">A token to cancel the call.</param>
    /// <returns>The open-indexed result, or null when no compatible daemon serves the root.</returns>
    public static Task<OpenIndexedResultDto?> TryOpenIndexedAsync(
        string root, TimeSpan connectTimeout, CancellationToken cancellationToken) =>
        TryInvokeAsync(
            root,
            "fuse/openIndexed",
            connectTimeout,
            (rpc, handshake, ct) => rpc.InvokeWithCancellationAsync<OpenIndexedResultDto>(
                "fuse/openIndexed", [handshake.SessionToken, root], ct),
            cancellationToken);

    /// <summary>
    ///     Starts or joins an index job on the daemon, or returns null when no compatible daemon serves the root.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="depth">The requested syntax or semantic index depth.</param>
    /// <param name="force">Whether to discard existing derived index data first.</param>
    /// <param name="captureBundlePath">An optional capture bundle directory.</param>
    /// <param name="connectTimeout">How long to wait for a connection before concluding no daemon serves the root.</param>
    /// <param name="cancellationToken">A token to cancel the call.</param>
    /// <returns>The job acceptance result, or null when no compatible daemon serves the root.</returns>
    public static Task<IndexJobStartResult?> TryIndexStartAsync(
        string root,
        IndexDepth depth,
        bool force,
        string? captureBundlePath,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken) =>
        TryInvokeAsync(
            root,
            "fuse/indexStart",
            connectTimeout,
            (rpc, handshake, ct) => rpc.InvokeWithCancellationAsync<IndexJobStartResult>(
                "fuse/indexStart", [handshake.SessionToken, root, depth, force, captureBundlePath], ct),
            cancellationToken);

    /// <summary>Gets the daemon-owned job snapshot for a repository root.</summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="connectTimeout">How long to wait for a connection before concluding no daemon serves the root.</param>
    /// <param name="cancellationToken">A token to cancel the call.</param>
    /// <returns>The active or retained job snapshot, or null when no daemon or job exists.</returns>
    public static Task<IndexJobSnapshot?> TryIndexStatusAsync(
        string root, TimeSpan connectTimeout, CancellationToken cancellationToken) =>
        TryInvokeAsync(
            root,
            "fuse/indexStatus",
            connectTimeout,
            (rpc, handshake, ct) => rpc.InvokeWithCancellationAsync<IndexJobSnapshot?>(
                "fuse/indexStatus", [handshake.SessionToken, root], ct),
            cancellationToken);

    /// <summary>Requests cancellation of the daemon-owned job for a repository root.</summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="connectTimeout">How long to wait for a connection before concluding no daemon serves the root.</param>
    /// <param name="cancellationToken">A token to cancel the call.</param>
    /// <returns>The updated job snapshot, or null when no daemon or active job exists.</returns>
    public static Task<IndexJobSnapshot?> TryIndexCancelAsync(
        string root, TimeSpan connectTimeout, CancellationToken cancellationToken) =>
        TryInvokeAsync(
            root,
            "fuse/indexCancel",
            connectTimeout,
            (rpc, handshake, ct) => rpc.InvokeWithCancellationAsync<IndexJobSnapshot?>(
                "fuse/indexCancel", [handshake.SessionToken, root], ct),
            cancellationToken);

    /// <summary>Runs a live doctor request on the root's daemon, or returns null when no compatible daemon serves it.</summary>
    public static Task<DoctorResultDto?> TryDoctorAsync(string root, TimeSpan connectTimeout, CancellationToken cancellationToken) =>
        TryInvokeAsync(root, "fuse/doctor", connectTimeout,
            (rpc, handshake, ct) => rpc.InvokeWithCancellationAsync<DoctorResultDto>("fuse/doctor", [handshake.SessionToken, root], ct), cancellationToken);

    /// <summary>Runs a staged refactor on the root's daemon, or returns null when no compatible daemon serves it.</summary>
    public static Task<RefactorResultDto?> TryRefactorAsync(
        string root, RefactorRequestDto request, TimeSpan connectTimeout, CancellationToken cancellationToken) =>
        TryInvokeAsync(root, "fuse/refactor", connectTimeout,
            (rpc, handshake, ct) => rpc.InvokeWithCancellationAsync<RefactorResultDto>("fuse/refactor", [handshake.SessionToken, root, request], ct), cancellationToken);

    /// <summary>Runs a capture-backed oracle check on the root's daemon, or returns null when no compatible daemon serves it.</summary>
    public static Task<CaptureCheckResultDto?> TryCaptureCheckAsync(
        string root, string relativeFilePath, string newContent, TimeSpan connectTimeout, CancellationToken cancellationToken) =>
        TryInvokeAsync(root, "fuse/checkCapture", connectTimeout,
            (rpc, handshake, ct) => rpc.InvokeWithCancellationAsync<CaptureCheckResultDto>("fuse/checkCapture", [handshake.SessionToken, root, relativeFilePath, newContent], ct), cancellationToken);

    /// <summary>
    ///     Probes whether a compatible daemon is serving a root (G5): connects to the root's endpoint and completes
    ///     the handshake, returning true only when a process answers and its protocol version matches. Used by the
    ///     daemon supervisor to decide whether to spawn a daemon or connect to the running one; never throws for the
    ///     absence of a daemon.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="connectTimeout">How long to wait for a connection before concluding no daemon serves the root.</param>
    /// <param name="cancellationToken">A token to cancel the probe.</param>
    /// <returns>True when a compatible daemon serves the root; false otherwise.</returns>
    public static async Task<bool> IsServingAsync(string root, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        Stream? stream = null;
        var activeMethod = "connect";
        try
        {
            stream = await ConnectAsync(root, connectTimeout, cancellationToken);
            if (stream is null)
            {
                FuseHookVerbose.LogRpcFailure("fuse/handshake", "no_host");
                return false;
            }

            using var rpc = CreateRpc(stream, CreateFormatter());
            rpc.StartListening();

            activeMethod = "fuse/handshake";
            var handshake = await rpc.InvokeWithCancellationAsync<FuseHostHandshake>(
                activeMethod, [], cancellationToken);
            if (handshake.ProtocolVersion != FuseHostService.ProtocolVersion)
            {
                FuseHookVerbose.LogRpcFailure(activeMethod, "protocol_mismatch");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FuseHookVerbose.LogRpcFailure(activeMethod, "rpc_error");
            return false;
        }
        finally
        {
            if (stream is not null)
                await stream.DisposeAsync();
        }
    }

    private static async Task<T?> TryInvokeAsync<T>(
        string root,
        string method,
        TimeSpan connectTimeout,
        Func<JsonRpc, FuseHostHandshake, CancellationToken, Task<T>> invoke,
        CancellationToken cancellationToken)
    {
        Stream? stream = null;
        var activeMethod = "connect";
        try
        {
            stream = await ConnectAsync(root, connectTimeout, cancellationToken);
            if (stream is null)
            {
                FuseHookVerbose.LogRpcFailure(method, "no_host");
                return default;
            }

            using var rpc = CreateRpc(stream, CreateFormatter());
            rpc.StartListening();

            activeMethod = "fuse/handshake";
            var handshake = await rpc.InvokeWithCancellationAsync<FuseHostHandshake>(
                activeMethod, [], cancellationToken);
            // A protocol mismatch means a stale host; treat it as no host rather than risk a malformed exchange.
            if (handshake.ProtocolVersion != FuseHostService.ProtocolVersion)
            {
                FuseHookVerbose.LogRpcFailure(activeMethod, "protocol_mismatch");
                return default;
            }

            activeMethod = method;
            return await invoke(rpc, handshake, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No host, a stale endpoint, or a transient RPC error: stay silent unless verbose mode is on.
            FuseHookVerbose.LogRpcFailure(activeMethod, "rpc_error");
            return default;
        }
        finally
        {
            if (stream is not null)
                await stream.DisposeAsync();
        }
    }

    private static async Task<T?> TryInvokeIgnoringProtocolAsync<T>(
        string root,
        TimeSpan connectTimeout,
        Func<JsonRpc, FuseHostHandshake, CancellationToken, Task<T>> invoke,
        CancellationToken cancellationToken)
    {
        Stream? stream = null;
        try
        {
            stream = await ConnectAsync(root, connectTimeout, cancellationToken);
            if (stream is null)
                return default;

            using var rpc = CreateRpc(stream, CreateFormatter());
            rpc.StartListening();
            var handshake = await rpc.InvokeWithCancellationAsync<FuseHostHandshake>(
                "fuse/handshake", [], cancellationToken);
            return await invoke(rpc, handshake, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return default;
        }
        finally
        {
            if (stream is not null)
                await stream.DisposeAsync();
        }
    }

    private static SystemTextJsonFormatter CreateFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FuseHostJsonContext.Default);
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        return formatter;
    }

    private static JsonRpc CreateRpc(Stream stream, SystemTextJsonFormatter formatter) =>
        new(new HeaderDelimitedMessageHandler(
            PipeWriter.Create(stream), PipeReader.Create(stream), formatter));

    // Connects to the deterministic endpoint for a root: a named pipe on Windows, a Unix domain socket elsewhere.
    // Returns null (not throw) when nothing is serving, so the caller stays silent.
    private static async Task<Stream?> ConnectAsync(string root, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipeName = HostEndpoint.PipeName(root);
            // A non-existent named pipe makes ConnectAsync poll for the whole timeout before failing, which would
            // put that full delay on the no-host hot path. Windows lists live pipes under the \\.\pipe\ device
            // namespace, so enumerating it short-circuits the no-host case to (effectively) zero probe latency; the
            // connect timeout then only bounds a pipe that exists but is momentarily busy. (File.Exists does not
            // detect a named pipe, so the directory enumeration is the reliable check.)
            if (!WindowsPipeExists(pipeName))
                return null;

            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync((int)connectTimeout.TotalMilliseconds, cancellationToken);
                return client;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await client.DisposeAsync();
                return null;
            }
        }

        return await ConnectUnixAsync(root, connectTimeout, cancellationToken);
    }

    // Whether a named pipe with the given name is currently served. The \\.\pipe\ device directory lists every
    // live pipe; enumerating it is reliable where File.Exists is not. On any enumeration failure this returns true
    // so the caller still attempts a real connect rather than wrongly short-circuiting to "no host".
    private static bool WindowsPipeExists(string pipeName)
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\")
                .Any(p => string.Equals(Path.GetFileName(p), pipeName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static async Task<Stream?> ConnectUnixAsync(string root, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        var socketPath = HostEndpoint.SocketPath(root);
        if (!File.Exists(socketPath))
            return null; // No socket file means no host is bound for this root.

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(connectTimeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeoutCts.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            socket.Dispose();
            return null;
        }
    }
}
