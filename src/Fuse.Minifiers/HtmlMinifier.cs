// -----------------------------------------------------------------------
// <copyright file="HtmlMinifier.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace Fuse.Minifiers;

/// <summary>
/// Provides minification functionality for HTML (HyperText Markup Language) files.
/// </summary>
/// <remarks>
/// <para>
/// This minifier performs the following optimizations on HTML content:
/// </para>
/// <list type="bullet">
///     <item><description>Removal of HTML comments (&lt;!-- ... --&gt;)</description></item>
///     <item><description>Removal of whitespace between HTML tags</description></item>
///     <item><description>Removal of unnecessary quotes around simple attribute values</description></item>
///     <item><description>Condensation of multiple consecutive spaces</description></item>
/// </list>
/// <para>
/// Note: This is a lightweight minifier. For production use, consider more sophisticated
/// solutions that handle edge cases like &lt;pre&gt; and &lt;script&gt; tags.
/// </para>
/// </remarks>
public static class HtmlMinifier
{
    /// <summary>
    /// Minifies HTML content by removing comments and unnecessary whitespace.
    /// </summary>
    /// <param name="content">The HTML content to minify.</param>
    /// <returns>The minified HTML content.</returns>
    /// <example>
    /// <code>
    /// string html = @"
    ///     &lt;!-- Header section --&gt;
    ///     &lt;div class=""container""&gt;
    ///         &lt;h1&gt;Hello World&lt;/h1&gt;
    ///     &lt;/div&gt;";
    /// string minified = HtmlMinifier.Minify(html);
    /// // Result: "&lt;div class=""container""&gt;&lt;h1&gt;Hello World&lt;/h1&gt;&lt;/div&gt;"
    /// </code>
    /// </example>
    public static string Minify(string content)
    {
        // Step 1: Remove HTML comments
        // Pattern matches <!-- followed by any content (including newlines) until -->
        // Uses Singleline mode so . matches newline characters
        content = Regex.Replace(content, @"<!--.*?-->", "", RegexOptions.Singleline);

        // Step 2: Remove whitespace between HTML tags
        // Converts "> <" (with any whitespace) to "><"
        // This significantly reduces file size in well-formatted HTML
        content = Regex.Replace(content, @">\s+<", "><");

        // Step 3: Remove unnecessary quotes from attribute values
        // HTML allows unquoted attribute values if they contain no spaces or special characters
        // Example: class="container" can become class=container if "container" has no spaces
        content = Regex.Replace(content, @"(\S+)=""([^""\s]+)""", m =>
        {
            // Only remove quotes if the value contains no special characters
            var attrName = m.Groups[1].Value;
            var attrValue = m.Groups[2].Value;
            
            // Keep quotes for values with special characters that require quoting
            if (Regex.IsMatch(attrValue, @"[<>&'""]"))
            {
                return m.Value;
            }
            
            return $"{attrName}={attrValue}";
        });

        // Step 4: Condense multiple consecutive spaces to single space
        // This handles text content between tags
        content = Regex.Replace(content, @" {2,}", " ");

        // Step 5: Remove leading/trailing whitespace from lines
        content = Regex.Replace(content, @"^\s+|\s+$", "", RegexOptions.Multiline);

        return content;
    }
}
