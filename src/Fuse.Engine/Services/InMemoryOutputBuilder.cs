// -----------------------------------------------------------------------
// <copyright file="InMemoryOutputBuilder.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using Fuse.Core;
using TiktokenSharp;

namespace Fuse.Engine.Services;

/// <summary>
///     An in-memory implementation of <see cref="IOutputBuilder" /> that captures
///     fused content into a string instead of writing to disk.
/// </summary>
/// <remarks>
///     <para>
///         This builder is used by the MCP server mode to produce fusion results
///         without any disk I/O, enabling low-latency responses to AI agents.
///     </para>
///     <para>
///         It respects <see cref="FuseOptions.MaxTokens" /> to enforce a hard stop,
///         but does not perform file splitting (returns a single content block).
///     </para>
/// </remarks>
public sealed class InMemoryOutputBuilder : IOutputBuilder
{
    /// <summary>
    ///     The content processor for transforming raw file content (minification, trimming).
    /// </summary>
    private readonly IContentProcessor _contentProcessor;

    /// <summary>
    ///     The tokenizer for GPT token counting (uses cl100k_base encoding).
    /// </summary>
    private readonly TikToken _tokenizer;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InMemoryOutputBuilder" /> class.
    /// </summary>
    /// <param name="contentProcessor">The content processor for file transformations.</param>
    public InMemoryOutputBuilder(IContentProcessor contentProcessor)
    {
        _contentProcessor = contentProcessor;
        _tokenizer = TikToken.GetEncoding("cl100k_base");
    }

    /// <inheritdoc />
    /// <summary>
    ///     Builds the fused output in-memory and returns a <see cref="FusionResult" />
    ///     where the single generated path entry contains the fused content itself.
    /// </summary>
    public async Task<FusionResult> BuildOutputAsync(
        List<FileProcessingInfo> files,
        FuseOptions options,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;
        var sb = new StringBuilder();
        long totalTokens = 0;
        int processedFileCount = 0;
        var fileTokenStats = new List<FileTokenInfo>();

        using var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Process files from biggest to smallest (by raw file size)
        foreach (var fileInfo in files.OrderByDescending(f => f.Info.Length))
        {
            if (localCts.IsCancellationRequested)
                break;

            var processedContent = await _contentProcessor.ProcessContentAsync(fileInfo, options, localCts.Token);

            // Skip trivial content
            if (string.IsNullOrWhiteSpace(processedContent) ||
                processedContent.Trim() is "{}" or "[]")
                continue;

            var fileTokenCount = _tokenizer.Encode(processedContent).Count;
            fileTokenStats.Add(new FileTokenInfo(fileInfo.RelativePath, fileTokenCount));

            var markerOverhead = 30;
            var totalEntryTokens = fileTokenCount + markerOverhead;

            // Normalize path to forward slashes
            var normalizedPath = fileInfo.RelativePath.Replace('\\', '/');

            sb.AppendLine($"<file path=\"{normalizedPath}\">");
            sb.Append(processedContent);
            if (!processedContent.EndsWith('\n'))
                sb.AppendLine();
            sb.AppendLine("</file>");

            totalTokens += totalEntryTokens;
            processedFileCount++;

            // Check MaxTokens hard stop
            if (options.MaxTokens.HasValue && totalTokens > options.MaxTokens.Value)
            {
                await localCts.CancelAsync();
            }
        }

        var duration = DateTime.Now - startTime;

        var topTokenFiles = fileTokenStats
            .OrderByDescending(f => f.Count)
            .Take(5)
            .ToList();

        // Store the in-memory content as the single "generated path" entry.
        // Callers that use InMemoryOutputBuilder know to read this as content, not a file path.
        var content = sb.ToString();

        return new FusionResult(
            [content],
            totalTokens,
            processedFileCount,
            files.Count,
            duration,
            topTokenFiles);
    }
}
