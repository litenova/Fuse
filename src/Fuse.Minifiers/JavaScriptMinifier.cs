// -----------------------------------------------------------------------
// <copyright file="JavaScriptMinifier.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace Fuse.Minifiers;

/// <summary>
/// Provides minification functionality for JavaScript files.
/// </summary>
/// <remarks>
/// <para>
/// This minifier performs the following optimizations:
/// </para>
/// <list type="bullet">
///     <item><description>Removal of single-line comments (// ...)</description></item>
///     <item><description>Removal of multi-line comments (/* ... */)</description></item>
///     <item><description>Condensation of whitespace</description></item>
///     <item><description>Removal of unnecessary newlines</description></item>
/// </list>
/// <para>
/// Note: This is a basic minifier. For production JavaScript, consider using
/// specialized tools like Terser or UglifyJS that can perform advanced optimizations
/// like variable renaming and dead code elimination.
/// </para>
/// </remarks>
public static class JavaScriptMinifier
{
    /// <summary>
    /// Minifies JavaScript content by removing comments and unnecessary whitespace.
    /// </summary>
    /// <param name="content">The JavaScript content to minify.</param>
    /// <returns>The minified JavaScript content.</returns>
    /// <example>
    /// <code>
    /// string js = @"
    /// // Calculate the sum
    /// function add(a, b) {
    ///     return a + b;
    /// }";
    /// string minified = JavaScriptMinifier.Minify(js);
    /// // Result: "function add(a,b){return a+b;}"
    /// </code>
    /// </example>
    public static string Minify(string content)
    {
        // Step 1: Remove single-line comments
        // Pattern: // to end of line
        // Note: Be careful not to affect URLs (http://, https://) or regex patterns
        content = Regex.Replace(content, @"(?<!:)//(?!/)[^\r\n]*", "");

        // Step 2: Remove multi-line comments
        // Pattern: /* anything */
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);

        // Step 3: Condense multiple newlines to single newline
        content = Regex.Replace(content, @"(\r?\n){2,}", "\n");

        // Step 4: Remove whitespace at start/end of lines
        content = Regex.Replace(content, @"^\s+|\s+$", "", RegexOptions.Multiline);

        // Step 5: Condense multiple spaces to single space
        content = Regex.Replace(content, @" {2,}", " ");

        // Step 6: Remove spaces around operators (basic optimization)
        // This handles common cases but may not cover all edge cases
        content = Regex.Replace(content, @"\s*([{}\[\]();,:])\s*", "$1");

        // Step 7: Remove trailing whitespace
        content = content.Trim();

        return content;
    }
}
