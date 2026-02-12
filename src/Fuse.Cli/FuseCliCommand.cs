// -----------------------------------------------------------------------
// <copyright file="FuseCliCommand.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using DotMake.CommandLine;
using Fuse.Cli.Commands;
using Fuse.Core;
using Fuse.Engine;

namespace Fuse.Cli;

/// <summary>
///     The root CLI command for the Fuse application.
/// </summary>
/// <remarks>
///     <para>
///         This command serves as both:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>The root command container for subcommands (dotnet, wiki, etc.)</description>
///         </item>
///         <item>
///             <description>A standalone generic fusion command when invoked without subcommands</description>
///         </item>
///     </list>
///     <para>
///         Global options defined here are available to all subcommands when they inherit from
///         the shared <see cref="CommandBase" /> class.
///     </para>
/// </remarks>
/// <example>
///     Usage examples:
///     <code>
/// fuse --directory ./src                           # Generic fusion
/// fuse dotnet --directory ./src                    # .NET-specific fusion
/// fuse wiki --directory ./docs                     # Wiki fusion
/// fuse dotnet --only-extensions .cs                # Override with only .cs files
/// </code>
/// </example>
[CliCommand(Description = "A flexible file combining tool for developers.")]
public class FuseCliCommand : CommandBase
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseCliCommand" /> class.
    /// </summary>
    /// <remarks>
    ///     Parameterless constructor required by DotMake.CommandLine source generator.
    /// </remarks>
    public FuseCliCommand() : base(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseCliCommand" /> class.
    /// </summary>
    /// <param name="engine">The fusion engine instance.</param>
    public FuseCliCommand(FuseEngine engine) : base(engine)
    {
    }

    /// <summary>
    ///     Executes the root fuse command for generic file fusion.
    /// </summary>
    /// <param name="context">The CLI context containing cancellation token and other metadata.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    ///     When invoked without a subcommand, this performs a generic fusion operation
    ///     without any template-specific settings. All files matching the criteria
    ///     will be included unless filtered by <c>--only-extensions</c>.
    /// </remarks>
    public async Task RunAsync(CliContext context)
    {
        // Build fusion options for generic (no template) fusion
        var options = new FuseOptions
        {
            // Explicitly no template for the root command
            Template = null,

            // Directory options
            SourceDirectory = Directory,
            OutputDirectory = Output,

            // Global extension override
            OnlyExtensions = OnlyExtensions,

            // Output options
            OutputFileName = OutputFileName,
            Overwrite = Overwrite,

            // Search options
            Recursive = Recursive,
            MaxFileSizeKB = MaxFileSize,
            IgnoreBinaryFiles = IgnoreBinary,

            // Content options
            IncludeMetadata = IncludeMetadata,
            RespectGitIgnore = RespectGitIgnore,

            // Token options
            MaxTokens = MaxTokens,
            SplitTokens = SplitTokens,
            ShowTokenCount = ShowTokenCount,

            // Test project options
            ExcludeTestProjects = ExcludeTestProjects,

            // Default content transformations (sensible defaults)
            UseCondensing = true,
            TrimContent = true
        };

        // Execute the fusion operation
        await _engine.FuseAsync(options, context.CancellationToken);
    }
}