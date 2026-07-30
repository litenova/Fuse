using System.Diagnostics;
using System.Reflection;
using Fuse.Cli.Mcp;
using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Indexing;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Reduction.Security;
using Fuse.Retrieval;
using Fuse.Scoping;
using Fuse.Semantics;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Fuse.Cli.Rpc;

/// <summary>
///     The JSON-RPC surface a local client calls over the pipe transport. Its client today is the
///     ambient-verification hooks (<c>fuse check --delta</c>, <c>fuse gate</c>), which use <c>fuse/check</c>; the
///     remaining read methods are the general host surface (the seed of the G5 daemon). One instance is shared by
///     the connection for a single repository root and reads the semantic index (the same
///     <see cref="WorkspaceIndexStore" /> and <see cref="SemanticRetrievalEngine" /> the MCP tools use), so the
///     host and the agent see identical data.
/// </summary>
/// <remarks>
///     Method names use the <c>fuse/</c> namespace. A random session token is generated at host start, returned
///     from <c>fuse/handshake</c>, and required on every other RPC method. When the process is the
///     <c>fuse host</c> entry point, the served repository root is taken from <c>--directory</c> (defaulting to
///     the current directory) and every RPC method that carries a <c>root</c> parameter rejects a path that
///     does not match it. The service never throws across the wire for an expected condition; it returns a typed
///     DTO so the client can render a clear state rather than parse an error.
/// </remarks>
public sealed class FuseHostService : IAsyncDisposable, IDisposable
{
    /// <summary>
    ///     The wire protocol version. Bumped on any breaking change to a DTO or method shape so a stale in-repo
    ///     client (the hooks) and a newer host detect the mismatch at handshake instead of failing later on a
    ///     serialization error. There is no external client to mirror: the VS Code extension was removed in v4
    ///     (Decision D15), so the host is the minimal pipe endpoint the hooks need.
    /// </summary>
    public const int ProtocolVersion = 11;

    internal const int ListLimit = 100_000;

    private readonly ILogger<FuseHostService> _logger;
    private readonly SemanticIndexer _indexer;
    private readonly IndexCoordinator _indexCoordinator;
    private readonly IWorkspaceIndexJobManager _indexJobs;
    private readonly IChangeSource _changeSource;
    private readonly ContentReductionPipeline _reductionPipeline;
    private readonly ISecretRedactor _redactor;
    private readonly IGeneratedCodeDetector _generatedCodeDetector;
    private readonly string _sessionToken;
    private readonly string? _servedRoot;
    private readonly long _startTimestamp;
    private readonly TaskCompletionSource _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HostPayloadTracker _payloads;
    private readonly Fuse.Workspace.IResidentWorkspaceProvider _residentWorkspaces;
    private readonly IIndexAccessProvider _indexAccess;
    private readonly LocalIndexAccessProvider _hostIndexAccess;
    private readonly FuseMcpRuntime _runtime;
    private readonly FuseHostReadOperations _readOperations;
    private readonly FuseHostIndexOperations _indexOperations;
    private readonly FuseHostVerificationOperations _verificationOperations;
    private int _disposed;

    internal Fuse.Workspace.IResidentWorkspaceProvider ResidentWorkspaces => _residentWorkspaces;

    internal CancellationToken LifetimeToken => _lifetime.Token;

    internal IChangeSource ChangeSource => _changeSource;

    internal ContentReductionPipeline ReductionPipeline => _reductionPipeline;

    internal ISecretRedactor Redactor => _redactor;

    internal IGeneratedCodeDetector GeneratedCodeDetector => _generatedCodeDetector;

    internal ILogger<FuseHostService> Logger => _logger;

    internal SemanticIndexer Indexer => _indexer;

    internal IWorkspaceIndexJobManager IndexJobs => _indexJobs;

    internal FuseMcpRuntime Runtime => _runtime;

