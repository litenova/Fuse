namespace Fuse.Cli.Mcp;

/// <summary>
///     Resolves the bounded wait a read may spend waiting for a shared syntax index job. The wait belongs to the
///     caller only; cancelling it never cancels the repository-owned job.
/// </summary>
internal static class ColdReadDeadline
{
    /// <summary>The default number of milliseconds a cold read waits for syntax data.</summary>
    internal const int DefaultDeadlineMilliseconds = 2500;

    /// <summary>The environment variable that overrides the cold-read deadline.</summary>
    internal const string DeadlineEnvVar = "FUSE_COLD_READ_DEADLINE_MS";

    /// <summary>Gets the configured cold-read deadline in milliseconds.</summary>
    /// <returns>A positive deadline.</returns>
    internal static int DeadlineMilliseconds() =>
        int.TryParse(Environment.GetEnvironmentVariable(DeadlineEnvVar), out var milliseconds) && milliseconds > 0
            ? milliseconds
            : DefaultDeadlineMilliseconds;
}

/// <summary>
///     Raised when a caller's cold-read wait expires before the shared syntax job commits a readable store.
/// </summary>
internal sealed class ColdStartInProgressException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ColdStartInProgressException" /> class.</summary>
    /// <param name="root">The workspace root whose index is building.</param>
    public ColdStartInProgressException(string root)
        : base($"cold index build in progress for {root}") => Root = root;

    /// <summary>The workspace root whose index is building.</summary>
    public string Root { get; }
}
