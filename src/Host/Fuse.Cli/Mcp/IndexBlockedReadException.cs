namespace Fuse.Cli.Mcp;

/// <summary>
///     Thrown when a read tool cannot open the index yet. Mapped to the structured availability header at the
///     MCP boundary (R20) instead of a bare <see cref="FuseOperationalErrors.IndexBusyPrefix" /> line.
/// </summary>
internal sealed class IndexBlockedReadException : Exception
{
    /// <summary>Initializes a new instance with the full availability header to return as the tool body.</summary>
    /// <param name="availabilityHeader">The multi-line availability header (index_state, files_indexed, availability).</param>
    public IndexBlockedReadException(string availabilityHeader)
        : base(availabilityHeader)
    {
        AvailabilityHeader = availabilityHeader;
    }

    /// <summary>The structured header to return as the MCP tool result.</summary>
    public string AvailabilityHeader { get; }
}
