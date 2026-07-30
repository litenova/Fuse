using System.Text;
using Fuse.Fusion.Indexing;

namespace Fuse.Fusion.Storage;

/// <summary>
///     Encode and decode helpers for values stored in a namespaced host-memory
///     <see cref="Fuse.Reduction.Caching.IKeyValueStore" />.
/// </summary>
/// <remarks>
///     Text formats use tab separation; symbol and token names contain neither tabs nor newlines, so no escaping
///     is required. Vectors are little-endian single-precision floats. Malformed entries decode to
///     <see langword="null" /> rather than throwing.
/// </remarks>
internal static class NamespacedKvCodec
{
    internal static readonly string[] EmptyTokens = [];

    /// <summary>Encodes a <see cref="FileAnalysis" /> as three tab-separated lines.</summary>
    internal static byte[] EncodeFileAnalysis(FileAnalysis analysis) =>
        Encoding.UTF8.GetBytes(
            string.Join('\t', analysis.ReferencedTypes) + "\n"
            + string.Join('\t', analysis.DeclaredTypes) + "\n"
            + string.Join('\t', analysis.DeclaredSymbols));

    /// <summary>Decodes a persisted <see cref="FileAnalysis" />; returns <see langword="null" /> when malformed.</summary>
    internal static FileAnalysis? DecodeFileAnalysis(byte[] bytes)
    {
        try
        {
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            if (lines.Length != 3)
                return null;

            return new FileAnalysis(SplitTabLine(lines, 0), SplitTabLine(lines, 1), SplitTabLine(lines, 2));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Encodes a token list as a tab-separated UTF-8 string.</summary>
    internal static byte[] EncodeTokens(IReadOnlyList<string> tokens) =>
        Encoding.UTF8.GetBytes(string.Join('\t', tokens));

    /// <summary>Decodes a token list; returns <see langword="null" /> when malformed.</summary>
    internal static IReadOnlyList<string>? DecodeTokens(byte[] bytes)
    {
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            return text.Length == 0 ? EmptyTokens : text.Split('\t', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Encodes a vector as little-endian single-precision floats.</summary>
    internal static byte[] EncodeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    ///     Decodes a vector when the byte length matches <paramref name="dimensions" />; otherwise returns
    ///     <see langword="null" />.
    /// </summary>
    internal static float[]? DecodeVector(byte[] bytes, int dimensions)
    {
        if (bytes.Length != dimensions * sizeof(float))
            return null;

        var loaded = new float[dimensions];
        Buffer.BlockCopy(bytes, 0, loaded, 0, bytes.Length);
        return loaded;
    }

    /// <summary>Formats a content hash as a fixed-width lowercase hex key.</summary>
    internal static string HashKey(ulong contentHash) => $"{contentHash:x16}";

    private static IReadOnlyList<string> SplitTabLine(string[] lines, int index)
    {
        if (index >= lines.Length || lines[index].Length == 0)
            return EmptyTokens;

        return lines[index].Split('\t', StringSplitOptions.RemoveEmptyEntries);
    }
}
