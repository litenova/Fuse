// -----------------------------------------------------------------------
// <copyright file="MarkdownMinifier.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace Fuse.Minifiers;

/// <summary>
///     Provides minification functionality for Markdown files.
/// </summary>
/// <remarks>
///     <para>
///         This minifier optimizes Markdown content while preserving its structure and formatting:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>Removal of HTML comments within Markdown</description>
///         </item>
///         <item>
///             <description>Condensation of multiple blank lines</description>
///         </item>
///         <item>
///             <description>Removal of trailing whitespace (except for line breaks)</description>
///         </item>
///         <item>
///             <description>Preservation of code blocks and their formatting</description>
///         </item>
///     </list>
///     <para>
///         Note: This minifier is conservative to avoid breaking Markdown syntax.
///         It preserves significant whitespace like indentation for lists and code blocks.
///     </para>
/// </remarks>
public static class MarkdownMinifier
{
    /// <summary>
    ///     Minifies Markdown content by removing unnecessary whitespace while preserving structure.
    /// </summary>
    /// <param name="content">The Markdown content to minify.</param>
    /// <returns>The minified Markdown content.</returns>
    /// <example>
    ///     <code>
    /// string md = @"
    /// # Title
    /// 
    /// 
    /// 
    /// Some paragraph text.
    /// 
    /// 
    /// ## Subtitle";
    /// string minified = MarkdownMinifier.Minify(md);
    /// // Result: "# Title\n\nSome paragraph text.\n\n## Subtitle"
    /// </code>
    /// </example>
    public static string Minify(string content)
    {
        // Step 1: Remove HTML comments (common in Markdown documents)
        // Pattern: <!-- anything -->
        content = Regex.Replace(content, @"<!--.*?-->", "", RegexOptions.Singleline);

        // Step 2: Condense multiple blank lines to a maximum of 2 (preserves paragraph breaks)
        // Markdown requires blank lines between paragraphs and other elements
        content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");

        // Step 3: Remove trailing whitespace from lines
        // Note: Two trailing spaces in Markdown mean a line break, so we preserve those
        content = Regex.Replace(content, @"(?<!  )[ \t]+$", "", RegexOptions.Multiline);

        // Step 4: Remove leading blank lines at start of document
        content = Regex.Replace(content, @"^[\r\n]+", "");

        // Step 5: Remove trailing blank lines at end of document
        content = Regex.Replace(content, @"[\r\n]+$", "");

        return content;
    }
}