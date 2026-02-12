// -----------------------------------------------------------------------
// <copyright file="FuseEngine.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Fuse.Core;
using Fuse.Core.Abstractions;
using Fuse.Engine.Services;

namespace Fuse.Engine;

/// <summary>
///     The main orchestrator for file fusion operations.
/// </summary>
/// <remarks>
///     <para>
///         This class serves as the central coordinator that:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>Receives fusion options from CLI commands</description>
///         </item>
///         <item>
///             <description>Delegates configuration resolution to <see cref="IConfigurationResolver" /></description>
///         </item>
///         <item>
///             <description>Delegates file collection to <see cref="IFileCollector" /></description>
///         </item>
///         <item>
///             <description>Delegates output generation to <see cref="IOutputBuilder" /></description>
///         </item>
///     </list>
///     <para>
///         Following the Single Responsibility Principle, this class only handles
///         orchestration and error handling, delegating actual work to specialized services.
///     </para>
/// </remarks>
public sealed class FuseEngine
{
    /// <summary>
    ///     The service responsible for resolving configuration options.
    /// </summary>
    private readonly IConfigurationResolver _configResolver;

    /// <summary>
    ///     The console UI for displaying status and output.
    /// </summary>
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     The service responsible for collecting files to process.
    /// </summary>
    private readonly IFileCollector _fileCollector;

    /// <summary>
    ///     The service responsible for building the output file.
    /// </summary>
    private readonly IOutputBuilder _outputBuilder;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseEngine" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for output display.</param>
    /// <param name="configResolver">The configuration resolver service.</param>
    /// <param name="fileCollector">The file collector service.</param>
    /// <param name="outputBuilder">The output builder service.</param>
    public FuseEngine(
        IConsoleUI consoleUI,
        IConfigurationResolver configResolver,
        IFileCollector fileCollector,
        IOutputBuilder outputBuilder)
    {
        _consoleUI = consoleUI;
        _configResolver = configResolver;
        _fileCollector = fileCollector;
        _outputBuilder = outputBuilder;
    }

    /// <summary>
    ///     Executes the file fusion operation with the specified options.
    /// </summary>
    /// <param name="options">The options controlling the fusion operation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous fusion operation.</returns>
    /// <remarks>
    ///     <para>
    ///         The fusion process consists of three main steps:
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             <description>Resolve configuration by merging user options with template defaults</description>
    ///         </item>
    ///         <item>
    ///             <description>Collect files matching the resolved configuration</description>
    ///         </item>
    ///         <item>
    ///             <description>Build the output file with processed content</description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         Progress and status information is displayed throughout the operation.
    ///         Any exceptions are caught and displayed to the user.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
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
        try
        {
            // ===== Step 1: Resolve Configuration =====
            // Merge user options with template defaults
            var config = _configResolver.Resolve(options);

            // ===== Step 2: Collect Files =====
            // Search for files matching the configuration
            var files = _fileCollector.CollectFiles(options, config);
            _consoleUI.WriteStep($"Found {files.Count} files to process.");

            // Handle case where no files were found
            if (files.Count == 0)
            {
                _consoleUI.WriteError("No files found matching the criteria. Aborting.");
                return;
            }

            // ===== Step 3: Build Output =====
            // Process files and generate the fused output
            var result = await _outputBuilder.BuildOutputAsync(files, options, cancellationToken);

            // ===== Step 4: Display Results =====
            DisplayResults(result, options);
        }
        catch (Exception ex)
        {
            // Display any errors that occurred during processing
            _consoleUI.WriteError($"Error: {ex.Message}");
            if (ex.InnerException != null)
            {
                _consoleUI.WriteError($"  {ex.InnerException.Message}");
            }
        }
    }

    /// <summary>
    ///     Displays the fusion results using the console UI.
    /// </summary>
    /// <param name="result">The fusion result containing paths, tokens, and statistics.</param>
    /// <param name="options">The fusion options.</param>
    private void DisplayResults(FusionResult result, FuseOptions options)
    {
        // Display success message
        _consoleUI.WriteSuccess($"Fused {result.ProcessedFileCount} files in {result.Duration.TotalSeconds:F1}s");

        // Display output files
        foreach (var path in result.GeneratedPaths)
        {
            var fileInfo = new FileInfo(path);
            var sizeKB = fileInfo.Length / 1024.0;
            var friendlyPath = GetFriendlyPath(path);
            _consoleUI.WriteResult($"Output: {friendlyPath}");
        }

        // Display statistics
        if (options.ShowTokenCount)
        {
            var totalSizeKB = result.GeneratedPaths.Sum(p => new FileInfo(p).Length) / 1024.0;
            var tokensFormatted = result.TotalTokens >= 1000
                ? $"{result.TotalTokens / 1000.0:F0}k"
                : $"{result.TotalTokens}";

            _consoleUI.WriteResult($"Stats:  {totalSizeKB:F0} KB • {tokensFormatted} tokens");

            // Display top token consumers
            if (result.TopTokenFiles.Count > 0)
            {
                _consoleUI.WriteResult("\nTop Token Consumers:");
                for (int i = 0; i < result.TopTokenFiles.Count; i++)
                {
                    var file = result.TopTokenFiles[i];
                    var count = file.Count >= 1000
                        ? $"{file.Count / 1000.0:F1}k"
                        : file.Count.ToString();
                    _consoleUI.WriteResult($"{i + 1}. {file.Path} ({count})");
                }
            }
        }
    }

    /// <summary>
    ///     Converts a full file path to a user-friendly path by replacing
    ///     the user profile directory with ~.
    /// </summary>
    /// <param name="path">The full file path to convert.</param>
    /// <returns>A user-friendly path with ~ substitution.</returns>
    private static string GetFriendlyPath(string path)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(userProfile) && path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return "~" + path.Substring(userProfile.Length);
        }

        return path;
    }
}