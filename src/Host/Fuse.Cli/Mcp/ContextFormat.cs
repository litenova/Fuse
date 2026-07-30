using Fuse.Context;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Parses the <c>format</c> argument the context and review tools accept.
/// </summary>
internal static class ContextFormat
{
    /// <summary>Parses a format name, defaulting to XML for an unknown or empty value.</summary>
    /// <param name="format">The requested format: xml, markdown (md), or json.</param>
    /// <returns>The output format to emit.</returns>
    internal static ContextOutputFormat Parse(string format) => format.Trim().ToLowerInvariant() switch
    {
        "markdown" or "md" => ContextOutputFormat.Markdown,
        "json" => ContextOutputFormat.Json,
        _ => ContextOutputFormat.Xml,
    };
}
