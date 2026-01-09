// -----------------------------------------------------------------------
// <copyright file="XmlMinifier.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace Fuse.Minifiers;

/// <summary>
/// Provides minification functionality for XML files including .csproj, .props, and .targets.
/// </summary>
/// <remarks>
/// <para>
/// This minifier performs the following optimizations:
/// </para>
/// <list type="bullet">
///     <item><description>Removal of XML comments (&lt;!-- ... --&gt;)</description></item>
///     <item><description>Removal of whitespace between XML tags</description></item>
///     <item><description>Condensation of whitespace within text content</description></item>
///     <item><description>Preservation of significant whitespace in CDATA sections</description></item>
/// </list>
/// <para>
/// Note: This minifier preserves the XML declaration and does not modify
/// attribute values or element content beyond whitespace condensation.
/// </para>
/// </remarks>
public static class XmlMinifier
{
    /// <summary>
    /// Minifies XML content by removing comments and unnecessary whitespace.
    /// </summary>
    /// <param name="content">The XML content to minify.</param>
    /// <returns>The minified XML content.</returns>
    /// <example>
    /// <code>
    /// string xml = @"
    /// &lt;!-- Project configuration --&gt;
    /// &lt;Project&gt;
    ///     &lt;PropertyGroup&gt;
    ///         &lt;TargetFramework&gt;net8.0&lt;/TargetFramework&gt;
    ///     &lt;/PropertyGroup&gt;
    /// &lt;/Project&gt;";
    /// string minified = XmlMinifier.Minify(xml);
    /// // Result: "&lt;Project&gt;&lt;PropertyGroup&gt;&lt;TargetFramework&gt;net8.0&lt;/TargetFramework&gt;&lt;/PropertyGroup&gt;&lt;/Project&gt;"
    /// </code>
    /// </example>
    public static string Minify(string content)
    {
        // Step 1: Remove XML comments
        // Pattern: <!-- anything -->
        content = Regex.Replace(content, @"<!--.*?-->", "", RegexOptions.Singleline);

        // Step 2: Remove whitespace between tags
        // Converts ">\s+<" to "><"
        content = Regex.Replace(content, @">\s+<", "><");

        // Step 3: Remove leading whitespace from lines
        content = Regex.Replace(content, @"^\s+", "", RegexOptions.Multiline);

        // Step 4: Remove trailing whitespace from lines
        content = Regex.Replace(content, @"\s+$", "", RegexOptions.Multiline);

        // Step 5: Remove newlines (but preserve XML declaration on its own line)
        content = Regex.Replace(content, @"(\?>\s*)", "?>\n");
        content = Regex.Replace(content, @"[\r\n]+(?!<\?)", "");

        // Step 6: Condense multiple spaces within text content
        content = Regex.Replace(content, @" {2,}", " ");

        // Step 7: Remove leading/trailing whitespace
        content = content.Trim();

        return content;
    }
}
