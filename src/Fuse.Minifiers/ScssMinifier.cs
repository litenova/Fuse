// -----------------------------------------------------------------------
// <copyright file="ScssMinifier.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace Fuse.Minifiers;

/// <summary>
/// Provides minification functionality for SCSS (Sassy CSS) files.
/// </summary>
/// <remarks>
/// <para>
/// This minifier handles SCSS-specific syntax in addition to standard CSS:
/// </para>
/// <list type="bullet">
///     <item><description>Removal of single-line comments (// ...)</description></item>
///     <item><description>Removal of multi-line comments (/* ... */)</description></item>
///     <item><description>Preservation of important SCSS constructs like variables and mixins</description></item>
///     <item><description>Whitespace optimization around SCSS operators</description></item>
/// </list>
/// </remarks>
public static class ScssMinifier
{
    /// <summary>
    /// Minifies SCSS content by removing comments and unnecessary whitespace.
    /// </summary>
    /// <param name="content">The SCSS content to minify.</param>
    /// <returns>The minified SCSS content.</returns>
    /// <example>
    /// <code>
    /// string scss = @"
    /// // Primary color variable
    /// $primary: #007bff;
    /// 
    /// .button {
    ///     background: $primary;
    /// }";
    /// string minified = ScssMinifier.Minify(scss);
    /// // Result: "$primary:#007bff;.button{background:$primary;}"
    /// </code>
    /// </example>
    public static string Minify(string content)
    {
        // Step 1: Remove single-line comments (SCSS-specific)
        // Pattern: // to end of line
        // Note: Be careful not to affect URLs (http://, https://)
        content = Regex.Replace(content, @"(?<!:)//(?!/)[^\r\n]*", "");

        // Step 2: Remove multi-line comments
        // Pattern: /* anything */
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);

        // Step 3: Remove newlines and carriage returns
        content = Regex.Replace(content, @"[\r\n]+", "");

        // Step 4: Remove whitespace around opening braces
        content = Regex.Replace(content, @"\s*\{\s*", "{");

        // Step 5: Remove whitespace around closing braces
        content = Regex.Replace(content, @"\s*\}\s*", "}");

        // Step 6: Remove whitespace around colons
        content = Regex.Replace(content, @"\s*:\s*", ":");

        // Step 7: Remove whitespace around semicolons
        content = Regex.Replace(content, @"\s*;\s*", ";");

        // Step 8: Remove whitespace around commas
        content = Regex.Replace(content, @"\s*,\s*", ",");

        // Step 9: Condense multiple spaces to single space
        content = Regex.Replace(content, @" {2,}", " ");

        // Step 10: Remove leading/trailing whitespace
        content = content.Trim();

        return content;
    }
}
