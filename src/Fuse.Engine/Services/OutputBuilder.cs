//-----------------------------------------------------------------------
// <copyright file="OutputBuilder.cs" company="Fuse">
// Copyright (c) Fuse. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using System.Text;
using Fuse.Core;
using Spectre.Console;
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
///             <description>Formatting file content with clear delimiters for LLM parsing.</description>
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
    ///     The console interface for progress display and user feedback.
    /// </summary>
    private readonly IAnsiConsole _console;

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
    /// <param name="console">The console for output and progress display.</param>
    /// <param name="contentProcessor">The content processor for file transformations.</param>
    public OutputBuilder(IAnsiConsole console, IContentProcessor contentProcessor)
    {
        _console = console;
        _contentProcessor = contentProcessor;

        // Initialize tokenizer with the cl100k_base encoding (used by GPT-4 and GPT-3.5-turbo)
        _tokenizer = TikToken.GetEncoding("cl100k_base");
    }

    /// <inheritdoc />
    /// <summary>
    ///     Builds the fused output file(s) with progress display, token tracking, and automatic splitting.
    /// </summary>
    /// <param name="files">The list of files to include in the output.</param>
    /// <param name="options">The fusion options controlling output generation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous build operation.</returns>
    /// <exception cref="IOException">Thrown if the output file exists and overwrite is disabled.</exception>
    public async Task BuildOutputAsync(List<FileProcessingInfo> files, FuseOptions options, CancellationToken cancellationToken)
    {
        // 1. Determine Base Filename
        // We need a clean base name (e.g., "fused_src") to append part numbers to later (e.g., "fused_src_part1.txt")
        string baseFileName;
        if (!string.IsNullOrWhiteSpace(options.OutputFileName))
        {
            // User provided a custom name
            baseFileName = options.OutputFileName;

            // Strip extension if present so we can handle it consistently
            if (Path.HasExtension(baseFileName))
                baseFileName = Path.GetFileNameWithoutExtension(baseFileName);
        }
        else
        {
            // Auto-generate name based on source directory and timestamp
            // Format: fused_DirectoryName_YYYYMMDDHHMMSS
            var allSuffix = options.ApplyAllOptions ? "_all" : string.Empty;
            baseFileName = $"fused_{Path.GetFileName(options.SourceDirectory)}{allSuffix}_{DateTime.Now:yyyyMMddHHmmss}";
        }

        // 2. State Tracking
        long totalGlobalTokens = 0; // Total tokens across ALL parts
        long currentFileTokens = 0; // Tokens in the CURRENT active output file
        int processedFileCount = 0; // Number of files successfully written
        int currentPart = 1;        // Current part number for splitting

        // Track created files for final summary
        var createdFilePaths = new List<string>();

        // Create a linked cancellation token for internal token limit enforcement
        var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 3. Initialize First File Stream
        // We open the first file immediately to write the header
        var (currentStream, currentWriter, currentFilePath) = await CreateNewOutputFileAsync(
            options.OutputDirectory, baseFileName, currentPart, options);
        createdFilePaths.Add(currentFilePath);

        try
        {
            // Write initial header to the first file
            await WriteMetadataHeaderAsync(currentWriter, options, currentPart);

            // Display progress bar using Spectre.Console
            await _console.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn()
                )
                .StartAsync(async ctx =>
                {
                    // Create progress task with total file count
                    var task = ctx.AddTask("[green]Fusing files[/]", new ProgressTaskSettings { MaxValue = files.Count });

                    // Process each file in the list
                    foreach (var fileInfo in files)
                    {
                        // Check for cancellation (either user-initiated or token limit reached)
                        if (localCts.IsCancellationRequested) break;

                        // Process content first (read, minify, trim)
                        var processedContent = await _contentProcessor.ProcessContentAsync(fileInfo, options, localCts.Token);

                        // SKIP EMPTY OR TRIVIAL FILES
                        // We skip if content is whitespace, or if it's just empty JSON/Array structures
                        if (IsContentTrivial(processedContent))
                        {
                            task.Increment(1);
                            continue;
                        }

                        // Calculate tokens for this specific file entry
                        var fileTokenCount = _tokenizer.Encode(processedContent).Count;

                        // Add overhead for file markers.
                        // Structure: <|path|>\n [Content] \n<|/|>\n
                        // This adds approximately 20-25 tokens depending on path length.
                        var markerOverhead = 25;
                        var totalEntryTokens = fileTokenCount + markerOverhead;

                        // CHECK FOR SPLIT CONDITION
                        // Logic: If we have a split limit, AND adding this file would exceed it,
                        // AND we aren't at the start of a new file (to prevent infinite loops on single huge files).
                        if (options.SplitTokens.HasValue &&
                            (currentFileTokens + totalEntryTokens > options.SplitTokens.Value) &&
                            currentFileTokens > 0)
                        {
                            // 1. Close current file resources
                            await currentWriter.DisposeAsync();
                            await currentStream.DisposeAsync();

                            // 2. Rotate state
                            currentPart++;
                            currentFileTokens = 0;

                            // 3. Open new file for the next part
                            (currentStream, currentWriter, currentFilePath) = await CreateNewOutputFileAsync(
                                options.OutputDirectory, baseFileName, currentPart, options);
                            createdFilePaths.Add(currentFilePath);

                            // 4. Write header for the new part
                            await WriteMetadataHeaderAsync(currentWriter, options, currentPart);

                            _console.MarkupLine($"[dim]Split limit reached. Starting Part {currentPart}...[/]");
                        }

                        // Build the output block for this file
                        var sb = new StringBuilder();

                        // Normalize path to forward slashes for consistency and token efficiency
                        var normalizedPath = fileInfo.RelativePath.Replace('\\', '/');

                        // 1. Opening Tag + Newline
                        // Placing content on a new line prevents tokenizer merging issues
                        sb.AppendLine($"<|{normalizedPath}|>");

                        // 2. Metadata (Optional)
                        if (options.IncludeMetadata)
                        {
                            sb.AppendLine($"[Size: {fileInfo.Info.Length} bytes | Modified: {fileInfo.Info.LastWriteTime:yyyy-MM-dd HH:mm:ss}]");
                        }

                        // 3. Content
                        sb.Append(processedContent);

                        // 4. Ensure content ends with newline before closing tag
                        // This prevents the closing tag from being appended to the last line of code
                        if (!processedContent.EndsWith('\n'))
                        {
                            sb.AppendLine();
                        }

                        // 5. Closing Tag + Newline
                        // We add a newline after the closing tag to separate it from the next file header
                        sb.AppendLine("<|/|>");

                        // Write to current stream
                        await currentWriter.WriteAsync(sb.ToString());

                        // Update counters
                        currentFileTokens += totalEntryTokens;
                        totalGlobalTokens += totalEntryTokens;
                        processedFileCount++;
                        task.Increment(1);

                        // Check Global MaxTokens (Hard Stop)
                        // If the total tokens across ALL parts exceeds the absolute max, we stop entirely.
                        if (options.MaxTokens.HasValue && totalGlobalTokens > options.MaxTokens.Value)
                        {
                            _console.MarkupLine($"[yellow]Global token limit of {options.MaxTokens:N0} reached. Stopping.[/]");
                            await localCts.CancelAsync();
                        }
                    }
                });

            // Display final statistics
            _console.MarkupLine($"[bold]Processing Complete[/]");
            _console.MarkupLine($"[bold]Total Files:[/][green] {processedFileCount}/{files.Count}[/]");

            // Display each created file with its size in KB
            foreach (var path in createdFilePaths)
            {
                var fileInfo = new FileInfo(path);
                var sizeKb = fileInfo.Length / 1024.0;
                _console.MarkupLine($"[bold]Output:[/][underline blue] {fileInfo.Name}[/] ([green]{sizeKb:N2} KB[/])");
            }

            if (options.ShowTokenCount)
                _console.MarkupLine($"[bold]Total Tokens:[/][yellow] {totalGlobalTokens:N0}[/]");
        }
        finally
        {
            // Ensure streams are properly disposed even if an exception occurs
            if (currentWriter != null) await currentWriter.DisposeAsync();
            if (currentStream != null) await currentStream.DisposeAsync();
        }
    }

    /// <summary>
    ///     Creates a new output file stream and writer for a specific part.
    /// </summary>
    /// <param name="directory">The output directory.</param>
    /// <param name="baseName">The base filename (without extension).</param>
    /// <param name="part">The part number (1-based).</param>
    /// <param name="options">The fusion options.</param>
    /// <returns>A tuple containing the stream, the writer, and the full file path.</returns>
    /// <exception cref="IOException">Thrown if the file exists and overwrite is disabled.</exception>
    private Task<(FileStream, StreamWriter, string)> CreateNewOutputFileAsync(
        string directory,
        string baseName,
        int part,
        FuseOptions options)
    {
        string fileName;

        // Naming Convention:
        // If SplitTokens is set, we ALWAYS append _partX to ensure consistent naming.
        // If not set, we use the base name (single file mode).
        if (options.SplitTokens.HasValue)
        {
            fileName = $"{baseName}_part{part}.txt";
        }
        else
        {
            fileName = $"{baseName}.txt";
        }

        var fullPath = Path.Combine(directory, fileName);

        // Check for existing file and overwrite permission
        if (File.Exists(fullPath) && !options.Overwrite)
        {
            throw new IOException($"The file '{fullPath}' already exists and overwrite is disabled.");
        }

        // Open output file stream with buffered writing (4KB buffer)
        var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        var writer = new StreamWriter(stream, Encoding.UTF8);

        return Task.FromResult((stream, writer, fullPath));
    }

    /// <summary>
    ///     Writes the context metadata header to the output file.
    /// </summary>
    /// <param name="writer">The stream writer to write to.</param>
    /// <param name="options">The fusion options.</param>
    /// <param name="partNumber">The current part number.</param>
    /// <remarks>
    ///     The header provides context to the LLM about the project structure,
    ///     the file format being used, and which part of the split this file represents.
    /// </remarks>
    private static async Task WriteMetadataHeaderAsync(StreamWriter writer, FuseOptions options, int partNumber)
    {
        var sb = new StringBuilder();

        // Find the project root (Solution or Git root) to calculate relative base path
        var rootPath = FindRootDirectory(options.SourceDirectory);
        var basePathLabel = "Source Path";
        var pathValue = options.SourceDirectory;

        if (rootPath != null)
        {
            basePathLabel = "Base Path";

            // Calculate relative path from the root (e.g., "src/MyService")
            pathValue = Path.GetRelativePath(rootPath, options.SourceDirectory).Replace('\\', '/');

            // If the source directory IS the root, represent it as "/"
            if (pathValue == ".") pathValue = "/";
        }

        // Write the header block
        sb.AppendLine("# FUSE CONTEXT");
        sb.AppendLine($"# {basePathLabel}: {pathValue}");

        // Add Part Metadata if splitting is active
        if (options.SplitTokens.HasValue)
        {
            sb.AppendLine($"# Part: {partNumber}");
        }

        // Describe the file format explicitly for the LLM
        sb.AppendLine("# File Format:");
        sb.AppendLine("# <|path/to/file|>");
        sb.AppendLine("# [Content]");
        sb.AppendLine("# <|/|>");
        sb.AppendLine(); // Empty line to separate header from first file

        await writer.WriteAsync(sb.ToString());
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
            return true;

        var trimmed = content.Trim();

        // Check for empty JSON object "{}"
        if (trimmed == "{}")
            return true;

        // Check for empty JSON array "[]"
        if (trimmed == "[]")
            return true;

        // Check for empty XML/HTML self-closing tags that might be leftovers (rare but possible)
        // e.g. <root />
        if (trimmed.StartsWith('<') && trimmed.EndsWith("/>") && trimmed.Length < 10)
            return true;

        return false;
    }
}