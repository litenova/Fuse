// -----------------------------------------------------------------------
// <copyright file="FusionResult.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Fuse.Core;

/// <summary>
///     Represents the result of a fusion operation.
/// </summary>
/// <param name="GeneratedPaths">The list of file paths that were generated.</param>
/// <param name="TotalTokens">The total number of tokens across all generated files.</param>
/// <param name="ProcessedFileCount">The number of files that were successfully processed and included.</param>
/// <param name="TotalFileCount">The total number of files that were considered for processing.</param>
/// <param name="Duration">The duration of the fusion operation.</param>
public sealed record FusionResult(
    List<string> GeneratedPaths,
    long TotalTokens,
    int ProcessedFileCount,
    int TotalFileCount,
    TimeSpan Duration);
