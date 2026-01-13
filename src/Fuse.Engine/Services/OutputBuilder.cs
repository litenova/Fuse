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
///     Builds the final fused output file from processed content.
/// </summary>
/// <remarks>
///     <para>
///         This service is responsible for:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>Creating the output file with proper encoding</description>
///         </item>
///         <item>
///             <description>Adding file markers and metadata</description>
///         </item>
///         <item>
///             <description>Tracking and enforcing token limits</description>
///         </item>
///         <item>
///             <description>Displaying progress and statistics</description>
///         </item>
///     </list>
///     <para>
///         The output format uses special markers to delimit file content:
///         <c>&lt;|path/to/file|&gt;</c>
///     </para>
/// </remarks>
public sealed class OutputBuilder : IOutputBuilder
{
    /// <summary>
    ///     The console interface for progress display and output.
    /// </summary>
    private readonly IAnsiConsole _console;

    /// <summary>
    ///     The content processor for transforming file content.
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
    ///     Builds the fused output file with progress display and token tracking.
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
            // If the user provided an extension (e.g., .md, .json), we respect it.
            if (!Path.HasExtension(outputFileName))
                outputFileName += ".txt";
        }
        else
        {
            // Auto-generate name based on source directory and timestamp
            // Format: fused_DirectoryName_YYYYMMDDHHMMSS.txt
            outputFileName = $"fused_{Path.GetFileName(options.SourceDirectory)}_{DateTime.Now:yyyyMMddHHmmss}.txt";
        }

        // Construct full output path
        var outputFilePath = Path.Combine(options.OutputDirectory, outputFileName);

        // Check for existing file and overwrite permission
        if (File.Exists(outputFilePath) && !options.Overwrite)
        {
            _console.MarkupLine($"[yellow]Warning:[/] The file [underline]'{outputFilePath}'[/] already exists and overwrite is disabled. Operation aborted.");
            return;
        }

        // Track total token count across all files
        long totalTokenCount = 0;

        // Create a linked cancellation token for token limit enforcement
        var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Open output file stream with buffered writing
        await using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await using var writer = new StreamWriter(outputStream, Encoding.UTF8);

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

                    // Build the output block for this file
                    var sb = new StringBuilder();

                    // Add opening file marker
                    sb.AppendLine($"<|{fileInfo.RelativePath}|>");

                    // Add metadata if requested
                    if (options.IncludeMetadata)
                        sb.AppendLine($"[Size: {fileInfo.Info.Length} bytes | Modified: {fileInfo.Info.LastWriteTime:yyyy-MM-dd HH:mm:ss}]");

                    // Process and add file content
                    var processedContent = await _contentProcessor.ProcessContentAsync(fileInfo, options, localCts.Token);
                    sb.AppendLine(processedContent);

                    // Add closing file marker
                    sb.AppendLine($"<|{fileInfo.RelativePath}|>");
                    sb.AppendLine();

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

                    // Update progress
                    task.Increment(1);
                }
            });

        // Display final statistics
        _console.MarkupLine($"[bold]Output File:[/][underline blue] {outputFilePath}[/]");
        _console.MarkupLine($"[bold]Final Size:[/][green] {new FileInfo(outputFilePath).Length:N0} bytes[/]");

        if (options.ShowTokenCount)
            _console.MarkupLine($"[bold]Est. Tokens:[/][yellow] {totalTokenCount:N0}[/]");
    }
}