// -----------------------------------------------------------------------
// <copyright file="CssMinifier.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace Fuse.Minifiers;

/// <summary>
///     Provides minification functionality for CSS (Cascading Style Sheets) files.
/// </summary>
/// <remarks>
///     <para>
///         This minifier performs the following optimizations:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>Removal of CSS comments (/* ... */)</description>
///         </item>
///         <item>
///             <description>Removal of unnecessary whitespace around selectors and properties</description>
///         </item>
///         <item>
///             <description>Condensation of multiple spaces</description>
///         </item>
///         <item>
///             <description>Removal of whitespace around special characters ({, }, :, ;)</description>
///         </item>
///     </list>
/// </remarks>
public static class CssMinifier
{
    /// <summary>
    ///     Minifies CSS content by removing comments and unnecessary whitespace.
    /// </summary>
    /// <param name="content">The CSS content to minify.</param>
    /// <returns>The minified CSS content.</returns>
    /// <example>
    ///     <code>
    /// string css = @"
    /// /* Main container styles */
    /// .container {
    ///     width: 100%;
    ///     padding: 20px;
    /// }";
    /// string minified = CssMinifier.Minify(css);
    /// // Result: ".container{width:100%;padding:20px;}"
    /// </code>
    /// </example>
    public static string Minify(string content)
    {
        // Step 1: Remove CSS comments
        // Pattern: /* anything */
        // Uses Singleline mode so . matches newlines
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);

        // Step 2: Remove newlines and carriage returns
        content = Regex.Replace(content, @"[\r\n]+", "");

        // Step 3: Remove whitespace around opening braces
        // Example: ".class {" becomes ".class{"
        content = Regex.Replace(content, @"\s*\{\s*", "{");

        // Step 4: Remove whitespace around closing braces
        // Example: "} .next" becomes "}.next"
        content = Regex.Replace(content, @"\s*\}\s*", "}");

        // Step 5: Remove whitespace around colons in property declarations
        // Example: "width : 100%" becomes "width:100%"
        content = Regex.Replace(content, @"\s*:\s*", ":");

        // Step 6: Remove whitespace around semicolons
        // Example: "width: 100% ;" becomes "width:100%;"
        content = Regex.Replace(content, @"\s*;\s*", ";");

        // Step 7: Remove whitespace around commas in selectors
        // Example: ".a , .b" becomes ".a,.b"
        content = Regex.Replace(content, @"\s*,\s*", ",");

        // Step 8: Condense multiple spaces to single space
        content = Regex.Replace(content, @" {2,}", " ");

        // Step 9: Remove leading/trailing whitespace
        content = content.Trim();

        return content;
    }
}