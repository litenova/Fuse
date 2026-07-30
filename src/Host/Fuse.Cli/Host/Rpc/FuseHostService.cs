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

    private const int ListLimit = 100_000;

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
    private readonly object _payloadLock = new();
    private readonly HashSet<string> _payloadPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Fuse.Workspace.IResidentWorkspaceProvider _residentWorkspaces;
    private readonly IIndexAccessProvider _indexAccess;
    private readonly FuseMcpRuntime _runtime;
    private CompilerStateBudget? _compilerStateBudget;
    private int _disposed;

    private Fuse.Workspace.IResidentWorkspaceProvider ResidentWorkspaces => _residentWorkspaces;

    private CancellationToken LifetimeToken => _lifetime.Token;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseHostService" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer that builds and refreshes the workspace index.</param>
    /// <param name="changeSource">The git change source for review (changes) scoping.</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render context payloads.</param>
    /// <param name="redactor">The secret redactor, used read-only to locate secret spans for diagnostics.</param>
    /// <param name="generatedCodeDetector">Detects machine-generated C# (for example EF Core migrations) for diagnostics.</param>
    /// <param name="indexCoordinator">The daemon-owned index store coordinator.</param>
    /// <param name="indexJobs">The daemon-owned index job manager.</param>
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
    /// <param name="runtime">The host-owned MCP runtime, supplied by dependency injection in production.</param>
    public FuseHostService(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        ContentReductionPipeline reductionPipeline,
        ISecretRedactor redactor,
        IGeneratedCodeDetector generatedCodeDetector,
        IndexCoordinator indexCoordinator,
        IWorkspaceIndexJobManager indexJobs,
        ILogger<FuseHostService> logger,
        string? servedRoot = null,
        Fuse.Workspace.IResidentWorkspaceProvider? residentWorkspaces = null,
        FuseMcpRuntime? runtime = null)
    {
        _indexer = indexer;
        _indexCoordinator = indexCoordinator;
        _indexJobs = indexJobs;
        _changeSource = changeSource;
        _reductionPipeline = reductionPipeline;
        _redactor = redactor;
        _generatedCodeDetector = generatedCodeDetector;
        _logger = logger;
        _indexAccess = runtime?.IndexAccess ?? new LocalIndexAccessProvider(indexCoordinator, indexJobs);
        _residentWorkspaces = residentWorkspaces
            ?? runtime?.ResidentWorkspaces
            ?? Fuse.Workspace.NullResidentWorkspaceProvider.Instance;
        _runtime = runtime ?? new FuseMcpRuntime(
            _indexAccess,
            _residentWorkspaces,
            indexCoordinator,
            indexJobs,
            new WarmSolutionCache(),
            new PooledCheckWorker(),
            new OwnedProcessRunner());
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
                MissingWorkspaceSnapshot(resolved), Joined: false, Conflict: false));
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
    public async Task<OpenIndexedResultDto> OpenIndexedAsync(string sessionToken, string root)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        var resolved = Path.GetFullPath(root);
        if (!Directory.Exists(resolved))
            return new OpenIndexedResultDto("not_indexed", "workspace directory not found", 0, null);

        var job = _indexJobs.GetStatus(resolved);
        try
        {
            await using var store = new WorkspaceIndexStore(FuseStorePaths.ResolveDatabasePath(resolved));
            if (await store.OpenForReadAsync(LifetimeToken) is WorkspaceIndexReadOpenStatus.Ready)
            {
                var state = await store.GetStateAsync(LifetimeToken);
                var manifest = await WorkspaceIndexManifest.ValidateAsync(resolved, store, LifetimeToken);
                if (manifest.Ready && await _indexer.IsInventoryCurrentAsync(resolved, store, LifetimeToken))
                    return new OpenIndexedResultDto("ready", null, state.FileCount, state.Mode, job);
            }
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogDebug(ex, "The index was not readable for {Root}.", resolved);
        }

        var started = await _indexJobs.StartOrJoinAsync(
            new IndexJobRequest(resolved, IndexDepth.Syntax, Force: false, CaptureBundlePath: null),
            LifetimeToken);
        return new OpenIndexedResultDto(
            "index_rebuilding",
            "syntax index is refreshing; call fuse/indexStatus for progress",
            0,
            null,
            started.Snapshot);
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
    public async Task<GraphDto> GraphAsync(
        string sessionToken, string root, string detail, string? scopeMode = null, string? seed = null, string? query = null,
        string? since = null, string? directory = null)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        var resolved = Path.GetFullPath(root);
        var expandDirectory = !string.IsNullOrWhiteSpace(directory);
        var directories = !expandDirectory && string.Equals(detail, "Directories", StringComparison.OrdinalIgnoreCase);
        if (!Directory.Exists(resolved))
            return new GraphDto([], [], directories ? "Directories" : "Files");

        await using var store = await OpenIndexedForHostAsync(resolved, LifetimeToken);

        var files = await store.FindFilesByPathAsync(string.Empty, ListLimit, LifetimeToken);
        var tokenByPath = await store.GetFileTokenEstimatesAsync(LifetimeToken);
        var edges = await store.GetFileDependencyEdgesAsync(LifetimeToken);

        // Declared symbol names per file, for the node label and hover.
        var typesByPath = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var symbol in await store.ListSymbolsAsync(ListLimit, LifetimeToken))
        {
            if (!typesByPath.TryGetValue(symbol.FilePath, out var names))
                typesByPath[symbol.FilePath] = names = [];
            if (names.Count < 25 && !names.Contains(symbol.Name))
                names.Add(symbol.Name);
        }

        // Degree-based centrality: a file's in+out edge count, normalized to [0, 1] by the busiest file.
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            degree[edge.FromPath] = degree.GetValueOrDefault(edge.FromPath) + 1;
            degree[edge.ToPath] = degree.GetValueOrDefault(edge.ToPath) + 1;
        }
        var maxDegree = degree.Count == 0 ? 1 : degree.Values.Max();

        // Optional scope overlay: tag each file with the role a fusion would give it, so the webview recolors.
        var roleByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(scopeMode))
        {
            var (_, plan) = await PlanScopeAsync(store, resolved, scopeMode!, seed, query, since, 0);
            foreach (var item in plan.Items)
                roleByPath[item.Path] = item.Role;
        }

        var fileNodes = files.Select(f => new GraphNodeDto(
            f.NormalizedPath,
            typesByPath.GetValueOrDefault(f.NormalizedPath, []),
            Math.Round(degree.GetValueOrDefault(f.NormalizedPath) / (double)maxDegree, 4),
            tokenByPath.GetValueOrDefault(f.NormalizedPath),
            roleByPath.GetValueOrDefault(f.NormalizedPath))).ToList();

        var fileEdges = edges
            .GroupBy(e => (e.FromPath, e.ToPath))
            .Select(g => new GraphEdgeDto(g.Key.FromPath, g.Key.ToPath, g.Count(), g.First().Kind))
            .ToList();

        if (expandDirectory)
        {
            var prefix = directory!.Replace('\\', '/').TrimEnd('/') + "/";
            bool Under(string p) => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            var subNodes = fileNodes.Where(n => Under(n.Path)).ToList();
            var subPaths = new HashSet<string>(subNodes.Select(n => n.Path), StringComparer.OrdinalIgnoreCase);
            var subEdges = fileEdges.Where(e => subPaths.Contains(e.From) && subPaths.Contains(e.To)).ToList();
            return new GraphDto(subNodes, subEdges, "Files");
        }

        if (!directories)
            return new GraphDto(fileNodes, fileEdges, "Files");

        return AggregateToDirectories(fileNodes, fileEdges);
    }

    // Folds the file graph into directory supernodes: a node per directory (token cost and centrality summed for
    // relative sizing) and one edge per distinct cross-directory reference, so a large repository ships a small
    // graph the webview expands on demand.
    private static GraphDto AggregateToDirectories(IReadOnlyList<GraphNodeDto> fileNodes, IReadOnlyList<GraphEdgeDto> fileEdges)
    {
        static string DirectoryOf(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash <= 0 ? "." : path[..slash];
        }

        var byDir = new Dictionary<string, (double Centrality, int Tokens, int Files)>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in fileNodes)
        {
            var dir = DirectoryOf(node.Path);
            var acc = byDir.GetValueOrDefault(dir);
            byDir[dir] = (acc.Centrality + node.Centrality, acc.Tokens + node.TokenCost, acc.Files + 1);
        }

        var dirNodes = byDir
            .Select(kv => new GraphNodeDto(kv.Key, [$"{kv.Value.Files} files"], Math.Round(kv.Value.Centrality, 4), kv.Value.Tokens, null))
            .ToList();

        var dirEdges = fileEdges
            .Select(e => (From: DirectoryOf(e.From), To: DirectoryOf(e.To)))
            .Where(e => !string.Equals(e.From, e.To, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => (e.From, e.To))
            .Select(g => new GraphEdgeDto(g.Key.From, g.Key.To, g.Count(), "reference"))
            .ToList();

        return new GraphDto(dirNodes, dirEdges, "Directories");
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
    public async Task<ScopeResultDto> ScopeAsync(
        string sessionToken, string root, string mode, string? seed, string? query, string? since, int maxTokens)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        var resolved = Path.GetFullPath(root);
        if (!Directory.Exists(resolved))
            return new ScopeResultDto((mode ?? "search").Trim().ToLowerInvariant(), [], 0, null);

        await using var store = await OpenIndexedForHostAsync(resolved, LifetimeToken);

        var (normalizedMode, plan) = await PlanScopeAsync(store, resolved, mode, seed, query, since, maxTokens);

        string? payloadPath = null;
        if (plan.Items.Count > 0)
        {
            var renderer = new SemanticContextRenderer(_reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
            var rendered = await renderer.RenderAsync(plan, resolved, LifetimeToken);
            var content = SemanticContextEmitter.Emit(plan, rendered, ContextOutputFormat.Xml, resolved);

            var dir = PayloadDirectory;
            Directory.CreateDirectory(dir);
            payloadPath = Path.Combine(dir, $"{HostEndpoint.PipeName(resolved)}-{normalizedMode}-{Guid.NewGuid():N}.fuse.xml");
            await File.WriteAllTextAsync(payloadPath, content);
            RestrictPayloadPermissions(payloadPath);
            lock (_payloadLock)
                _payloadPaths.Add(payloadPath);
        }

        var files = plan.Items
            .Select(i => new ScopeFileDto(i.Path, i.EstimatedTokens))
            .OrderByDescending(f => f.TokenCost)
            .ToList();

        _logger.LogInformation("Scope {Mode} on {Root}: {Files} files, {Tokens} tokens.",
            normalizedMode, resolved, files.Count, plan.EstimatedTokens);
        return new ScopeResultDto(normalizedMode, files, plan.EstimatedTokens, payloadPath);
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
    public async Task<ExplainResultDto> ExplainAsync(
        string sessionToken, string root, string mode, string? seed, string? query, string? since)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        var resolved = Path.GetFullPath(root);
        if (!Directory.Exists(resolved))
            return new ExplainResultDto((mode ?? "search").Trim().ToLowerInvariant(), []);

        await using var store = await OpenIndexedForHostAsync(resolved, LifetimeToken);

        var (normalizedMode, plan) = await PlanScopeAsync(store, resolved, mode, seed, query, since, 0);
        var files = plan.Items
            .Select(i => new ExplainFileDto(i.Path, i.Role, i.Tier.ToString(), i.Score))
            .ToList();

        _logger.LogInformation("Explain {Mode} on {Root}: {Files} planned files.", normalizedMode, resolved, files.Count);
        return new ExplainResultDto(normalizedMode, files);
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
    public async Task<DiagnosticsDto> DiagnosticsAsync(string sessionToken, string root)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        var resolved = Path.GetFullPath(root);
        if (!Directory.Exists(resolved))
            return new DiagnosticsDto([], [], [], []);

        await using var store = await OpenIndexedForHostAsync(resolved, LifetimeToken);

        var files = await store.FindFilesByPathAsync(string.Empty, ListLimit, LifetimeToken);

        // Read each indexed file's content once to locate the secret spans the reduction path would redact (mapped
        // to zero-based editor ranges) and to flag machine-generated C#. Read failures skip the file.
        var secrets = new List<SecretDiagnosticDto>();
        var generated = new List<string>();
        foreach (var file in files)
        {
            string content;
            try
            {
                content = await File.ReadAllTextAsync(Path.Combine(resolved, file.NormalizedPath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (string.Equals(file.Extension, ".cs", StringComparison.OrdinalIgnoreCase) && _generatedCodeDetector.IsGenerated(content))
                generated.Add(file.NormalizedPath);

            var spans = _redactor.FindSecretSpans(content);
            if (spans.Count == 0)
                continue;

            var lineStarts = ComputeLineStarts(content);
            foreach (var span in spans)
            {
                var (startLine, startCol) = OffsetToLineColumn(lineStarts, span.Start);
                var (endLine, endCol) = OffsetToLineColumn(lineStarts, span.Start + span.Length);
                secrets.Add(new SecretDiagnosticDto(file.NormalizedPath, span.Kind, startLine, startCol, endLine, endCol));
            }
        }

        var tokenByPath = await store.GetFileTokenEstimatesAsync(LifetimeToken);
        var hotspots = tokenByPath
            .Select(kv => new HotspotDiagnosticDto(kv.Key, kv.Value))
            .OrderByDescending(h => h.TokenCost)
            .Take(20)
            .ToList();

        // Graph gaps: indexed files that no typed dependency edge touches (often reflection-only or dead code the
        // syntax tier cannot connect).
        var connected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in await store.GetFileDependencyEdgesAsync(LifetimeToken))
        {
            connected.Add(edge.FromPath);
            connected.Add(edge.ToPath);
        }
        var graphGaps = files
            .Select(f => f.NormalizedPath)
            .Where(p => !connected.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        _logger.LogInformation("Diagnostics on {Root}: {Secrets} secrets, {Hotspots} hotspots, {Gaps} gaps, {Generated} generated.",
            resolved, secrets.Count, hotspots.Count, graphGaps.Count, generated.Count);
        return new DiagnosticsDto(secrets, hotspots, graphGaps, generated);
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
        DeleteTrackedPayloads();
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
    public async Task<CheckDeltaDto> CheckDeltaAsync(string sessionToken, string root, string session)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        var resolved = Path.GetFullPath(root);

        // The current whole-state diagnostics come from a live resident workspace (the same process-wide provider
        // the MCP fuse_check delta mode reads); delta mode must not run a build, so with no resident workspace this
        // returns an empty, non-resident delta and the hook stays silent.
        var current = ResidentWorkspaces.TryGetCurrentDiagnostics(resolved, LifetimeToken);
        if (current is null)
            return new CheckDeltaDto(false, [], []);

        await using var store = await OpenStoreAsync(resolved, LifetimeToken);
        var baseline = await store.GetCheckSessionBaselineAsync(session, LifetimeToken);
        if (baseline is null)
        {
            await store.SaveCheckSessionBaselineAsync(session, resolved, current, LifetimeToken);
            return new CheckDeltaDto(true, [], []);
        }

        var delta = DiagnosticDelta.Compute(baseline.Diagnostics, current);
        return new CheckDeltaDto(
            true,
            delta.Introduced.Select(ToCheckDiagnosticDto).ToList(),
            delta.Resolved.Select(ToCheckDiagnosticDto).ToList());
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
    public async Task<CheckOverlayResultDto> CheckOverlayAsync(
        string sessionToken, string root, string relativeFilePath, string newContent, bool includeAnalyzers)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        var resolved = Path.GetFullPath(root);
        var diagnostics = await ResidentWorkspaces.TryCheckOverlayAsync(
            resolved, relativeFilePath, newContent, includeAnalyzers, LifetimeToken);
        return diagnostics is null
            ? new CheckOverlayResultDto(false, [])
            : new CheckOverlayResultDto(true, diagnostics.Select(ToCheckDiagnosticDto).ToList());
    }

    /// <summary>Runs a live doctor load through this root's held warm solution cache.</summary>
    [JsonRpcMethod("fuse/doctor")]
    public async Task<DoctorResultDto> DoctorAsync(string sessionToken, string root)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        BudgetFor(root).ActivateWarm();
        return new DoctorResultDto(await Fuse.Cli.Commands.DoctorCommand.BuildLiveReportAsync(_indexer, Path.GetFullPath(root), LifetimeToken));
    }

    /// <summary>Runs a staged compiler refactor through this root's held warm solution cache.</summary>
    [JsonRpcMethod("fuse/refactor")]
    public async Task<RefactorResultDto> RefactorAsync(string sessionToken, string root, RefactorRequestDto request)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        BudgetFor(root).ActivateWarm();
        var output = await FuseTools.FuseRefactorCoreAsync(
            root, request.Symbol, request.NewName, request.Operation, request.ContainingType, request.ParameterType,
            request.ParameterName, request.Argument, request.NewOrder, request.DiagnosticId, request.File,
            LifetimeToken, routeToHost: false, runtime: _runtime);
        return new RefactorResultDto(output);
    }

    /// <summary>Runs a capture-bundle oracle check through this host's pooled worker ownership domain.</summary>
    [JsonRpcMethod("fuse/checkCapture")]
    public async Task<CaptureCheckResultDto> CheckCaptureAsync(
        string sessionToken, string root, string relativeFilePath, string newContent)
    {
        FuseHostSessionToken.Validate(_sessionToken, sessionToken);
        ValidateServedRoot(root);
        BudgetFor(root).ActivateCapture();
        var result = await FuseTools.TryOracleFromCaptureBundleAsync(
            _runtime,
            Path.GetFullPath(root), relativeFilePath, newContent,
            new BuildCaptureClient(processRunner: _runtime.ProcessRunner), LifetimeToken);
        return result is { Verified: true }
            ? new CaptureCheckResultDto(true, null, result.Diagnostics.Select(ToCheckDiagnosticDto).ToList())
            : new CaptureCheckResultDto(false, result?.Reason, []);
    }

    /// <summary>Reserves the daemon's per-root budget for an opt-in resident workspace.</summary>
    public void ActivateResidentBudget(string root)
    {
        ValidateServedRoot(root);
        BudgetFor(root).ActivateResident();
    }

    private static CheckDiagnosticDto ToCheckDiagnosticDto(CheckDiagnostic diagnostic) =>
        new(diagnostic.Id, diagnostic.Severity, diagnostic.Message, diagnostic.FilePath, diagnostic.Line);

    private CompilerStateBudget BudgetFor(string root) =>
        _compilerStateBudget ??= new CompilerStateBudget(
            Path.GetFullPath(root),
            warmSolutions: _runtime.WarmSolutions,
            pooledWorkers: _runtime.PooledCheckWorkers,
            residentWorkspaces: _residentWorkspaces);

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
            DeleteTrackedPayloads();
            await _indexJobs.DisposeAsync();
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private Task<WorkspaceIndexStore> OpenIndexedForHostAsync(string root, CancellationToken cancellationToken) =>
        _indexAccess.OpenIndexedAsync(_indexer, root, cancellationToken);

    private static IndexJobSnapshot MissingWorkspaceSnapshot(string root)
    {
        var now = DateTimeOffset.UtcNow;
        return new IndexJobSnapshot(
            JobId: string.Empty,
            Root: root,
            State: IndexJobState.Failed,
            Phase: IndexPhase.Inventory,
            PhaseNumber: 1,
            PhaseCount: 4,
            CompletedUnits: 0,
            TotalUnits: null,
            PhasePercent: null,
            EstimatedRemaining: null,
            CurrentItem: null,
            StartedAt: now,
            Elapsed: TimeSpan.Zero,
            Counts: IndexCountSnapshot.Empty,
            Storage: IndexStorageSnapshot.Empty,
            Warnings: [],
            ErrorCode: "workspace_not_found",
            ErrorMessage: $"Workspace directory does not exist: {root}");
    }

    private async Task<IndexResultDto> BuildIndexResultDtoAsync(
        string resolved, SemanticIndexResult pass, long elapsedMs)
    {
        await using var store = await _indexCoordinator.OpenForReadOnlyAsync(resolved, LifetimeToken);
        var state = await store.GetStateAsync(LifetimeToken);
        var languages = (await store.GetLanguageCountsAsync(LifetimeToken))
            .Select(l => new LanguageCountDto(l.Language, l.Count))
            .ToList();
        var fuseVersion = await store.GetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, LifetimeToken)
                          ?? FuseBuildInfo.Current;
        var manifest = await WorkspaceIndexManifest.ValidateAsync(resolved, store, LifetimeToken);

        _logger.LogInformation("Index {Root}: [{Mode}] {Files} files, {Symbols} symbols, {Routes} routes.",
            resolved, pass.Mode, pass.FileCount, pass.SymbolCount, pass.RouteCount);
        return new IndexResultDto(
            manifest.Ready ? "Warm" : "NotIndexed",
            pass.FileCount,
            elapsedMs,
            pass.Mode,
            pass.SymbolCount,
            pass.RouteCount,
            state.SchemaVersion,
            store.FullTextSearchAvailable,
            fuseVersion,
            languages);
    }

    // Opens the store for check-session baseline persistence (daemon-owned small writes).
    private static async Task<WorkspaceIndexStore> OpenStoreAsync(string root, CancellationToken cancellationToken)
    {
        var store = new WorkspaceIndexStore(FuseStorePaths.ResolveDatabasePath(root));
        await store.InitializeAsync(cancellationToken);
        return store;
    }

    // Plans a scoped context payload for a mode: focus (a symbol or file seed), changes (a git review), or search
    // (localize the query, then build context from the located files). Shared by scope and explain.
    private async Task<(string Mode, ContextPlan Plan)> PlanScopeAsync(
        WorkspaceIndexStore store, string root, string mode, string? seed, string? query, string? since, int maxTokens)
    {
        var engine = new SemanticRetrievalEngine(store, _changeSource);
        var normalized = (mode ?? "search").Trim().ToLowerInvariant();
        int? budget = maxTokens > 0 ? maxTokens : null;

        switch (normalized)
        {
            case "changes":
                return ("changes", await engine.ReviewAsync(
                    new ReviewRequest(root, string.IsNullOrWhiteSpace(since) ? "HEAD" : since!, MaxTokens: budget),
                    LifetimeToken));

            case "focus":
                var seeds = new List<ContextSeed>();
                if (!string.IsNullOrWhiteSpace(seed))
                    seeds.Add(new ContextSeed(LooksLikePath(seed!) ? ContextSeedKind.File : ContextSeedKind.Symbol, seed!));
                return ("focus", await engine.PlanContextAsync(
                    new ContextRequest(root, seeds, MaxTokens: budget), LifetimeToken));

            default:
                var located = await engine.LocalizeAsync(new LocalizationRequest(root, Query: query), LifetimeToken);
                var fileSeeds = located.Candidates
                    .Where(c => !string.IsNullOrEmpty(c.Path))
                    .Select(c => new ContextSeed(ContextSeedKind.File, c.Path))
                    .ToList();
                return ("search", await engine.PlanContextAsync(
                    new ContextRequest(root, fileSeeds, MaxTokens: budget), LifetimeToken));
        }
    }

    // A focus seed is treated as a file when it looks like a path (a separator or a known source extension),
    // otherwise as a symbol name.
    private static bool LooksLikePath(string seed) =>
        seed.Contains('/', StringComparison.Ordinal)
        || seed.Contains('\\', StringComparison.Ordinal)
        || Path.HasExtension(seed);

    // The character offset at which each line starts, so an offset maps to a line by binary search.
    private static int[] ComputeLineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
            if (content[i] == '\n')
                starts.Add(i + 1);
        return [.. starts];
    }

    // Maps a character offset to a zero-based (line, column) using the precomputed line-start table.
    private static (int Line, int Column) OffsetToLineColumn(int[] lineStarts, int offset)
    {
        var line = Array.BinarySearch(lineStarts, offset);
        if (line < 0)
            line = ~line - 1;
        line = Math.Clamp(line, 0, lineStarts.Length - 1);
        return (line, offset - lineStarts[line]);
    }

    private static string PayloadDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fuse",
            "host-payloads");

    // On Unix, scope payloads may contain source excerpts; restrict to owner read/write only.
    private static void RestrictPayloadPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private void DeleteTrackedPayloads()
    {
        string[] paths;
        lock (_payloadLock)
        {
            paths = [.. _payloadPaths];
            _payloadPaths.Clear();
        }

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete payload file {Path}.", path);
            }
        }
    }
}
