// -----------------------------------------------------------------------
// <copyright file="FuseOptions.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Fuse.Core;

/// <summary>
/// Represents the configuration options for a file fusion operation.
/// </summary>
/// <remarks>
/// <para>
/// This record encapsulates all settings that control how files are collected,
/// processed, and combined into a single output file. It supports:
/// </para>
/// <list type="bullet">
///     <item><description>Directory and file filtering options</description></item>
///     <item><description>Content transformation and minification settings</description></item>
///     <item><description>Output configuration and token limiting</description></item>
///     <item><description>Project template-based presets</description></item>
/// </list>
/// </remarks>
public sealed record FuseOptions
{
    /// <summary>
    /// Gets the source directory to scan for files.
    /// </summary>
    /// <value>The absolute path to the source directory. Defaults to the current working directory.</value>
    public string SourceDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// Gets the directory where the fused output file will be written.
    /// </summary>
    /// <value>The absolute path to the output directory. Defaults to the user's Documents folder.</value>
    public string OutputDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>
    /// Gets the project template to use for default file extensions and exclusions.
    /// </summary>
    /// <value>A <see cref="ProjectTemplate"/> value, or <c>null</c> for generic processing.</value>
    public ProjectTemplate? Template { get; init; }

    /// <summary>
    /// Gets the file extensions to include in addition to template defaults.
    /// </summary>
    /// <value>An array of file extensions (e.g., ".cs", ".md"), or <c>null</c> if not specified.</value>
    public string[]? IncludeExtensions { get; init; }

    /// <summary>
    /// Gets the file extensions to exclude from template defaults.
    /// </summary>
    /// <value>An array of file extensions to exclude, or <c>null</c> if not specified.</value>
    public string[]? ExcludeExtensions { get; init; }

    /// <summary>
    /// Gets the file extensions that should be processed exclusively, ignoring all template defaults.
    /// </summary>
    /// <value>An array of file extensions, or <c>null</c> to use template/include settings.</value>
    /// <remarks>
    /// When this property is set, it overrides both <see cref="IncludeExtensions"/> 
    /// and any template-based extension settings.
    /// </remarks>
    public string[]? OnlyExtensions { get; init; }

    /// <summary>
    /// Gets the directory names to exclude from scanning.
    /// </summary>
    /// <value>An array of directory names to exclude, or <c>null</c> if not specified.</value>
    public string[]? ExcludeDirectories { get; init; }

    /// <summary>
    /// Gets the custom output file name.
    /// </summary>
    /// <value>The output file name (without path), or <c>null</c> for auto-generated name.</value>
    public string? OutputFileName { get; init; }

    /// <summary>
    /// Gets a value indicating whether to overwrite existing output files.
    /// </summary>
    /// <value><c>true</c> to overwrite existing files; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to search subdirectories recursively.
    /// </summary>
    /// <value><c>true</c> for recursive search; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    public bool Recursive { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to trim leading and trailing whitespace from lines.
    /// </summary>
    /// <value><c>true</c> to trim whitespace; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    public bool TrimContent { get; init; } = true;

    /// <summary>
    /// Gets the maximum file size in kilobytes to process.
    /// </summary>
    /// <value>The maximum file size in KB, or <c>0</c> for unlimited. Defaults to <c>0</c>.</value>
    public int MaxFileSizeKB { get; init; } = 0;

    /// <summary>
    /// Gets a value indicating whether to skip binary files.
    /// </summary>
    /// <value><c>true</c> to ignore binary files; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    public bool IgnoreBinaryFiles { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to include file metadata in the output.
    /// </summary>
    /// <value><c>true</c> to include metadata (size, dates); otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    public bool IncludeMetadata { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether to condense multiple empty lines.
    /// </summary>
    /// <value><c>true</c> to remove consecutive empty lines; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    public bool UseCondensing { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to remove C# namespace declarations.
    /// </summary>
    /// <value><c>true</c> to remove namespace declarations; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    public bool RemoveCSharpNamespaceDeclarations { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether to remove C# comments.
    /// </summary>
    /// <value><c>true</c> to remove comments; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    public bool RemoveCSharpComments { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether to remove C# region directives.
    /// </summary>
    /// <value><c>true</c> to remove #region/#endregion; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    public bool RemoveCSharpRegions { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether to remove C# using statements.
    /// </summary>
    /// <value><c>true</c> to remove using directives; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    public bool RemoveCSharpUsings { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether to minify XML-based files.
    /// </summary>
    /// <value><c>true</c> to minify .csproj, .xml, etc.; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    public bool MinifyXmlFiles { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to minify HTML and Razor files.
    /// </summary>
    /// <value><c>true</c> to minify .html, .cshtml, .razor; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    public bool MinifyHtmlAndRazor { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to apply aggressive C# reduction.
    /// </summary>
    /// <value><c>true</c> for aggressive reduction; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    public bool AggressiveCSharpReduction { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether to apply all available optimization options.
    /// </summary>
    /// <value><c>true</c> to enable all options; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    public bool ApplyAllOptions { get; init; }

    /// <summary>
    /// Gets a value indicating whether to exclude test project directories.
    /// </summary>
    /// <value><c>true</c> to exclude test projects; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    public bool ExcludeTestProjects { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether to respect .gitignore file rules.
    /// </summary>
    /// <value><c>true</c> to honor .gitignore; otherwise, <c>false</c>. Defaults to <c>true</c>.</value>
    public bool RespectGitIgnore { get; init; } = true;

    /// <summary>
    /// Gets the maximum number of tokens allowed in the output.
    /// </summary>
    /// <value>The token limit, or <c>null</c> for unlimited. Defaults to <c>null</c>.</value>
    /// <remarks>
    /// When set, processing will stop once this token count is reached.
    /// Useful for preparing content for LLMs with context length limits.
    /// </remarks>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Gets a value indicating whether to display token count in the output summary.
    /// </summary>
    /// <value><c>true</c> to show token count; otherwise, <c>false</c>. Defaults to <c>false</c>.</value>
    public bool ShowTokenCount { get; init; }
}