//-----------------------------------------------------------------------
// <copyright file="OutputBuilder.cs" company="Fuse">
// Copyright (c) Fuse. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using System.Text;
using Fuse.Core;
using TiktokenSharp;

namespace Fuse.Engine.Services;

/// <summary>
///     Builds the final fused output file(s) from processed content.
/// </summary>
/// <remarks>
///     <para>
///         This service is responsible for the final stage of the fusion pipeline. Its primary responsibilities include:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>Managing output file streams and encoding (UTF-8).</description>
///         </item>
///         <item>
///             <description>Formatting file content using simplified XML tags (<c>&lt;file&gt;</c>) for optimal LLM parsing.</description>
///         </item>
///         <item>
///             <description>Tracking token usage using the <see cref="TikToken"/> tokenizer.</description>
///         </item>
///         <item>
///             <description>Enforcing token limits (<see cref="FuseOptions.MaxTokens"/>).</description>
///         </item>
///         <item>
///             <description>Splitting output into multiple parts if <see cref="FuseOptions.SplitTokens"/> is exceeded.</description>
///         </item>
///     </list>
/// </remarks>
public sealed class OutputBuilder : IOutputBuilder
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
    ///     Initializes a new instance of the <see cref="OutputBuilder" /> class.
    /// </summary>
    /// <param name="contentProcessor">The content processor for file transformations.</param>
    public OutputBuilder(IContentProcessor contentProcessor)
    {
        _contentProcessor = contentProcessor;

        // Initialize tokenizer with the cl100k_base encoding (used by GPT-4 and GPT-3.5-turbo)
        _tokenizer = TikToken.GetEncoding("cl100k_base");
    }

    /// <inheritdoc />
    /// <summary>
    ///     Builds the fused output file(s) with token tracking and automatic splitting.
    /// </summary>
    /// <param name="files">The list of files to include in the output.</param>
    /// <param name="options">The fusion options controlling output generation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous build operation, returning a FusionResult with statistics.</returns>
    /// <exception cref="IOException">Thrown if the output file exists and overwrite is disabled.</exception>
    public async Task<FusionResult> BuildOutputAsync(List<FileProcessingInfo> files, FuseOptions options, CancellationToken cancellationToken)
    {
        // Track start time for duration calculation
        var startTime = DateTime.Now;
        // 1. Determine Base Filename (without extension or path)
        string baseFileName;
        if (!string.IsNullOrWhiteSpace(options.OutputFileName))
        {
            // User provided a custom name
            baseFileName = options.OutputFileName;

            // Strip extension if present so we can handle it consistently
            if (Path.HasExtension(baseFileName))
            {
                baseFileName = Path.GetFileNameWithoutExtension(baseFileName);
            }
        }
        else
        {
            // Auto-generate name based on source directory and timestamp
            // Format: ProjectName_YYYY-MM-DD_HHmm
            var allSuffix = options.ApplyAllOptions ? "_all" : string.Empty;

            // Sanitize directory name: replace dots with underscores to avoid confusion with file extensions
            // Example: "My.Project" -> "My_Project"
            var dirName = Path.GetFileName(options.SourceDirectory).Replace('.', '_');

            // Use Project First format with date and time separate
            var dateStamp = DateTime.Now.ToString("yyyy-MM-dd");
            var timeStamp = DateTime.Now.ToString("HHmm");

            baseFileName = $"{dirName}{allSuffix}_{dateStamp}_{timeStamp}";
        }

        // 2. State Tracking
        long totalGlobalTokens = 0;    // Total tokens across ALL parts
        long currentFileTokens = 0;    // Tokens in the CURRENT active output file
        int processedFileCount = 0;    // Number of files successfully written
        int currentPart = 1;           // Current part number for splitting
        bool hasSplitOccurred = false; // Tracks if we ever needed to split (affects naming)

        var createdFilePaths = new List<string>();
        var fileTokenStats = options.TrackTopTokenFiles ? new List<FileTokenInfo>() : null;
        var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 3. Setup Temporary File
        // We write to a .tmp file first, then rename it with the final token count/part number when done.
        // This ensures filenames accurately reflect their content size (e.g., _554k.txt).
        var tempFilePath = Path.Combine(options.OutputDirectory, $"{baseFileName}.tmp");

        // Ensure output directory exists
        Directory.CreateDirectory(options.OutputDirectory);

        // Open the initial stream
        var (currentStream, currentWriter) = CreateStream(tempFilePath);

        try
        {
            // Write header to the first file
            await WriteMetadataHeaderAsync(currentWriter, options, currentPart);

            // Process each file in the list
            foreach (var fileInfo in files)
            {
                // Check for cancellation (either user-initiated or token limit reached)
                if (localCts.IsCancellationRequested)
                {
                    break;
                }

                // Process content first (read, minify, trim)
                var processedContent = await _contentProcessor.ProcessContentAsync(fileInfo, options, localCts.Token);

                // SKIP EMPTY OR TRIVIAL FILES
                // We skip if content is whitespace, or if it's just empty JSON/Array structures
                if (IsContentTrivial(processedContent))
                {
                    continue;
                }

                // Calculate tokens for this specific file entry
                var fileTokenCount = _tokenizer.Encode(processedContent).Count;

                // Track file token stats for analysis (if enabled)
                fileTokenStats?.Add(new FileTokenInfo(fileInfo.RelativePath, fileTokenCount));

                // Add overhead for <file path="..."> tags + newlines
                // Approx 30 tokens is a safe buffer for the XML structure
                var markerOverhead = 30;
                var totalEntryTokens = fileTokenCount + markerOverhead;

                // --- SPLIT LOGIC ---
                // Check if adding this file would exceed the split threshold.
                // We ensure currentFileTokens > 0 to prevent infinite loops if a single file is larger than the limit.
                if (options.SplitTokens.HasValue &&
                    (currentFileTokens + totalEntryTokens > options.SplitTokens.Value) &&
                    currentFileTokens > 0)
                {
                    // 1. Close current temp file resources
                    await currentWriter.DisposeAsync();
                    await currentStream.DisposeAsync();

                    // 2. Finalize the current part (Rename .tmp -> _partX_100k.txt)
                    // Since we are splitting, isMultiPart is definitely true.
                    var finalPath = FinalizeFile(tempFilePath, options.OutputDirectory, baseFileName, currentPart, currentFileTokens, options.Overwrite, true);
                    createdFilePaths.Add(finalPath);

                    // 3. Reset State for next part
                    currentPart++;
                    currentFileTokens = 0;
                    hasSplitOccurred = true;

                    // 4. Open new temp file
                    (currentStream, currentWriter) = CreateStream(tempFilePath);

                    // 5. Write header for new part
                    await WriteMetadataHeaderAsync(currentWriter, options, currentPart);
                }

                // --- CONTENT WRITING (Simplified XML) ---
                var sb = new StringBuilder();

                // Normalize path to forward slashes for consistency and token efficiency
                var normalizedPath = fileInfo.RelativePath.Replace('\\', '/');

                // 1. Opening Tag with path attribute
                // Format: <file path="src/Program.cs">
                sb.AppendLine($"<file path=\"{normalizedPath}\">");

                // 2. Content
                sb.Append(processedContent);

                // 3. Ensure content ends with newline before closing tag
                // This prevents the closing tag from being appended to the last line of code
                if (!processedContent.EndsWith('\n'))
                {
                    sb.AppendLine();
                }

                // 4. Closing Tag + Newline
                sb.AppendLine("</file>");

                // Write to current stream
                await currentWriter.WriteAsync(sb.ToString());

                // Update counters
                currentFileTokens += totalEntryTokens;
                totalGlobalTokens += totalEntryTokens;
                processedFileCount++;

                // Check Global MaxTokens (Hard Stop)
                // If the total tokens across ALL parts exceeds the absolute max, we stop entirely.
                if (options.MaxTokens.HasValue && totalGlobalTokens > options.MaxTokens.Value)
                {
                    await localCts.CancelAsync();
                }
            }

            // --- FINALIZE LAST FILE ---
            // Close the stream first to release the file lock
            await currentWriter.DisposeAsync();
            await currentStream.DisposeAsync();
            currentWriter = null;
            currentStream = null;

            if (currentFileTokens > 0)
            {
                // If hasSplitOccurred is true, this is the last part of a multi-part set (e.g., Part 3).
                // If hasSplitOccurred is false, this is just a single file (Part 1), so we pass false to omit "_part1".
                var finalPath = FinalizeFile(tempFilePath, options.OutputDirectory, baseFileName, currentPart, currentFileTokens, options.Overwrite, hasSplitOccurred);
                createdFilePaths.Add(finalPath);
            }
            else
            {
                // Clean up empty temp file if nothing was written (e.g., all files were filtered out)
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }

            // --- RETURN RESULT ---
            var duration = DateTime.Now - startTime;

            // Get top 5 token consumers (if tracking is enabled)
            var topTokenFiles = fileTokenStats?
                .OrderByDescending(f => f.Count)
                .Take(5)
                .ToList() ?? new List<FileTokenInfo>();

            return new FusionResult(
                createdFilePaths,
                totalGlobalTokens,
                processedFileCount,
                files.Count,
                duration,
                topTokenFiles);
        }
        finally
        {
            // Ensure streams are properly disposed even if an exception occurs
            if (currentWriter != null)
            {
                await currentWriter.DisposeAsync();
            }

            if (currentStream != null)
            {
                await currentStream.DisposeAsync();
            }

            // Safety cleanup for temp file
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

    /// <summary>
    ///     Helper to open a stream for the temporary file.
    /// </summary>
    /// <param name="path">The path to the temporary file.</param>
    /// <returns>A tuple containing the file stream and stream writer.</returns>
    private static (FileStream, StreamWriter) CreateStream(string path)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        var writer = new StreamWriter(stream, Encoding.UTF8);
        return (stream, writer);
    }

    /// <summary>
    ///     Renames the temporary file to the final filename with token count and optional part number.
    /// </summary>
    /// <param name="tempPath">The path to the temporary file.</param>
    /// <param name="directory">The output directory.</param>
    /// <param name="baseName">The base filename.</param>
    /// <param name="part">The part number.</param>
    /// <param name="tokenCount">The token count for this file.</param>
    /// <param name="overwrite">Whether to overwrite existing files.</param>
    /// <param name="isMultiPart">Whether this file is part of a multi-part set.</param>
    /// <returns>The full path to the finalized file.</returns>
    private string FinalizeFile(string tempPath, string directory, string baseName, int part, long tokenCount, bool overwrite, bool isMultiPart)
    {
        // Format tokens: 800000 -> 800k, 500 -> 500t
        var tokenString = FormatTokenCount(tokenCount);

        // Construct filename
        // If multi-part: name_part1_800k.txt
        // If single-part: name_50k.txt
        var partSuffix = isMultiPart ? $"_part{part}" : "";
        var fileName = $"{baseName}{partSuffix}_{tokenString}.txt";
        var finalPath = Path.Combine(directory, fileName);

        if (File.Exists(finalPath))
        {
            if (!overwrite)
            {
                // Fallback if overwrite disabled: try appending a timestamp to avoid collision
                fileName = $"{baseName}{partSuffix}_{tokenString}_{DateTime.Now:mmss}.txt";
                finalPath = Path.Combine(directory, fileName);
            }
            else
            {
                File.Delete(finalPath);
            }
        }

        File.Move(tempPath, finalPath);
        return finalPath;
    }

    /// <summary>
    ///     Formats a token count into a readable string (e.g., 1k, 500t).
    /// </summary>
    /// <param name="count">The raw token count.</param>
    /// <returns>A formatted string representation.</returns>
    private static string FormatTokenCount(long count)
    {
        if (count < 1000)
        {
            return $"{count}t";
        }

        // Round to nearest whole number for thousands (e.g., 554.3k -> 554k)
        // The :0 format specifier ensures no decimal places are shown
        return $"{count / 1000.0:0}k";
    }

    /// <summary>
    ///     Writes a minimal header to the output file.
    /// </summary>
    /// <param name="writer">The stream writer to write to.</param>
    /// <param name="options">The fusion options.</param>
    /// <param name="partNumber">The current part number.</param>
    private static async Task WriteMetadataHeaderAsync(StreamWriter writer, FuseOptions options, int partNumber)
    {
        // No header - start directly with file content
        await Task.CompletedTask;
    }

    /// <summary>
    ///     Attempts to find the project root directory by looking for .sln files or .git folders.
    /// </summary>
    /// <param name="startPath">The directory to start searching from.</param>
    /// <returns>The path to the root directory, or null if not found.</returns>
    private static string? FindRootDirectory(string startPath)
    {
        try
        {
            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                // Check for Solution file or Git folder
                if (dir.GetFiles("*.sln").Length > 0 || dir.GetDirectories(".git").Length > 0)
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            // Ignore permission errors or invalid paths during root search
        }

        return null;
    }

    /// <summary>
    ///     Determines if the processed content is trivial and should be skipped.
    /// </summary>
    /// <param name="content">The processed file content.</param>
    /// <returns>True if the content is empty, whitespace, or trivial JSON/Array structures.</returns>
    private static bool IsContentTrivial(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        var trimmed = content.Trim();

        // Check for empty JSON object "{}"
        if (trimmed == "{}")
        {
            return true;
        }

        // Check for empty JSON array "[]"
        if (trimmed == "[]")
        {
            return true;
        }

        // Check for empty XML/HTML self-closing tags that might be leftovers (rare but possible)
        // e.g. <root />
        if (trimmed.StartsWith('<') && trimmed.EndsWith("/>") && trimmed.Length < 10)
        {
            return true;
        }

        return false;
    }
}