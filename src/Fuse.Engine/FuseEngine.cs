// -----------------------------------------------------------------------
// <copyright file="FuseEngine.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Fuse.Core;
using Fuse.Engine.Services;
using Spectre.Console;

namespace Fuse.Engine;

/// <summary>
/// The main orchestrator for file fusion operations.
/// </summary>
/// <remarks>
/// <para>
/// This class serves as the central coordinator that:
/// </para>
/// <list type="bullet">
///     <item><description>Receives fusion options from CLI commands</description></item>
///     <item><description>Delegates configuration resolution to <see cref="IConfigurationResolver"/></description></item>
///     <item><description>Delegates file collection to <see cref="IFileCollector"/></description></item>
///     <item><description>Delegates output generation to <see cref="IOutputBuilder"/></description></item>
/// </list>
/// <para>
/// Following the Single Responsibility Principle, this class only handles
/// orchestration and error handling, delegating actual work to specialized services.
/// </para>
/// </remarks>
public sealed class FuseEngine
{
    /// <summary>
    /// The console interface for displaying status and output.
    /// </summary>
    private readonly IAnsiConsole _console;

    /// <summary>
    /// The service responsible for resolving configuration options.
    /// </summary>
    private readonly IConfigurationResolver _configResolver;

    /// <summary>
    /// The service responsible for collecting files to process.
    /// </summary>
    private readonly IFileCollector _fileCollector;

    /// <summary>
    /// The service responsible for building the output file.
    /// </summary>
    private readonly IOutputBuilder _outputBuilder;

    /// <summary>
    /// Initializes a new instance of the <see cref="FuseEngine"/> class.
    /// </summary>
    /// <param name="console">The console for output display.</param>
    /// <param name="configResolver">The configuration resolver service.</param>
    /// <param name="fileCollector">The file collector service.</param>
    /// <param name="outputBuilder">The output builder service.</param>
    public FuseEngine(
        IAnsiConsole console,
        IConfigurationResolver configResolver,
        IFileCollector fileCollector,
        IOutputBuilder outputBuilder)
    {
        _console = console;
        _configResolver = configResolver;
        _fileCollector = fileCollector;
        _outputBuilder = outputBuilder;
    }

    /// <summary>
    /// Executes the file fusion operation with the specified options.
    /// </summary>
    /// <param name="options">The options controlling the fusion operation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous fusion operation.</returns>
    /// <remarks>
    /// <para>
    /// The fusion process consists of three main steps:
    /// </para>
    /// <list type="number">
    ///     <item><description>Resolve configuration by merging user options with template defaults</description></item>
    ///     <item><description>Collect files matching the resolved configuration</description></item>
    ///     <item><description>Build the output file with processed content</description></item>
    /// </list>
    /// <para>
    /// Progress and status information is displayed throughout the operation.
    /// Any exceptions are caught and displayed to the user.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var options = new FuseOptions
    /// {
    ///     SourceDirectory = @"C:\Projects\MyApp",
    ///     Template = ProjectTemplate.DotNet,
    ///     ShowTokenCount = true
    /// };
    /// await engine.FuseAsync(options, cancellationToken);
    /// </code>
    /// </example>
    public async Task FuseAsync(FuseOptions options, CancellationToken cancellationToken)
    {
        // Record start time for duration calculation
        var startTime = DateTime.Now;

        // Display header rule
        _console.Write(new Rule());

        // Display operation summary table
        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders();

        table.AddColumn(new TableColumn("").NoWrap());
        table.AddColumn(new TableColumn(""));
        table.AddRow("[bold]Source:[/]", options.SourceDirectory);
        table.AddRow("[bold]Output Dir:[/]", options.OutputDirectory);
        table.AddRow("[bold]Template:[/]", options.Template?.ToString() ?? "[grey]Generic (Default)[/]");

        _console.Write(table);

        try
        {
            // ===== Step 1: Resolve Configuration =====
            // Merge user options with template defaults
            var config = _configResolver.Resolve(options);

            // ===== Step 2: Collect Files =====
            // Search for files matching the configuration
            _console.MarkupLine("Searching for files...");
            var files = _fileCollector.CollectFiles(options, config);
            _console.MarkupLine($"Found [green]{files.Count}[/] files to process.");

            // Handle case where no files were found
            if (files.Count == 0)
            {
                _console.MarkupLine("[yellow]Warning:[/] No files found matching the criteria. Aborting.");
                return;
            }

            // ===== Step 3: Build Output =====
            // Process files and generate the fused output
            await _outputBuilder.BuildOutputAsync(files, options, cancellationToken);
        }
        catch (Exception ex)
        {
            // Display any errors that occurred during processing
            _console.WriteException(ex, ExceptionFormats.ShortenPaths);
        }
        finally
        {
            // Calculate and display operation duration
            var duration = DateTime.Now - startTime;
            var ruleTitle = $"[bold green]Operation Complete[/][dim]({duration.TotalSeconds:F1}s)[/]";
            _console.Write(new Rule(ruleTitle).LeftJustified());
        }
    }
}
