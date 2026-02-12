// -----------------------------------------------------------------------
// <copyright file="FusionResult.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Fuse.Core;

/// <summary>
///     Represents information about a file's token consumption.
/// </summary>
/// <param name="Path">The relative path of the file.</param>
/// <param name="Count">The number of tokens consumed by the file.</param>
public record FileTokenInfo(string Path, long Count);

/// <summary>
///     Represents the result of a fusion operation.
/// </summary>
/// <param name="GeneratedPaths">The list of file paths that were generated.</param>
/// <param name="TotalTokens">The total number of tokens across all generated files.</param>
/// <param name="ProcessedFileCount">The number of files that were successfully processed and included.</param>
/// <param name="TotalFileCount">The total number of files that were considered for processing.</param>
/// <param name="Duration">The duration of the fusion operation.</param>
/// <param name="TopTokenFiles">The top files consuming the most tokens.</param>
public sealed record FusionResult(
    List<string> GeneratedPaths,
    long TotalTokens,
    int ProcessedFileCount,
    int TotalFileCount,
    TimeSpan Duration,
    IReadOnlyList<FileTokenInfo> TopTokenFiles);
