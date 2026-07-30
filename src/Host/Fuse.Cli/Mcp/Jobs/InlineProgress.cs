namespace Fuse.Cli.Mcp;

/// <summary>
///     Delivers progress on the reporting thread. Index work can run on a thread-pool thread, but phase updates
///     must retain their order so a later phase never overwrites a newer snapshot with an earlier queued callback.
/// </summary>
/// <typeparam name="T">The progress value type.</typeparam>
internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;

    /// <summary>Initializes a synchronous progress sink.</summary>
    /// <param name="report">The callback that consumes each progress update.</param>
    public InlineProgress(Action<T> report) => _report = report;

    /// <inheritdoc />
    public void Report(T value) => _report(value);
}