    internal void TrackPayload(string path) => _payloads.Track(path);

    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseHostService" /> class.
    /// </summary>
    /// <param name="context">The host-owned application dependencies shared by focused RPC operations.</param>
    /// <param name="logger">The logger for host-side diagnostics, routed away from the transport stream.</param>
    /// <param name="servedRoot">
    ///     The repository root this daemon serves. When omitted and the process is <c>fuse host</c>, the root is
    ///     resolved from <c>--directory</c> on the command line (defaulting to the current directory). When unset,
    ///     root arguments are not validated (in-process tests and non-host callers).
    /// </param>
    /// <param name="residentWorkspaces">
    ///     The resident workspace provider this daemon checks against. Tests may supply a provider that is distinct
    ///     from the host runtime's provider.
    /// </param>
    internal FuseHostService(
        FuseHostRequestContext context,
        ILogger<FuseHostService> logger,
        string? servedRoot = null,
        Fuse.Workspace.IResidentWorkspaceProvider? residentWorkspaces = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _indexer = context.Indexer;
        _indexCoordinator = context.IndexCoordinator;
        _indexJobs = context.IndexJobs;
        _changeSource = context.ChangeSource;
        _reductionPipeline = context.ReductionPipeline;
        _redactor = context.Redactor;
        _generatedCodeDetector = context.GeneratedCodeDetector;
        _logger = logger;
        _payloads = new HostPayloadTracker(logger);
        _indexAccess = context.IndexAccess ?? context.Runtime?.IndexAccess
            ?? new LocalIndexAccessProvider(_indexCoordinator, _indexJobs);
        // A daemon-owned RPC has no MCP tool response body in which to return a deferred availability header.
        // Keep the shared job running and wait for its committed syntax store until the daemon stops instead.
        _hostIndexAccess = new LocalIndexAccessProvider(
            _indexCoordinator,
            _indexJobs,
            Timeout.InfiniteTimeSpan);
        _residentWorkspaces = residentWorkspaces
            ?? context.Runtime?.ResidentWorkspaces
            ?? Fuse.Workspace.NullResidentWorkspaceProvider.Instance;
        _runtime = context.Runtime ?? new FuseMcpRuntime(
            _indexAccess,
            _residentWorkspaces,
            _indexCoordinator,
            _indexJobs,
            new WarmSolutionCache(),
            new PooledCheckWorker(),
            new OwnedProcessRunner());
        _readOperations = new FuseHostReadOperations(this);
        _indexOperations = new FuseHostIndexOperations(this);
        _verificationOperations = new FuseHostVerificationOperations(this);
        _sessionToken = FuseHostSessionToken.Generate();
        _servedRoot = servedRoot is not null
            ? NormalizeRoot(servedRoot)
            : TryResolveServedRootFromCommandLine();
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    ///     A task that completes when a client calls <c>fuse/shutdown</c>, so the host can stop serving and exit.
    /// </summary>
    public Task ShutdownRequested => _shutdownRequested.Task;

    /// <summary>The host package version, read once from the assembly.</summary>
    public static string HostVersion =>
        typeof(FuseHostService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(FuseHostService).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>
    ///     Returns the host and protocol versions so the client can confirm it is talking to a compatible host.
    /// </summary>
    /// <returns>The host version and the wire protocol version.</returns>
    [JsonRpcMethod("fuse/handshake")]
    public FuseHostHandshake Handshake()
    {
        _logger.LogInformation("Handshake: host {HostVersion}, protocol {ProtocolVersion}.", HostVersion, ProtocolVersion);
        return new FuseHostHandshake(HostVersion, ProtocolVersion, _sessionToken);
    }

    /// <summary>
    ///     Returns cheap process-level health for the status bar and index panel (host version, process id,
    ///     uptime, and working-set size shown as host RSS).
    /// </summary>
    /// <param name="sessionToken">The session token from <c>fuse/handshake</c>.</param>
    /// <returns>The host process statistics.</returns>
    /// <exception cref="LocalRpcException">The session token is missing or invalid.</exception>
    [JsonRpcMethod("fuse/stats")]
    public FuseHostStats Stats(string sessionToken)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        var uptimeMs = (long)Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        using var process = Process.GetCurrentProcess();
        return new FuseHostStats(HostVersion, Environment.ProcessId, uptimeMs, process.WorkingSet64);
    }

    /// <summary>
    ///     Starts or joins an index job for a repository root. The default job extracts syntax only; callers must
    ///     request <see cref="IndexDepth.Semantic" /> to load the compiler workspace.
    /// </summary>
    /// <param name="sessionToken">The session token from <c>fuse/handshake</c>.</param>
    /// <param name="root">The absolute repository root to index.</param>
    /// <param name="depth">The requested syntax or semantic depth.</param>
    /// <param name="force">Whether to discard existing derived data before indexing.</param>
    /// <param name="captureBundlePath">An optional portable capture bundle directory.</param>
    /// <returns>The shared job snapshot and whether this caller joined it.</returns>
    /// <exception cref="LocalRpcException">The session token is missing or invalid, or the root does not match the served root.</exception>
    [JsonRpcMethod("fuse/indexStart")]
    public Task<IndexJobStartResult> IndexStartAsync(
        string sessionToken,
        string root,
        IndexDepth depth = IndexDepth.Syntax,
        bool force = false,
        string? captureBundlePath = null)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        var resolved = Path.GetFullPath(root);
        if (!Directory.Exists(resolved))
        {
            _logger.LogWarning("Index requested for missing directory {Root}.", resolved);
            return Task.FromResult(new IndexJobStartResult(
                FuseHostIndexOperations.MissingWorkspaceSnapshot(resolved), Joined: false, Conflict: false));
        }

        return _indexJobs.StartOrJoinAsync(
            new IndexJobRequest(resolved, depth, force, captureBundlePath),
            LifetimeToken);
    }

