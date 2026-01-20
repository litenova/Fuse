// -----------------------------------------------------------------------
// <copyright file="RazorMinifier.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace Fuse.Minifiers;

/// <summary>
///     Provides minification functionality for Razor (.cshtml, .razor) files.
/// </summary>
/// <remarks>
///     <para>
///         This minifier handles the hybrid nature of Razor files which contain:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>HTML markup</description>
///         </item>
///         <item>
///             <description>C# code blocks (@{ ... })</description>
///         </item>
///         <item>
///             <description>Razor expressions (@expression or @(expression))</description>
///         </item>
///         <item>
///             <description>HTML and C# comments</description>
///         </item>
///     </list>
///     <para>
///         The minifier optimizes all aspects while preserving the functional relationship
///         between the C# code and HTML output.
///     </para>
/// </remarks>
public static class RazorMinifier
{
    /// <summary>
    ///     Minifies Razor content by removing comments and optimizing syntax.
    /// </summary>
    /// <param name="content">The Razor content to minify.</param>
    /// <returns>The minified Razor content.</returns>
    /// <example>
    ///     <code>
    /// string razor = @"
    /// &lt;!-- Page header --&gt;
    /// @{
    ///     var message = ""Hello"";
    /// }
    /// &lt;div class=""container""&gt;
    ///     @( message )
    /// &lt;/div&gt;";
    /// string minified = RazorMinifier.Minify(razor);
    /// // Results in compact Razor with optimized code blocks
    /// </code>
    /// </example>
    public static string Minify(string content)
    {
        // Step 1: Remove HTML comments
        // Pattern: <!-- anything -->
        // Uses Singleline mode so . matches newlines within comments
        content = Regex.Replace(content, @"<!--.*?-->", "", RegexOptions.Singleline);

        // Step 2: Remove C# multi-line comments (used in Razor code blocks)
        // Pattern: /* anything */
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);

        // Step 3: Remove C# single-line comments within code blocks
        // Pattern: // to end of line
        // Be careful not to affect URLs in HTML attributes
        content = Regex.Replace(content, @"(?<!:)//(?!/)[^\r\n]*", "");

        // Step 4: Remove Razor comments
        // Pattern: @* anything *@
        content = Regex.Replace(content, @"@\*.*?\*@", "", RegexOptions.Singleline);

        // Step 5: Remove whitespace between HTML tags
        // Converts "> <" (with any whitespace) to "><"
        content = Regex.Replace(content, @">\s+<", "><");

        // Step 6: Optimize Razor expressions by removing unnecessary spaces
        // Pattern: @( expression ) becomes @(expression)
        content = Regex.Replace(content, @"@\(\s+", "@(");
        content = Regex.Replace(content, @"\s+\)", ")");

        // Step 7: Condense multiple spaces to single space
        content = Regex.Replace(content, @" {2,}", " ");

        // Step 8: Remove leading/trailing whitespace from lines
        content = Regex.Replace(content, @"^\s+|\s+$", "", RegexOptions.Multiline);

        // Step 9: Remove multiple consecutive blank lines
        content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");

        return content;
    }
}