using System.Diagnostics;
using System.Reflection;
using DotMake.CommandLine;
using Fuse.Cli.Extensions;
using Fuse.Cli.Mcp;
using Fuse.Cli.Rpc;
using Fuse.Cli.Services;
using Fuse.Collection.FileSystem;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Commands;

/// <summary>
///     Starts the Fuse MCP server for AI agent integration over stdio.
/// </summary>
[CliCommand(
    Name = "serve",
    Description = "Start the Fuse MCP server for AI agent integration. Communicates via stdio using the Model Context Protocol.",
    Parent = typeof(McpCommand))]
public sealed class McpServeCommand
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="McpServeCommand" /> class.
    /// </summary>
    public McpServeCommand()
    {
    }

    /// <summary>
    ///     Builds and runs the MCP host, serving the Fuse <see cref="FuseTools" /> and <see cref="FuseResources" />
    ///     over the stdio transport.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token that shuts the host down.</param>
    /// <returns>A task that completes when the MCP host stops, either on cancellation or transport close.</returns>
    /// <remarks>
    ///     Starts a long-running host that owns stdin and stdout for JSON-RPC traffic. All logging is routed to
    ///     stderr (and <see cref="StderrConsoleUI" /> is registered) so that diagnostic output never corrupts the
    ///     protocol stream. The method blocks until <paramref name="context" />'s cancellation token is signalled.
    /// </remarks>
    public async Task RunAsync(CliContext context)
    {
        // Tell the client (or auto-apply, when FUSE_AUTO_UPDATE is set) that a newer Fuse is available. Writes to
        // stderr so it never corrupts the JSON-RPC stream on stdout; cache-first, so it does not delay serving.
        FuseUpdatePrompt.Emit(Console.Error.WriteLine, allowAutoUpdate: true);

        var hasWorkspaceIdentity = WorkspaceIdentityResolver.TryResolveRepositoryRoot(
            Environment.CurrentDirectory,
            out var daemonRoot);
        if (!hasWorkspaceIdentity)
        {
            Console.Error.WriteLine(
                $"Fuse serve: {Path.GetFullPath(Environment.CurrentDirectory)} is not inside a Git repository. "
                + "Workspace-scoped MCP tools are disabled for this folder; fuse_reduce remains available.");
        }

        var daemonAttached = hasWorkspaceIdentity && IsDaemonEnabled()
            && await EnsureDaemonAsync(daemonRoot, context.CancellationToken);

        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddSingleton<IConsoleUI, StderrConsoleUI>();
        builder.Services.AddFuse();
        if (daemonAttached)
        {
            builder.Services.AddSingleton<IIndexAccessProvider>(serviceProvider =>
                new RemoteIndexAccessProvider(localFallback: serviceProvider.GetRequiredService<LocalIndexAccessProvider>()));
            builder.Services.AddSingleton<Fuse.Workspace.IResidentWorkspaceProvider>(_ => new RemoteResidentWorkspaceProvider());
        }

        var mcpServer = builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "fuse",
                    Version = typeof(McpServeCommand).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? typeof(McpServeCommand).Assembly.GetName().Version?.ToString(3)
                        ?? "0.0.0"
                };
                options.ServerInstructions = FuseAgentGuidance.ServerInstructions;
            });

        // F-017: opt-in System.Diagnostics.Metrics for tool duration, index mode, and reconcile-stamped events.
        if (FuseMetrics.Enabled)
        {
            // ModelContextProtocol 1.x moved per-request filters behind WithRequestFilters; the call-tool filter is
            // registered on the IMcpRequestFilterBuilder rather than directly on the server builder (0.8-preview API).
            mcpServer.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var sw = Stopwatch.StartNew();
                var toolName = context.Params?.Name ?? "unknown";
                try
                {
                    return await next(context, cancellationToken);
                }
                finally
                {
                    sw.Stop();
                    FuseMetrics.RecordToolDuration(toolName, sw.Elapsed);
                }
            }));
        }

        mcpServer
            .WithStdioServerTransport()
            .WithTools<FuseTools>()
            .WithResources<FuseResources>()
            // Register the playbook prompts (U3): selectable, anchored plans that teach the verified-edit loop.
            .WithPrompts<FusePrompts>();

        using var app = builder.Build();
        var indexJobs = app.Services.GetRequiredService<IWorkspaceIndexJobManager>();
        using var indexJobShutdown = context.CancellationToken.Register(
            () => _ = ShutdownIndexJobsAsync(indexJobs));

        // Resident workspace (S1) is opt-in for now (FUSE_RESIDENT truthy), default off so the shipped serve path
        // stays byte-identical until the latency gate promotes it. When on, warm the served root in the background
        // (never blocking startup) so fuse_check answers resident-grade from the held compilation, watch the tree
        // to keep it current, and project the changed cone into the store so store-backed reads reflect edits; a
        // bulk change above the storm threshold evicts to store-backed rather than serving stale. Wired after Build
        // so the SemanticIndexer (used for the store projection) is resolvable from the host services. Promotion to
        // default-on with the recorded latency and single-writer validation is the S1 gate.
        // Shared daemon (G5, R13 default-on): serve ensures one shared `fuse host` daemon runs for this root
        // (spawning it on demand; the daemon's single-instance lock resolves any race) and delegates
        // resident-grade checks to it over the pipe, so one warm compilation serves every client. Set
        // FUSE_DAEMON=0 to hold an in-process workspace instead. Falls back to in-process when the daemon
        // cannot start.
        // Own resident workspace (S1) when not delegating to a daemon and FUSE_RESIDENT is opt-in.
        using var residentWatcher = hasWorkspaceIdentity && !daemonAttached && ResidentWorkspaceHosting.OptIn()
            ? new DebouncedFileWatcher(daemonRoot, recursive: true, cancellationToken: context.CancellationToken)
            : null;
        using var warmSolutionWatcher = residentWatcher is null
            ? null
            : app.Services.GetRequiredService<WarmSolutionCache>().AttachWatcher(daemonRoot);
        if (residentWatcher is not null)
        {
            var warmSolutions = app.Services.GetRequiredService<WarmSolutionCache>();
            residentWatcher.BatchChanged += (batch, _) =>
            {
                if (batch.Any(change => WarmSolutionCache.IsTrackedSourceFile(daemonRoot, change.FullPath)))
                    warmSolutions.InvalidateWatcherRoot(daemonRoot);
                return Task.CompletedTask;
            };
        }
        using var resident = residentWatcher is null
            ? null
            : ResidentWorkspaceHosting.Enable(
                daemonRoot, residentWatcher,
                app.Services.GetRequiredService<ResidentWorkspaceRegistry>(),
                indexJobs,
                Console.Error.WriteLine,
                context.CancellationToken);

        // An in-process server starts the same visible syntax job as a daemon. The daemon starts it in its host.
        if (hasWorkspaceIdentity && !daemonAttached)
        {
            _ = app.Services.GetRequiredService<EagerIndex>().Start(daemonRoot, context.CancellationToken);
        }

        await app.RunAsync(context.CancellationToken);
    }

    private static async Task ShutdownIndexJobsAsync(IWorkspaceIndexJobManager jobs)
    {
        try
        {
            await jobs.ShutdownAsync(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            // The host container reached disposal before its cancellation callback ran.
        }
    }

    // The shared-daemon delegation (G5, R13 default-on; R19 index writes): serve ensures one shared `fuse host`
    // per repository serves every client unless FUSE_DAEMON=0 opts out.
    internal static bool IsDaemonEnabled()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_DAEMON");
        if (value is null)
            return true;

        return !(value.Equals("0", StringComparison.Ordinal)
                 || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                 || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> EnsureDaemonAsync(string root, CancellationToken cancellationToken)
    {
        var supervisor = new DaemonSupervisor(
            ct => FuseHostClient.IsServingAsync(root, TimeSpan.FromMilliseconds(500), ct),
            () => DaemonProcessLauncher.Spawn(root, idleMinutes: 30));
        var outcome = await supervisor.EnsureRunningAsync(TimeSpan.FromSeconds(20), cancellationToken);
        if (outcome == DaemonSupervisor.Outcome.FailedToStart)
        {
            Console.Error.WriteLine($"Fuse serve: could not start a daemon for {root}; serving in-process.");
            return false;
        }

        Console.Error.WriteLine(
            $"Fuse serve: resident workspace and index writes delegated to the shared daemon for {root} ({outcome}).");
        return true;
    }
}
