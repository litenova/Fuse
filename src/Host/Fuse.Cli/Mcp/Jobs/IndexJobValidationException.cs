namespace Fuse.Cli.Mcp;

/// <summary>
///     Identifies invalid job input that callers can correct without treating the index pipeline as a failure.
/// </summary>
public sealed class IndexJobValidationException : Exception
{
    /// <summary>Initializes a new validation exception.</summary>
    /// <param name="message">The direct correction message.</param>
    public IndexJobValidationException(string message) : base(message)
    {
    }
}
