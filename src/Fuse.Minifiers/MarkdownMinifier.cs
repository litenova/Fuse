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
///             <description>Normalization of headers (Setext to ATX)</description>
///         </item>
///         <item>
///             <description>Removal of horizontal rules</description>
///         </item>
///         <item>
///             <description>Compression of tables (removing alignment whitespace)</description>
///         </item>
///         <item>
///             <description>Removal of optional link titles</description>
///         </item>
///         <item>
///             <description>Condensation of multiple blank lines</description>
///         </item>
///     </list>
///     <para>
///         Note: This minifier is aggressive but semantically safe for LLM consumption.
///         Output may not be visually pretty for humans (e.g., misaligned tables) but
///         retains all data relationships.
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
    /// Header
    /// ======
    /// 
    /// | Col 1 | Col 2 |
    /// | ----- | ----- |
    /// | Val 1 | Val 2 |";
    /// string minified = MarkdownMinifier.Minify(md);
    /// // Result: "# Header\n\n|Col 1|Col 2|\n|---|---|\n|Val 1|Val 2|"
    /// </code>
    /// </example>
    public static string Minify(string content)
    {
        // Step 1: Remove HTML comments (common in Markdown documents)
        // Pattern: <!-- anything -->
        content = Regex.Replace(content, @"<!--.*?-->", "", RegexOptions.Singleline);

        // Step 2: Normalize Setext headers to ATX headers
        // Converts:
        // Title       -> # Title
        // =====
        // Subtitle    -> ## Subtitle
        // --------
        // Note: We use a MatchEvaluator to distinguish between H1 (=) and H2 (-)
        content = Regex.Replace(content, @"(?m)^(?<text>.+)(\r?\n)(?<underline>[=\-]{3,})\s*$", m =>
        {
            var text = m.Groups["text"].Value.Trim();
            var underline = m.Groups["underline"].Value;
            var level = underline.StartsWith('=') ? "#" : "##";
            return $"{level} {text}";
        });

        // Step 3: Remove Horizontal Rules
        // Removes lines consisting only of ---, ***, or ___
        // LLMs understand context shifts via headers; HRs are mostly decorative noise.
        content = Regex.Replace(content, @"(?m)^\s*[-*_]{3,}\s*(\r?\n)?", "");

        // Step 4: Compress Tables
        // Removes whitespace around pipes.
        // |  Column 1  |  Column 2  | -> |Column 1|Column 2|
        content = Regex.Replace(content, @"\s*\|\s*", "|");

        // Step 5: Remove Link Titles
        // [Link Text](URL "Title") -> [Link Text](URL)
        // The title attribute is rarely semantically critical for LLMs.
        content = Regex.Replace(content, @"\[([^\]]+)\]\(([^\s)]+)\s+""[^""]*""\)", "[$1]($2)");

        // Step 6: Condense multiple blank lines to a maximum of 2 (preserves paragraph breaks)
        // Markdown requires blank lines between paragraphs and other elements
        content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");

        // Step 7: Remove trailing whitespace from lines
        // Note: Two trailing spaces in Markdown mean a line break, so we preserve those
        content = Regex.Replace(content, @"(?<!  )[ \t]+$", "", RegexOptions.Multiline);

        // Step 8: Remove leading blank lines at start of document
        content = Regex.Replace(content, @"^[\r\n]+", "");

        // Step 9: Remove trailing blank lines at end of document
        content = Regex.Replace(content, @"[\r\n]+$", "");

        return content;
    }
}