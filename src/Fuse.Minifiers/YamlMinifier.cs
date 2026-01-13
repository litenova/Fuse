// -----------------------------------------------------------------------
// <copyright file="YamlMinifier.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace Fuse.Minifiers;

/// <summary>
///     Provides minification functionality for YAML (YAML Ain't Markup Language) files.
/// </summary>
/// <remarks>
///     <para>
///         This minifier optimizes YAML content while being careful to preserve its
///         whitespace-sensitive structure:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>Removal of comment lines (# ...)</description>
///         </item>
///         <item>
///             <description>Condensation of multiple blank lines</description>
///         </item>
///         <item>
///             <description>Removal of trailing whitespace</description>
///         </item>
///         <item>
///             <description>Preservation of indentation (critical for YAML structure)</description>
///         </item>
///     </list>
///     <para>
///         Warning: YAML is whitespace-sensitive. This minifier is conservative and
///         preserves all indentation to avoid breaking document structure.
///     </para>
/// </remarks>
public static class YamlMinifier
{
    /// <summary>
    ///     Minifies YAML content by removing comments and unnecessary blank lines.
    /// </summary>
    /// <param name="content">The YAML content to minify.</param>
    /// <returns>The minified YAML content.</returns>
    /// <example>
    ///     <code>
    /// string yaml = @"
    /// # Application configuration
    /// name: myapp
    /// 
    /// # Server settings
    /// server:
    ///   port: 8080";
    /// string minified = YamlMinifier.Minify(yaml);
    /// // Result: "name: myapp\nserver:\n  port: 8080"
    /// </code>
    /// </example>
    public static string Minify(string content)
    {
        // Step 1: Remove full-line comments (lines starting with #)
        // Preserves inline comments for now as they may be significant
        content = Regex.Replace(content, @"^\s*#.*$", "", RegexOptions.Multiline);

        // Step 2: Remove trailing whitespace from lines
        // This is safe in YAML as trailing whitespace is never significant
        content = Regex.Replace(content, @"[ \t]+$", "", RegexOptions.Multiline);

        // Step 3: Condense multiple blank lines to single blank line
        // YAML doesn't require blank lines, but we keep one for readability
        content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");

        // Step 4: Remove leading blank lines
        content = Regex.Replace(content, @"^[\r\n]+", "");

        // Step 5: Remove trailing blank lines
        content = Regex.Replace(content, @"[\r\n]+$", "");

        return content;
    }
}