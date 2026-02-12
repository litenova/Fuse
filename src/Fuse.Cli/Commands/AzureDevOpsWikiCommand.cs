// -----------------------------------------------------------------------
// <copyright file="AzureDevOpsWikiCommand.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using DotMake.CommandLine;
using Fuse.Core;
using Fuse.Engine;

namespace Fuse.Cli.Commands;

/// <summary>
///     CLI command for fusing Azure DevOps wiki repositories.
/// </summary>
/// <remarks>
///     <para>
///         This command is invoked via <c>fuse wiki</c> and is optimized for Azure DevOps wikis.
///         It uses the <see cref="ProjectTemplate.AzureDevOpsWiki" /> template which:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>Includes only Markdown (.md) files</description>
///         </item>
///         <item>
///             <description>Excludes .git and .attachments directories</description>
///         </item>
///     </list>
///     <para>
///         This is useful for consolidating wiki documentation into a single file
///         for analysis, backup, or feeding to LLMs.
///     </para>
/// </remarks>
/// <example>
///     Usage examples:
///     <code>
/// fuse wiki --directory ./wiki-repo
/// fuse wiki --directory ./docs --include-metadata
/// </code>
/// </example>
[CliCommand(Name = "wiki", Description = "Fuse an Azure DevOps wiki repository (includes only .md files).", Parent = typeof(FuseCliCommand))]
public sealed class AzureDevOpsWikiCommand : CommandBase
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="AzureDevOpsWikiCommand" /> class.
    /// </summary>
    /// <remarks>
    ///     Parameterless constructor required by DotMake.CommandLine source generator.
    /// </remarks>
    public AzureDevOpsWikiCommand() : base(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="AzureDevOpsWikiCommand" /> class.
    /// </summary>
    /// <param name="engine">The fusion engine instance.</param>
    public AzureDevOpsWikiCommand(FuseEngine engine) : base(engine)
    {
    }

    /// <summary>
    ///     Executes the wiki fusion command.
    /// </summary>
    /// <param name="context">The CLI context containing cancellation token and other metadata.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RunAsync(CliContext context)
    {
        // Build the fusion options from CLI arguments
        var options = new FuseOptions
        {
            // Use Azure DevOps Wiki template (Markdown files only)
            Template = ProjectTemplate.AzureDevOpsWiki,

            // Directory options
            SourceDirectory = Directory,
            OutputDirectory = Output,

            // Global option from base command
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

            // Default content transformations
            UseCondensing = true,
            TrimContent = true
        };

        // Execute the fusion operation
        await _engine.FuseAsync(options, context.CancellationToken);
    }
}