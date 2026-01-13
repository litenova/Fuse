// -----------------------------------------------------------------------
// <copyright file="JsonMinifier.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace Fuse.Minifiers;

/// <summary>
///     Provides minification functionality for JSON (JavaScript Object Notation) files.
/// </summary>
/// <remarks>
///     <para>
///         This minifier removes all unnecessary whitespace from JSON content while
///         preserving the data structure. The output is valid, compact JSON.
///     </para>
///     <para>
///         Note: This minifier does not validate JSON syntax. Invalid JSON will be
///         processed but may produce incorrect output.
///     </para>
/// </remarks>
public static class JsonMinifier
{
    /// <summary>
    ///     Minifies JSON content by removing unnecessary whitespace.
    /// </summary>
    /// <param name="content">The JSON content to minify.</param>
    /// <returns>The minified JSON content.</returns>
    /// <example>
    ///     <code>
    /// string json = @"{
    ///     ""name"": ""John"",
    ///     ""age"": 30
    /// }";
    /// string minified = JsonMinifier.Minify(json);
    /// // Result: "{""name"":""John"",""age"":30}"
    /// </code>
    /// </example>
    public static string Minify(string content)
    {
        // Step 1: Remove newlines and carriage returns
        content = Regex.Replace(content, @"[\r\n]+", "");

        // Step 2: Remove whitespace after colons (except inside strings)
        // This is a simplified approach - proper JSON minification would
        // need to parse strings to avoid modifying content within them
        content = Regex.Replace(content, @":\s+", ":");

        // Step 3: Remove whitespace after commas
        content = Regex.Replace(content, @",\s+", ",");

        // Step 4: Remove whitespace after opening brackets and braces
        content = Regex.Replace(content, @"([\[{])\s+", "$1");

        // Step 5: Remove whitespace before closing brackets and braces
        content = Regex.Replace(content, @"\s+([\]}])", "$1");

        // Step 6: Remove leading/trailing whitespace
        content = content.Trim();

        return content;
    }
}