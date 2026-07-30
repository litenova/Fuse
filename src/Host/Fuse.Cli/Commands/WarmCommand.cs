using DotMake.CommandLine;
using Fuse.Cli.Services;

namespace Fuse.Cli.Commands;

/// <summary>
///     Warms the persistent index for a workspace now (R38): builds the syntax-first index so a subsequent read
///     hits a warm store rather than paying the cold cost. It starts or joins the same daemon-owned syntax job as
///     <c>fuse index</c> and runs regardless of the <c>FUSE_EAGER_INDEX</c> opt-out.
/// </summary>
[CliCommand(
    Name = "warm",
    Description = "Warm the persistent index for a workspace now, so the next read is fast (syntax-first build; a warm store is a no-op).",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class WarmCommand
{
    private readonly IndexJobClient _jobs;
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="WarmCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependencies are null, so this instance must not run.</remarks>
    public WarmCommand() : this(null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="WarmCommand" /> class.
    /// </summary>
    /// <param name="jobs">The daemon or in-process index lifecycle client.</param>
    /// <param name="consoleUI">The console UI for output.</param>
    public WarmCommand(IndexJobClient jobs, IConsoleUI consoleUI)
    {
        _jobs = jobs;
        _consoleUI = consoleUI;
    }

    /// <summary>The workspace directory to warm. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory to warm. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>
    ///     The always-on warm service action (R40): <c>install</c>, <c>uninstall</c>, or <c>status</c>. Opt-in and
    ///     never installed by <c>fuse mcp install</c>. Empty for a one-shot warm of <see cref="Path" />.
    /// </summary>
    [CliOption(Name = "--service", Description = "Manage the opt-in always-on warm service: install, uninstall, or status.")]
    public string Service { get; set; } = "";

    /// <summary>
    ///     Runs the warm command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the index has been warmed.</returns>
    public async Task RunAsync(CliContext context)
    {
        if (!string.IsNullOrWhiteSpace(Service))
        {
            await RunServiceActionAsync(Service.Trim().ToLowerInvariant(), context.CancellationToken);
            return;
        }

        var root = System.IO.Path.GetFullPath(Path);
        if (!Directory.Exists(root))
        {
            _consoleUI.WriteError($"Directory not found: {root}");
            return;
        }

        _consoleUI.WriteStep($"Warming the index for {root}");
        await WarmAsync(root, context.CancellationToken);
        _consoleUI.WriteResult($"warmed: {root} (syntax index is current).");
    }

    // R40: the opt-in always-on warm service surface. install/uninstall actually attempt the platform
    // registration and fall back to the manual command on failure (option 1); run is the service loop that
    // re-warms recently-used repos, battery-aware. The service is store-backed and LRU-capped; it is never
    // installed by `fuse mcp install`.
    private async Task RunServiceActionAsync(string action, CancellationToken cancellationToken)
    {
        var invocation = Environment.ProcessPath ?? "fuse";
        switch (action)
        {
            case "install":
                var installResult = WarmServiceInstaller.Install(invocation);
                _consoleUI.WriteResult(installResult.Message);
                if (!installResult.Succeeded)
                    Environment.ExitCode = 1;
                break;
            case "uninstall":
                var uninstallResult = WarmServiceInstaller.Uninstall();
                _consoleUI.WriteResult(uninstallResult.Message);
                if (!uninstallResult.Succeeded)
                    Environment.ExitCode = 1;
                break;
            case "run":
                await RunServiceLoopAsync(cancellationToken);
                break;
            case "status":
            default:
                var repos = WarmServiceState.Recent();
                _consoleUI.WriteResult(
                    $"warm service: opt-in ({WarmServiceDefinition.PlatformMechanism()}), store-backed, LRU-capped at {WarmServiceLru.DefaultCap} repos, battery-aware. " +
                    $"Recently-used repos tracked: {repos.Count}. Install with 'fuse warm --service install', remove with 'fuse warm --service uninstall'. Never installed by 'fuse mcp install'.");
                break;
        }
    }

    // The warm-service loop (R40): re-warm the recently-used repos on an interval, pausing on battery or high load.
    private async Task RunServiceLoopAsync(CancellationToken cancellationToken)
    {
        _consoleUI.WriteStep("Fuse warm service running (store-backed, battery-aware; Ctrl+C to stop).");
        var interval = TimeSpan.FromMinutes(10);
        while (!cancellationToken.IsCancellationRequested)
        {
            var repos = WarmServiceState.Recent();
            var paused = WarmServicePolicy.ShouldPause(PowerState.OnBattery(), highLoad: false);
            await WarmServiceRunner.RunOnceAsync(
                repos, paused, WarmAsync, cancellationToken);
            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task WarmAsync(string root, CancellationToken cancellationToken)
    {
        var started = await _jobs.StartAsync(
            new Fuse.Cli.Mcp.IndexJobRequest(
                root,
                Fuse.Cli.Mcp.IndexDepth.Syntax,
                Force: false,
                CaptureBundlePath: null),
            cancellationToken);
        if (started.Result.Conflict)
            throw new IndexJobClientException(
                "index_job_conflict",
                started.Result.Snapshot.ErrorMessage ?? "index job conflict");

        var snapshot = started.Result.Snapshot;
        while (snapshot.State is Fuse.Cli.Mcp.IndexJobState.Queued
               or Fuse.Cli.Mcp.IndexJobState.Running
               or Fuse.Cli.Mcp.IndexJobState.Cancelling)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            snapshot = await _jobs.StatusAsync(root, started.UsesDaemon, cancellationToken)
                ?? throw new IndexJobClientException("daemon_unavailable", "The index job stopped reporting status.");
        }

        if (snapshot.State == Fuse.Cli.Mcp.IndexJobState.Failed)
            throw new IndexJobClientException(
                snapshot.ErrorCode ?? "index_failed",
                snapshot.ErrorMessage ?? "index job failed");
        if (snapshot.State == Fuse.Cli.Mcp.IndexJobState.Cancelled)
            throw new OperationCanceledException("index job was cancelled", cancellationToken);
    }
}
