//-----------------------------------------------------------------------
// <copyright file="DotNetCommand.cs" company="Fuse">
// Copyright (c) Fuse. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using DotMake.CommandLine;
using Fuse.Core;
using Fuse.Engine;

namespace Fuse.Cli.Commands;

/// <summary>
/// CLI command for fusing .NET projects (C#, F#, VB.NET, ASP.NET).
/// </summary>
/// <remarks>
/// <para>
/// This command is invoked via <c>fuse dotnet</c> and provides .NET-specific options:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>C# namespace and using statement removal</description>
/// </item>
/// <item>
/// <description>C# comment and region removal</description>
/// </item>
/// <item>
/// <description>Aggressive reduction (attributes, auto-props)</description>
/// </item>
/// <item>
/// <description>XML and HTML/Razor minification toggles</description>
/// </item>
/// </list>
/// <para>
/// The command uses the <see cref="ProjectTemplate.DotNet" /> template which includes
/// common .NET file extensions and excludes bin/obj directories.
/// </para>
/// </remarks>
/// <example>
/// Usage examples:
/// <code>
/// fuse dotnet --directory ./src --remove-csharp-comments
/// fuse dotnet --only-extensions .cs,.razor --exclude-test-projects
/// fuse dotnet --all
/// </code>
/// </example>
[CliCommand(Name = "dotnet", Description = "Fuse a .NET project, including C#, F#, and web files.", Parent = typeof(FuseCliCommand))]
public sealed class DotNetCommand : CommandBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DotNetCommand" /> class.
    /// </summary>
    /// <remarks>
    /// Parameterless constructor required by DotMake.CommandLine source generator.
    /// </remarks>
    public DotNetCommand() : base(null!)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotNetCommand" /> class.
    /// </summary>
    /// <param name="engine">The fusion engine instance.</param>
    public DotNetCommand(FuseEngine engine) : base(engine)
    {
    }

    /// <summary>
    /// Executes the dotnet fusion command.
    /// </summary>
    /// <param name="context">The CLI context containing cancellation token and other metadata.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RunAsync(CliContext context)
    {
        // Build the fusion options from CLI arguments
        var options = new FuseOptions
        {
            // Use .NET template for extensions and exclusions
            Template = ProjectTemplate.DotNet,

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

            // Test project options
            ExcludeTestProjects = ExcludeTestProjects,
            ExcludeUnitTestProjects = ExcludeUnitTestProjects,

            // .NET-specific options
            // If 'All' is true, it overrides the individual flags to true
            RemoveCSharpNamespaceDeclarations = All || RemoveCSharpNamespaces,
            RemoveCSharpComments = All || RemoveCSharpComments,
            RemoveCSharpRegions = All || RemoveCSharpRegions,
            RemoveCSharpUsings = All || RemoveCSharpUsings,
            AggressiveCSharpReduction = All || Aggressive, // Enable aggressive reduction if All or Aggressive is set
            MinifyXmlFiles = MinifyXmlFiles,
            MinifyHtmlAndRazor = MinifyHtmlAndRazor,
            ApplyAllOptions = All,

            // Default content transformations
            UseCondensing = true,
            TrimContent = true
        };

        // Execute the fusion operation
        await _engine.FuseAsync(options, context.CancellationToken);
    }

    #region .NET Specific Options

    /// <summary>
    /// Gets or sets a value indicating whether to remove namespace declarations from C# files.
    /// </summary>
    /// <value><c>true</c> to remove namespaces; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    [CliOption(Description = "Remove namespace declarations from C# files.")]
    public bool RemoveCSharpNamespaces { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to remove comments from C# files.
    /// </summary>
    /// <value><c>true</c> to remove comments; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    [CliOption(Description = "Remove comments from C# files.")]
    public bool RemoveCSharpComments { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to remove region directives from C# files.
    /// </summary>
    /// <value><c>true</c> to remove #region/#endregion; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    [CliOption(Description = "Remove #region directives from C# files.")]
    public bool RemoveCSharpRegions { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to remove using statements from C# files.
    /// </summary>
    /// <value><c>true</c> to remove using directives; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    [CliOption(Description = "Remove all using statements from C# files.")]
    public bool RemoveCSharpUsings { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to apply aggressive reduction optimizations.
    /// </summary>
    /// <value><c>true</c> to enable aggressive reduction; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    [CliOption(Description = "Apply aggressive reduction (remove attributes, redundant keywords, compress properties).")]
    public bool Aggressive { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to minify XML-based files.
    /// </summary>
    /// <value><c>true</c> to minify XML files; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    [CliOption(Description = "Minify XML files (.csproj, .xml, etc.).")]
    public bool MinifyXmlFiles { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to minify HTML and Razor files.
    /// </summary>
    /// <value><c>true</c> to minify HTML/Razor; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    [CliOption(Description = "Minify HTML and Razor files.")]
    public bool MinifyHtmlAndRazor { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to exclude only unit test project directories.
    /// </summary>
    /// <value><c>true</c> to exclude unit test projects only; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    /// <remarks>
    /// This excludes projects like UnitTests, Tests, TestProject, but keeps
    /// IntegrationTests, EndToEndTests, E2ETests, and Benchmarks.
    /// </remarks>
    [CliOption(Description = "Exclude only unit test project directories (keeps integration tests and benchmarks).")]
    public bool ExcludeUnitTestProjects { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to apply all available optimizations.
    /// </summary>
    /// <value><c>true</c> to enable all optimizations; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    [CliOption(Description = "Apply all optimizations (remove namespaces, comments, regions, usings, aggressive reduction).")]
    public bool All { get; set; } = false;

    #endregion
}