    /// <summary>Returns the active or last completed job for a repository root.</summary>
    /// <param name="sessionToken">The session token from <c>fuse/handshake</c>.</param>
    /// <param name="root">The absolute repository root.</param>
    /// <returns>The job snapshot, or null when the daemon has not indexed this root.</returns>
    /// <exception cref="LocalRpcException">The session token is missing or invalid, or the root does not match the served root.</exception>
    [JsonRpcMethod("fuse/indexStatus")]
    public IndexJobSnapshot? IndexStatus(string sessionToken, string root)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        return _indexJobs.GetStatus(Path.GetFullPath(root));
    }

    /// <summary>Requests cancellation of the active repository job.</summary>
    /// <param name="sessionToken">The session token from <c>fuse/handshake</c>.</param>
    /// <param name="root">The absolute repository root.</param>
    /// <returns>The job snapshot after cancellation was requested, or null when no job is active.</returns>
    /// <exception cref="LocalRpcException">The session token is missing or invalid, or the root does not match the served root.</exception>
    [JsonRpcMethod("fuse/indexCancel")]
    public Task<IndexJobSnapshot?> IndexCancelAsync(string sessionToken, string root)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        return _indexJobs.CancelAsync(Path.GetFullPath(root), LifetimeToken);
    }

    /// <summary>
    ///     Prepares the syntax index for store-backed reads. A cold, incomplete, or inventory-stale store starts
    ///     or joins the daemon-owned syntax job; a current syntax store remains readable while semantic work runs.
    ///     Non-owner MCP clients call this before opening the store read-only locally.
    /// </summary>
    /// <param name="sessionToken">The session token from <c>fuse/handshake</c>.</param>
    /// <param name="root">The absolute repository root.</param>
    /// <returns>A coarse readiness result for the client to map to tool output.</returns>
    /// <exception cref="LocalRpcException">The session token is missing or invalid, or the root does not match the served root.</exception>
    [JsonRpcMethod("fuse/openIndexed")]
    public Task<OpenIndexedResultDto> OpenIndexedAsync(string sessionToken, string root)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        return _indexOperations.OpenIndexedAsync(Path.GetFullPath(root));
    }

    /// <summary>
    ///     Projects the semantic dependency graph for a repository root: nodes are files with the symbols they
    ///     declare, a degree-based centrality, and an estimated token cost; edges are the typed dependency edges
    ///     resolved to file pairs. An optional scope overlay tags each node with the role a fusion would give it.
    /// </summary>
    /// <param name="sessionToken">The session token from <c>fuse/handshake</c>.</param>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="detail"><c>Files</c> for a node per file, or <c>Directories</c> for directory supernodes.</param>
    /// <param name="scopeMode">Optional scoping mode (<c>focus</c>, <c>search</c>, <c>changes</c>) for a role overlay.</param>
    /// <param name="seed">The focus seed when <paramref name="scopeMode" /> is <c>focus</c>.</param>
    /// <param name="query">The search query when <paramref name="scopeMode" /> is <c>search</c>.</param>
    /// <param name="since">The git base ref when <paramref name="scopeMode" /> is <c>changes</c>.</param>
    /// <param name="directory">Optional subdirectory to restrict a file-level projection to.</param>
    /// <returns>The graph nodes and edges at the requested level of detail.</returns>
    /// <exception cref="LocalRpcException">The session token is missing or invalid, or the root does not match the served root.</exception>
    [JsonRpcMethod("fuse/graph")]
    public Task<GraphDto> GraphAsync(
        string sessionToken, string root, string detail, string? scopeMode = null, string? seed = null, string? query = null,
        string? since = null, string? directory = null)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        return _readOperations.GraphAsync(Path.GetFullPath(root), detail, scopeMode, seed, query, since, directory);
    }

    /// <summary>
    ///     Plans and emits a scoped context payload from the semantic index, and returns the included files with
    ///     their token costs plus a path to the written payload the extension opens read-only.
    /// </summary>
    /// <param name="sessionToken">The session token from <c>fuse/handshake</c>.</param>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="mode">The scoping mode: <c>focus</c>, <c>changes</c>, or anything else for <c>search</c>.</param>
    /// <param name="seed">The focus seed (symbol or file) when <paramref name="mode" /> is <c>focus</c>.</param>
    /// <param name="query">The search query when <paramref name="mode" /> is <c>search</c>.</param>
    /// <param name="since">The git base when <paramref name="mode" /> is <c>changes</c>.</param>
    /// <param name="maxTokens">The token budget for the emitted payload, or <c>0</c> for unbounded.</param>
    /// <returns>The included files with token costs, the total tokens, and the payload file path.</returns>
    /// <exception cref="LocalRpcException">The session token is missing or invalid, or the root does not match the served root.</exception>
    [JsonRpcMethod("fuse/scope")]
    public Task<ScopeResultDto> ScopeAsync(
        string sessionToken, string root, string mode, string? seed, string? query, string? since, int maxTokens)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        return _readOperations.ScopeAsync(Path.GetFullPath(root), mode, seed, query, since, maxTokens);
    }

    /// <summary>
    ///     Explains what a scoped fusion would include without writing a payload: returns each planned file's
    ///     role, render tier, and score from the semantic context plan.
    /// </summary>
    /// <param name="sessionToken">The session token from <c>fuse/handshake</c>.</param>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="mode">The scoping mode: <c>focus</c>, <c>changes</c>, or anything else for <c>search</c>.</param>
    /// <param name="seed">The focus seed when <paramref name="mode" /> is <c>focus</c>.</param>
    /// <param name="query">The search query when <paramref name="mode" /> is <c>search</c>.</param>
    /// <param name="since">The git base when <paramref name="mode" /> is <c>changes</c>.</param>
    /// <returns>The scoping mode and the planned files with their roles, tiers, and scores.</returns>
    /// <exception cref="LocalRpcException">The session token is missing or invalid, or the root does not match the served root.</exception>
    [JsonRpcMethod("fuse/explain")]
    public Task<ExplainResultDto> ExplainAsync(
        string sessionToken, string root, string mode, string? seed, string? query, string? since)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        return _readOperations.ExplainAsync(Path.GetFullPath(root), mode, seed, query, since);
    }

    /// <summary>
    ///     Scans the repository for context diagnostics from the semantic index: secret findings with precise
    ///     editor ranges (computed read-only with the same redactor the reduction path uses), the most
    ///     token-expensive files, files with no dependency edge, and files flagged as generated.
    /// </summary>
    /// <param name="sessionToken">The session token from <c>fuse/handshake</c>.</param>
    /// <param name="root">The absolute repository root.</param>
    /// <returns>The detected secrets, hotspots, graph gaps, and generated files.</returns>
    /// <exception cref="LocalRpcException">The session token is missing or invalid, or the root does not match the served root.</exception>
    [JsonRpcMethod("fuse/diagnostics")]
    public Task<DiagnosticsDto> DiagnosticsAsync(string sessionToken, string root)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        return _readOperations.DiagnosticsAsync(Path.GetFullPath(root));
    }

    /// <summary>
    ///     Signals the host to flush and exit. The transport completes the in-flight response before the host
    ///     stops serving.
    /// </summary>
    /// <param name="sessionToken">The session token from <c>fuse/handshake</c>.</param>
    /// <exception cref="LocalRpcException">The session token is missing or invalid.</exception>
    [JsonRpcMethod("fuse/shutdown")]
    public void Shutdown(string sessionToken)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        _logger.LogInformation("Shutdown requested by client.");
        _payloads.DeleteTrackedPayloads();
        _lifetime.Cancel();
        _shutdownRequested.TrySetResult();
    }

    /// <summary>
    ///     Returns the diagnostics a check session's edits introduced or resolved since its baseline (S3 ambient
    ///     verification): the delta the harness hook renders after an edit and the gate blocks a red turn on.
    /// </summary>
    /// <param name="sessionToken">The session token from <c>fuse/handshake</c>.</param>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="session">The opaque check-session id whose baseline the delta is measured against.</param>
    /// <returns>
    ///     The introduced and resolved diagnostics. When no resident workspace serves the root,
    ///     <see cref="CheckDeltaDto.Resident" /> is false and both lists are empty, so a hook exits silently rather
    ///     than blocking editing (delta mode never runs a build). The first call for a session establishes its
    ///     baseline and returns an empty delta.
    /// </returns>
    /// <exception cref="LocalRpcException">The session token is missing or invalid, or the root does not match the served root.</exception>
    [JsonRpcMethod("fuse/check")]
    public Task<CheckDeltaDto> CheckDeltaAsync(string sessionToken, string root, string session)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        return _verificationOperations.CheckDeltaAsync(Path.GetFullPath(root), session);
    }

    /// <summary>
    ///     Typechecks a proposed single-file edit against the daemon's live resident workspace and returns the
    ///     diagnostics, with no build (G5). This is the resident-grade check a non-owner process delegates over the
    ///     pipe, so one daemon-held compilation serves every client instead of each process holding its own.
    /// </summary>
    /// <param name="sessionToken">The session token from <c>fuse/handshake</c>.</param>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="includeAnalyzers">Whether to also run the repository's analyzers and nullable warnings.</param>
    /// <returns>
    ///     The diagnostics for the changed document; when no resident workspace serves the root,
    ///     <see cref="CheckOverlayResultDto.HasResident" /> is false and the caller falls back to its own path.
    /// </returns>
    /// <exception cref="LocalRpcException">The session token is missing or invalid, or the root does not match the served root.</exception>
    [JsonRpcMethod("fuse/checkOverlay")]
    public Task<CheckOverlayResultDto> CheckOverlayAsync(
        string sessionToken, string root, string relativeFilePath, string newContent, bool includeAnalyzers)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        return _verificationOperations.CheckOverlayAsync(
            Path.GetFullPath(root),
            relativeFilePath,
            newContent,
            includeAnalyzers);
    }

    /// <summary>Runs a live doctor load through this root's held warm solution cache.</summary>
    [JsonRpcMethod("fuse/doctor")]
    public Task<DoctorResultDto> DoctorAsync(string sessionToken, string root)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        return _verificationOperations.DoctorAsync(Path.GetFullPath(root));
    }

    /// <summary>Runs a staged compiler refactor through this root's held warm solution cache.</summary>
    [JsonRpcMethod("fuse/refactor")]
    public Task<RefactorResultDto> RefactorAsync(string sessionToken, string root, RefactorRequestDto request)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        return _verificationOperations.RefactorAsync(Path.GetFullPath(root), request);
    }

    /// <summary>Runs a capture-bundle oracle check through this host's pooled worker ownership domain.</summary>
    [JsonRpcMethod("fuse/checkCapture")]
    public Task<CaptureCheckResultDto> CheckCaptureAsync(
        string sessionToken, string root, string relativeFilePath, string newContent)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        return _verificationOperations.CheckCaptureAsync(Path.GetFullPath(root), relativeFilePath, newContent);
    }

    /// <summary>Reserves the daemon's per-root budget for an opt-in resident workspace.</summary>
    public void ActivateResidentBudget(string root)
    {
        ValidateServedRoot(root);
        _verificationOperations.ActivateResidentBudget(Path.GetFullPath(root));
    }

    // Confirms the caller's root matches the repository root this daemon was started to serve.
    private void ValidateServedRoot(string root)
    {
        if (_servedRoot is null)
            return;

        var requested = NormalizeRoot(root);
        if (string.Equals(_servedRoot, requested, StringComparison.Ordinal))
            return;

        _logger.LogWarning("Rejected RPC for root {Requested}; daemon serves {Served}.", requested, _servedRoot);
        throw new LocalRpcException("Root does not match the daemon served root.")
        {
            ErrorCode = (int)JsonRpcErrorCode.InvalidParams,
        };
    }

    // The same normalization HostEndpoint uses when hashing a root, so equivalent paths compare equal.
    private static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToLowerInvariant();

    // Resolves the served root when this process is the fuse host entry point (--directory, else cwd).
    private static string? TryResolveServedRootFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        var hostIndex = Array.FindIndex(args, static a => string.Equals(a, "host", StringComparison.OrdinalIgnoreCase));
        if (hostIndex < 0)
            return null;

        for (var i = hostIndex + 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--directory", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[i], "-d", StringComparison.OrdinalIgnoreCase))
                return NormalizeRoot(args[i + 1]);
        }

        return NormalizeRoot(Directory.GetCurrentDirectory());
    }

    /// <summary>
    ///     Deletes any scope payload files written during this host session. Called from <c>fuse/shutdown</c> and
    ///     when the host service is disposed.
    /// </summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Cancel();
        try
        {
            _payloads.DeleteTrackedPayloads();
            await _indexJobs.DisposeAsync();
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    internal Task<WorkspaceIndexStore> OpenIndexedForHostAsync(string root, CancellationToken cancellationToken) =>
        _hostIndexAccess.OpenIndexedAsync(_indexer, root, cancellationToken);

}
