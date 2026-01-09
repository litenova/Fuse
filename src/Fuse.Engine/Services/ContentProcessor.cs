// -----------------------------------------------------------------------
// <copyright file="ContentProcessor.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.RegularExpressions;
using Fuse.Core;
using Fuse.Engine.FileSystem;
using Fuse.Minifiers;

namespace Fuse.Engine.Services;

/// <summary>
/// Processes file content by applying transformations and minification.
/// </summary>
/// <remarks>
/// <para>
/// This service handles the content transformation pipeline:
/// </para>
/// <list type="number">
///     <item><description>Read raw content from file</description></item>
///     <item><description>Apply whitespace trimming if enabled</description></item>
///     <item><description>Apply line condensation if enabled</description></item>
///     <item><description>Apply file-type-specific minification</description></item>
/// </list>
/// <para>
/// Minification is applied based on file extension and respects the
/// relevant options in <see cref="FuseOptions"/>.
/// </para>
/// </remarks>
public sealed class ContentProcessor : IContentProcessor
{
    /// <summary>
    /// The file system abstraction for reading file content.
    /// </summary>
    private readonly PhysicalFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentProcessor"/> class.
    /// </summary>
    /// <param name="fileSystem">The file system implementation for reading files.</param>
    public ContentProcessor(PhysicalFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    /// <summary>
    /// Processes file content by reading, transforming, and minifying it.
    /// </summary>
    public async Task<string> ProcessContentAsync(FileProcessingInfo fileInfo, FuseOptions options, CancellationToken cancellationToken)
    {
        // Step 1: Read the raw file content
        var content = await _fileSystem.ReadAllTextAsync(fileInfo.FullPath, cancellationToken);

        // Get the file extension for minification decisions
        var fileExtension = fileInfo.Info.Extension.ToLowerInvariant();

        // Step 2: Apply whitespace trimming if enabled
        // This removes leading and trailing whitespace from each line
        if (options.TrimContent)
        {
            content = Regex.Replace(content, @"^[\s\t]+|[\s\t]+$", "", RegexOptions.Multiline);
        }

        // Step 3: Apply line condensation if enabled
        // This removes empty lines (lines containing only whitespace)
        if (options.UseCondensing)
        {
            content = Regex.Replace(content, @"^\s*$\r?\n", string.Empty, RegexOptions.Multiline);
        }

        // Step 4: Apply file-type-specific minification
        return ApplyMinification(content, fileExtension, options);
    }

    /// <summary>
    /// Applies the appropriate minifier based on file extension.
    /// </summary>
    /// <param name="content">The content to minify.</param>
    /// <param name="fileExtension">The file extension (including leading dot).</param>
    /// <param name="options">The fusion options controlling minification behavior.</param>
    /// <returns>The minified content.</returns>
    /// <remarks>
    /// <para>
    /// Supported file types and their minifiers:
    /// </para>
    /// <list type="bullet">
    ///     <item><description>.cs - <see cref="CSharpMinifier"/></description></item>
    ///     <item><description>.razor, .cshtml - <see cref="RazorMinifier"/> (when MinifyHtmlAndRazor is true)</description></item>
    ///     <item><description>.html, .htm - <see cref="HtmlMinifier"/> (when MinifyHtmlAndRazor is true)</description></item>
    ///     <item><description>.css - <see cref="CssMinifier"/></description></item>
    ///     <item><description>.scss - <see cref="ScssMinifier"/></description></item>
    ///     <item><description>.js - <see cref="JavaScriptMinifier"/></description></item>
    ///     <item><description>.json - <see cref="JsonMinifier"/></description></item>
    ///     <item><description>.xml, .targets, .props, .csproj - <see cref="XmlMinifier"/> (when MinifyXmlFiles is true)</description></item>
    ///     <item><description>.md - <see cref="MarkdownMinifier"/></description></item>
    ///     <item><description>.yml, .yaml - <see cref="YamlMinifier"/></description></item>
    /// </list>
    /// </remarks>
    private static string ApplyMinification(string content, string fileExtension, FuseOptions options)
    {
        switch (fileExtension)
        {
            // C# files - apply C# minifier with options
            case ".cs":
                return CSharpMinifier.Minify(content, options);

            // Razor and Blazor files - minify if option is enabled
            case ".razor":
            case ".cshtml" when options.MinifyHtmlAndRazor:
                return RazorMinifier.Minify(content);

            // HTML files - minify if option is enabled
            case ".html":
            case ".htm" when options.MinifyHtmlAndRazor:
                return HtmlMinifier.Minify(content);

            // CSS files - always minify
            case ".css":
                return CssMinifier.Minify(content);

            // SCSS files - always minify
            case ".scss":
                return ScssMinifier.Minify(content);

            // JavaScript files - always minify
            case ".js":
                return JavaScriptMinifier.Minify(content);

            // JSON files - always minify
            case ".json":
                return JsonMinifier.Minify(content);

            // XML and related files - minify if option is enabled
            case ".xml":
            case ".targets":
            case ".props":
            case ".csproj" when options.MinifyXmlFiles:
                return XmlMinifier.Minify(content);

            // Markdown files - always minify (removes excessive whitespace)
            case ".md":
                return MarkdownMinifier.Minify(content);

            // YAML files - always minify (removes comments and excessive whitespace)
            case ".yml":
            case ".yaml":
                return YamlMinifier.Minify(content);

            // Unrecognized file types - return content unchanged
            default:
                return content;
        }
    }
}
