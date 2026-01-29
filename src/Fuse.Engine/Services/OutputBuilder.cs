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
/// Builds the final fused output file from processed content.
/// </summary>
/// <remarks>
/// <para>
/// This service is responsible for:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Creating the output file with proper encoding</description>
/// </item>
/// <item>
/// <description>Adding context metadata and format instructions</description>
/// </item>
/// <item>
/// <description>Adding file markers and metadata</description>
/// </item>
/// <item>
/// <description>Tracking and enforcing token limits</description>
/// </item>
/// <item>
/// <description>Skipping files that become empty after minification</description>
/// </item>
/// </list>
/// </remarks>
public sealed class OutputBuilder : IOutputBuilder
{
    /// <summary>
    /// The console interface for progress display and output.
    /// </summary>
    private readonly IAnsiConsole _console;

    /// <summary>
    /// The content processor for transforming file content.
    /// </summary>
    private readonly IContentProcessor _contentProcessor;

    /// <summary>
    /// The tokenizer for GPT token counting (uses cl100k_base encoding).
    /// </summary>
    private readonly TikToken _tokenizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutputBuilder" /> class.
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
    /// Builds the fused output file with progress display and token tracking.
    /// </summary>
    public async Task BuildOutputAsync(List<FileProcessingInfo> files, FuseOptions options, CancellationToken cancellationToken)
    {
        // Determine the final output file name
        string outputFileName;
        if (!string.IsNullOrWhiteSpace(options.OutputFileName))
        {
            // User provided a custom name
            outputFileName = options.OutputFileName;

            // Check if the provided name has an extension.
            // If not, append .txt to ensure the file is easily readable.
            if (!Path.HasExtension(outputFileName))
                outputFileName += ".txt";
        }
        else
        {
            // Auto-generate name based on source directory and timestamp
            // Format: fused_DirectoryName_YYYYMMDDHHMMSS.txt (or fused_DirectoryName_all_YYYYMMDDHHMMSS.txt if --all is used)
            var allSuffix = options.ApplyAllOptions ? "_all" : string.Empty;
            outputFileName = $"fused_{Path.GetFileName(options.SourceDirectory)}{allSuffix}_{DateTime.Now:yyyyMMddHHmmss}.txt";
        }

        // Construct full output path
        var outputFilePath = Path.Combine(options.OutputDirectory, outputFileName);

        // Check for existing file and overwrite permission
        if (File.Exists(outputFilePath) && !options.Overwrite)
        {
            _console.MarkupLine($"[yellow]Warning:[/] The file [underline]'{outputFilePath}'[/] already exists and overwrite is disabled. Operation aborted.");
            return;
        }

        // Track statistics
        long totalTokenCount = 0;
        int processedFileCount = 0;

        // Create a linked cancellation token for token limit enforcement
        var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Open output file stream with buffered writing
        await using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await using var writer = new StreamWriter(outputStream, Encoding.UTF8);

        // 1. Write Context Metadata Header
        // This helps the LLM understand the project structure and file format
        await WriteMetadataHeaderAsync(writer, options);

        // Display progress bar during processing
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

                // Process each file
                foreach (var fileInfo in files)
                {
                    // Check for cancellation (either user-initiated or token limit reached)
                    if (localCts.IsCancellationRequested) break;

                    // Process content first to see if it's empty
                    var processedContent = await _contentProcessor.ProcessContentAsync(fileInfo, options, localCts.Token);

                    // SKIP EMPTY OR TRIVIAL FILES
                    // We skip if content is whitespace, or if it's just empty JSON/Array structures
                    if (IsContentTrivial(processedContent))
                    {
                        task.Increment(1);
                        continue;
                    }

                    // Build the output block for this file
                    var sb = new StringBuilder();

                    // Normalize path to forward slashes for consistency and token efficiency
                    var normalizedPath = fileInfo.RelativePath.Replace('\\', '/');

                    // Add opening file marker
                    // Format: <|path/to/file|>
                    sb.Append($"<|{normalizedPath}|>");

                    // Only add newline if metadata is present, otherwise content starts immediately
                    // This saves vertical space in the output file
                    if (options.IncludeMetadata)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"[Size: {fileInfo.Info.Length} bytes | Modified: {fileInfo.Info.LastWriteTime:yyyy-MM-dd HH:mm:ss}]");
                    }

                    // Add processed content
                    sb.Append(processedContent);

                    // Ensure content ends with newline before closing tag
                    // This prevents the closing tag from being appended to the last line of code
                    if (!processedContent.EndsWith('\n'))
                    {
                        sb.AppendLine();
                    }

                    // Add simplified closing file marker
                    // Format: <|/|>
                    // We do NOT add a trailing newline here, allowing the next file header to start immediately
                    sb.Append("<|/|>");

                    // Track token count if needed
                    if (options.ShowTokenCount || options.MaxTokens.HasValue)
                    {
                        var tokenCount = _tokenizer.Encode(processedContent).Count;
                        var currentTotal = Interlocked.Add(ref totalTokenCount, tokenCount);

                        // Check if token limit has been reached
                        if (options.MaxTokens.HasValue && currentTotal > options.MaxTokens.Value)
                        {
                            _console.MarkupLine($"[yellow]Token limit of {options.MaxTokens:N0} reached. Stopping.[/]");
                            await localCts.CancelAsync();
                        }
                    }

                    // Write to output file
                    await writer.WriteAsync(sb.ToString());

                    // Update stats and progress
                    processedFileCount++;
                    task.Increment(1);
                }
            });

        // Display final statistics
        _console.MarkupLine($"[bold]Output File:[/][underline blue] {outputFilePath}[/]");
        _console.MarkupLine($"[bold]Files Included:[/][green] {processedFileCount}/{files.Count}[/]");
        _console.MarkupLine($"[bold]Final Size:[/][green] {new FileInfo(outputFilePath).Length:N0} bytes[/]");
        if (options.ShowTokenCount)
            _console.MarkupLine($"[bold]Est. Tokens:[/][yellow] {totalTokenCount:N0}[/]");
    }

    /// <summary>
    /// Writes the context metadata header to the output file.
    /// </summary>
    /// <param name="writer">The stream writer to write to.</param>
    /// <param name="options">The fusion options.</param>
    private static async Task WriteMetadataHeaderAsync(StreamWriter writer, FuseOptions options)
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
        sb.AppendLine("# File Format: <|path/to/file|> content <|/|>");
        sb.AppendLine(); // Empty line to separate header from first file

        await writer.WriteAsync(sb.ToString());
    }

    /// <summary>
    /// Attempts to find the project root directory by looking for .sln files or .git folders.
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
    /// Determines if the processed content is trivial and should be skipped.
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