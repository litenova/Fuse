using System.IO.Hashing;
using System.Security.Cryptography;

namespace Fuse.Indexing;

/// <summary>
///     Computes content hashes for change detection in the workspace index.
/// </summary>
/// <remarks>
///     Uses XxHash64: fast and collision-resistant enough to decide whether a file changed and must be
///     re-indexed. The hash is not used for security, only for cache invalidation.
/// </remarks>
public sealed class FileHashService
{
    /// <summary>
    ///     Computes the content hash of a byte span.
    /// </summary>
    /// <param name="content">The bytes to hash.</param>
    /// <returns>A 16-character lowercase hexadecimal hash.</returns>
    public string ComputeHash(ReadOnlySpan<byte> content) =>
        XxHash64.HashToUInt64(content).ToString("x16");

    /// <summary>
    ///     Reads a file and computes its content hash.
    /// </summary>
    /// <param name="path">The absolute path to the file.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>A 16-character lowercase hexadecimal hash of the file content.</returns>
    public async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return ComputeHash(bytes);
    }

    /// <summary>
    ///     Streams a SHA-256 hash for a working-tree file.
    /// </summary>
    /// <param name="path">The absolute file path.</param>
    /// <param name="cancellationToken">A token to cancel the stream read.</param>
    /// <returns>A lowercase SHA-256 identity prefixed with <c>sha256:</c>.</returns>
    /// <remarks>
    ///     The workspace inventory uses Git blob ids for clean tracked files. Dirty and untracked files have no
    ///     usable blob id, so this method hashes their on-disk bytes without retaining the complete source in memory.
    /// </remarks>
    public async Task<string> ComputeSha256FileAsync(string path, CancellationToken cancellationToken)
    {
        const int bufferSize = 64 * 1024;
        var buffer = new byte[bufferSize];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
        }

        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
