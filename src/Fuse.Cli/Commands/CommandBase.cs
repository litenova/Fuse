//-----------------------------------------------------------------------
// <copyright file="CommandBase.cs" company="Fuse">
// Copyright (c) Fuse. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using DotMake.CommandLine;
using Fuse.Engine;
using Spectre.Console;

namespace Fuse.Cli.Commands;

/// <summary>
/// Abstract base class for all Fuse CLI commands.
/// </summary>
/// <remarks>
/// <para>
/// This class provides common CLI options that are shared across all commands:
/// </para>
/// <list type="bullet">
/// <item><description>Directory and output path settings</description></item>
/// <item><description>File filtering options (size, binary, recursive)</description></item>
/// <item><description>Content options (metadata, gitignore, tokens)</description></item>
/// <item><description>Test project exclusion</description></item>
/// </list>
/// <para>
/// Derived command classes inherit these options and can add their own
/// template-specific options.
/// </para>
/// </remarks>
public abstract class CommandBase
{
    /// <summary>
    /// The fusion engine for executing the fusion operation.
    /// </summary>
    protected readonly FuseEngine _engine;

    /// <summary>
    /// The console interface for output display.
    /// </summary>
    protected readonly IAnsiConsole _console;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandBase"/> class.
    /// </summary>
    /// <param name="engine">The fusion engine instance.</param>
    /// <param name="console">The console for output.</param>
    protected CommandBase(FuseEngine engine, IAnsiConsole console)
    {
        _engine = engine;
        _console = console;
    }

    #region Directory Options

    /// <summary>
    /// Gets or sets the source directory to process.
    /// </summary>
    /// <value>The path to the directory to scan for files. Defaults to the current working directory.</value>
    [CliOption(Description = "Path to the directory to process.")]
    public string Directory { get; set; } = System.IO.Directory.GetCurrentDirectory();

    /// <summary>
    /// Gets or sets the output directory for the fused file.
    /// </summary>
    /// <value>The path where the output file will be written. Defaults to the user's Documents folder.</value>
    [CliOption(Description = "Path to the output directory.")]
    public string Output { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    #endregion

    #region Output Options

    /// <summary>
    /// Gets or sets the custom output file name.
    /// </summary>
    /// <value>The output file name without extension, or null for auto-generated name.</value>
    /// <remarks>
    /// <para>
    /// This option is optional. If not provided, a filename will be generated based on the 
    /// source directory name and the current timestamp.
    /// </para>
    /// <para>
    /// If a name is provided without a file extension, .txt will be appended automatically
    /// by the output builder.
    /// </para>
    /// </remarks>
    [CliOption(Name = "name", Required = false, Description = "Name of the output file (without extension).")]
    public string? OutputFileName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to overwrite existing output files.
    /// </summary>
    /// <value><c>true</c> to overwrite existing files; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    [CliOption(Description = "Overwrite the output file if it exists.")]
    public bool Overwrite { get; set; } = true;

    #endregion

    #region Search Options

    /// <summary>
    /// Gets or sets a value indicating whether to search subdirectories recursively.
    /// </summary>
    /// <value><c>true</c> to search recursively; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    [CliOption(Description = "Search recursively through subdirectories.")]
    public bool Recursive { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum file size in kilobytes to process.
    /// </summary>
    /// <value>The maximum file size in KB, or 0 for unlimited. Defaults to 0.</value>
    [CliOption(Description = "Maximum file size in KB to process (0 for unlimited).")]
    public int MaxFileSize { get; set; } = 0;

    /// <summary>
    /// Gets or sets a value indicating whether to skip binary files.
    /// </summary>
    /// <value><c>true</c> to skip binary files; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    [CliOption(Description = "Ignore binary files.")]
    public bool IgnoreBinary { get; set; } = true;

    #endregion

    #region Content Options

    /// <summary>
    /// Gets or sets a value indicating whether to include file metadata in the output.
    /// </summary>
    /// <value><c>true</c> to include metadata; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    [CliOption(Description = "Include file metadata (size, dates) in the output.")]
    public bool IncludeMetadata { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to respect .gitignore rules.
    /// </summary>
    /// <value><c>true</c> to respect .gitignore; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    [CliOption(Description = "Respect rules from .gitignore files found in the directory tree.")]
    public bool RespectGitIgnore { get; set; } = true;

    #endregion

    #region Token Options

    /// <summary>
    /// Gets or sets the maximum token count limit.
    /// </summary>
    /// <value>The maximum number of tokens, or null for unlimited.</value>
    [CliOption(Description = "Stops processing when token count is reached.")]
    public int? MaxTokens { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to display token count in the output.
    /// </summary>
    /// <value><c>true</c> to show token count; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    [CliOption(Description = "Displays the final estimated token count upon completion.")]
    public bool ShowTokenCount { get; set; } = true;

    #endregion

    #region Test Project Options

    /// <summary>
    /// Gets or sets a value indicating whether to exclude test project directories.
    /// </summary>
    /// <value><c>true</c> to exclude test projects; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    [CliOption(Description = "Exclude common test project directories.")]
    public bool ExcludeTestProjects { get; set; } = false;

    #endregion

    #region Extension Override Options

    /// <summary>
    /// Gets or sets the file extensions to process exclusively, ignoring all template defaults.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This option is optional. When specified, it overrides any template-based extension settings.
    /// </para>
    /// <para>
    /// Example: <c>--only-extensions .cs,.razor</c> will only process C# and Razor files.
    /// </para>
    /// </remarks>
    [CliOption(Required = false, Description = "Fuse ONLY the specified comma-separated file extensions, ignoring all template defaults.")]
    public string[]? OnlyExtensions { get; set; }

    #endregion
}