using System.Text.Json;
using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Serialization;
using Fuse.Cli.Services;
using Fuse.Collection.FileSystem;

namespace Fuse.Cli.Commands;

/// <summary>
///     Starts or joins a repository-owned index job and renders its progress until it reaches a terminal state.
/// </summary>
[CliCommand(
    Name = "index",
    Description = "Build or refresh the persistent syntax index for a workspace.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class IndexCommand
{
    private readonly IndexJobClient _jobs;
    private readonly IConsoleUI _consoleUI;

    /// <summary>Initializes an instance for CLI option binding only.</summary>
    /// <remarks>DotMake creates this instance only to inspect options; it must not execute.</remarks>
    public IndexCommand() : this(null!, null!)
    {
    }

    /// <summary>Initializes an index command.</summary>
    /// <param name="jobs">The daemon or in-process index lifecycle client.</param>
    /// <param name="consoleUI">The console output service.</param>
    public IndexCommand(IndexJobClient jobs, IConsoleUI consoleUI)
    {
        _jobs = jobs;
        _consoleUI = consoleUI;
    }

    /// <summary>The workspace directory to index. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory to index. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Whether the job should run compiler analysis after syntax extraction.</summary>
    [CliOption(Description = "Run compiler analysis after syntax indexing. Requires an unambiguous workspace target.")]
    public bool Semantic { get; set; }

    /// <summary>Whether to discard the current derived index before starting.</summary>
    [CliOption(Description = "Discard the current derived index before starting. Conflicts with an active job.")]
    public bool Force { get; set; }

    /// <summary>Rehydrates the index from a portable capture bundle instead of compiling.</summary>
    [CliOption(Name = "--from-capture", Required = false, Description = "Rehydrate from a portable capture bundle directory.")]
    public string? FromCapture { get; set; }

    /// <summary>Whether to emit JSON Lines job snapshots instead of human-readable progress.</summary>
    [CliOption(Name = "--json", Required = false, Description = "Emit JSON Lines progress snapshots.")]
    public bool Json { get; set; }

    /// <summary>Runs the index command.</summary>
    /// <param name="context">The CLI invocation context.</param>
    /// <returns>A task that completes when the shared job reaches a terminal state.</returns>
    public async Task RunAsync(CliContext context)
    {
        if (!TryResolveRepositoryRoot(Path, out var root))
        {
            RenderError(
                "workspace_identity_unresolved",
                "Fuse indexing requires a Git repository. Run 'git init' or pass a path inside an existing repository.",
                Json,
                exitCode: 2);
            return;
        }
        if (!Directory.Exists(root))
        {
            RenderError("workspace_not_found", $"Directory does not exist: {root}", Json, exitCode: 2);
            return;
        }

        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var interruptCount = 0;
        ConsoleCancelEventHandler onCancel = (_, args) =>
        {
            if (Interlocked.Increment(ref interruptCount) == 1)
            {
                args.Cancel = true;
                commandCancellation.Cancel();
            }
            else
            {
                // The daemon is a separate process. Let the terminal detach this CLI process without sending a
                // second signal to the shared repository job.
                args.Cancel = false;
            }
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var request = new IndexJobRequest(
                root,
                Semantic || FromCapture is not null ? IndexDepth.Semantic : IndexDepth.Syntax,
                Force,
                FromCapture);
            var started = await _jobs.StartAsync(request, commandCancellation.Token);
            if (started.Result.Conflict)
            {
                RenderSnapshot(started.Result.Snapshot, Json);
                Environment.ExitCode = 4;
                return;
            }

            await WaitAndRenderAsync(root, started.UsesDaemon, started.Result.Snapshot, Json, commandCancellation.Token);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref interruptCount) > 0)
        {
            await CancelAfterInterruptAsync(root, Json);
            Environment.ExitCode = 130;
        }
        catch (IndexJobValidationException ex)
        {
            RenderError("index_invalid_request", ex.Message, Json, exitCode: 2);
        }
        catch (IndexJobClientException ex)
        {
            RenderError(ex.Code, ex.Message, Json, exitCode: 3);
        }
        catch (Exception ex)
        {
            RenderError("index_failed", ex.Message, Json, exitCode: 3);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private async Task WaitAndRenderAsync(
        string root,
        bool usesDaemon,
        IndexJobSnapshot initial,
        bool json,
        CancellationToken cancellationToken)
    {
        var renderer = new IndexProgressRenderer(_consoleUI, json);
        var snapshot = initial;
        renderer.Render(snapshot, force: true);
        while (snapshot.State is IndexJobState.Queued or IndexJobState.Running or IndexJobState.Cancelling)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            snapshot = await _jobs.StatusAsync(root, usesDaemon, cancellationToken)
                ?? throw new IndexJobClientException("daemon_unavailable", "The index daemon stopped before reporting completion.");
            renderer.Render(snapshot, force: false);
        }

        renderer.Render(snapshot, force: false);
        switch (snapshot.State)
        {
            case IndexJobState.Completed:
                if (!json)
                    _consoleUI.WriteSuccess(
                        $"Indexed {snapshot.Counts.Files} files, {snapshot.Counts.Projects} projects, "
                        + $"{snapshot.Counts.Symbols} symbols, and {snapshot.Counts.Routes} routes.");
                return;
            case IndexJobState.Cancelled:
                if (!json)
                    _consoleUI.WriteResult("Index job cancelled. Committed batches remain available for the next run.");
                return;
            case IndexJobState.Failed:
                var exitCode = string.Equals(snapshot.ErrorCode, "index_invalid_request", StringComparison.Ordinal)
                    ? 2
                    : 3;
                RenderError(snapshot.ErrorCode ?? "index_failed", snapshot.ErrorMessage ?? "Index job failed.", json, exitCode);
                return;
            default:
                RenderError("index_failed", "Index job reached an invalid terminal state.", json, exitCode: 3);
                return;
        }
    }

    private async Task CancelAfterInterruptAsync(string root, bool json)
    {
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var cancelled = await _jobs.CancelAsync(root, wait.Token);
            if (cancelled.Snapshot is null)
                return;

            var snapshot = cancelled.Snapshot;
            while (snapshot.State is IndexJobState.Queued or IndexJobState.Running or IndexJobState.Cancelling)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), wait.Token);
                var next = await _jobs.StatusAsync(root, cancelled.UsesDaemon, wait.Token);
                if (next is null)
                    break;
                snapshot = next;
            }

            RenderSnapshot(snapshot, json);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RenderError("index_cancel_failed", ex.Message, json, exitCode: 130);
        }
    }

    internal static string ResolveRoot(string path)
    {
        var requested = System.IO.Path.GetFullPath(path);
        return WorkspaceIdentityResolver.TryResolveRepositoryRoot(requested, out var repositoryRoot)
            ? repositoryRoot
            : requested;
    }

    /// <summary>Resolves the canonical Git repository root required by index lifecycle commands.</summary>
    /// <param name="path">A path at or below the requested repository.</param>
    /// <param name="root">The resolved repository root on success.</param>
    /// <returns><see langword="true" /> when a Git repository identity is available.</returns>
    internal static bool TryResolveRepositoryRoot(string path, out string root) =>
        WorkspaceIdentityResolver.TryResolveRepositoryRoot(System.IO.Path.GetFullPath(path), out root);

    internal static void RenderSnapshot(IndexJobSnapshot snapshot, bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(snapshot, IndexCliJsonContext.Default.IndexJobSnapshot));
            return;
        }

        var renderer = new IndexProgressRenderer(new ConsoleUI(), json: false);
        renderer.Render(snapshot, force: true);
    }

    internal static void RenderError(string code, string message, bool json, int exitCode)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new IndexCliError(code, message),
                IndexCliJsonContext.Default.IndexCliError));
        }
        else
        {
            Console.Error.WriteLine($"[ERROR] {code}: {message}");
        }

        Environment.ExitCode = exitCode;
    }
}

/// <summary>Renders human and JSON index job progress without relying on terminal-specific control sequences.</summary>
internal sealed class IndexProgressRenderer
{
    private static readonly char[] SpinnerFrames = ['|', '/', '-', '\\'];
    private readonly IConsoleUI _consoleUI;
    private readonly bool _json;
    private readonly TextWriter _output;
    private IndexPhase? _lastPhase;
    private IndexJobState? _lastState;
    private int? _lastBucket;
    private DateTimeOffset _lastRender;
    private int _spinnerFrame;

    /// <summary>Initializes a progress renderer.</summary>
    /// <param name="consoleUI">The output service used for human-readable progress.</param>
    /// <param name="json">Whether to write JSON Lines snapshots.</param>
    /// <param name="output">The JSON output writer; defaults to the process standard output.</param>
    public IndexProgressRenderer(IConsoleUI consoleUI, bool json, TextWriter? output = null)
    {
        _consoleUI = consoleUI;
        _json = json;
        _output = output ?? Console.Out;
    }

    /// <summary>Renders a snapshot when its visible progress changed.</summary>
    /// <param name="snapshot">The current job snapshot.</param>
    /// <param name="force">Whether to bypass throttling.</param>
    public void Render(IndexJobSnapshot snapshot, bool force)
    {
        int? bucket = snapshot.PhasePercent is null ? null : (int)(snapshot.PhasePercent.Value / 10);
        var now = DateTimeOffset.UtcNow;
        var periodicOutput = _json || Console.IsOutputRedirected;
        var unchanged = _lastPhase == snapshot.Phase
                        && _lastState == snapshot.State
                        && _lastBucket == bucket;
        if (!force && periodicOutput && unchanged
            && now - _lastRender < TimeSpan.FromSeconds(5))
            return;
        if (!force && !periodicOutput && unchanged)
            return;

        _lastPhase = snapshot.Phase;
        _lastState = snapshot.State;
        _lastBucket = bucket;
        _lastRender = now;
        if (_json)
        {
            _output.WriteLine(JsonSerializer.Serialize(snapshot, IndexCliJsonContext.Default.IndexJobSnapshot));
            return;
        }

        _consoleUI.WriteStep(Format(snapshot, SpinnerFrames[_spinnerFrame++ % SpinnerFrames.Length]));
    }

    private static string Format(IndexJobSnapshot snapshot, char spinnerFrame)
    {
        var phase = snapshot.Phase.ToString();
        if (snapshot.PhasePercent is { } percent && snapshot.TotalUnits is { } total)
        {
            var rounded = (int)Math.Round(percent, MidpointRounding.AwayFromZero);
            var filled = Math.Clamp((int)Math.Round(rounded / 10d, MidpointRounding.AwayFromZero), 0, 10);
            var bar = new string('#', filled) + new string('-', 10 - filled);
            return $"Phase {snapshot.PhaseNumber}/{snapshot.PhaseCount} [{bar}] {rounded}% {snapshot.CompletedUnits}/{total} files ({phase})";
        }

        var current = string.IsNullOrWhiteSpace(snapshot.CurrentItem) ? "working" : snapshot.CurrentItem;
        return $"Phase {snapshot.PhaseNumber}/{snapshot.PhaseCount} [{spinnerFrame}] {current} ({phase})";
    }
}

/// <summary>A source-generated JSON error payload for index CLI output.</summary>
/// <param name="Code">The stable error code.</param>
/// <param name="Message">The direct recovery message.</param>
public sealed record IndexCliError(string Code, string Message);
