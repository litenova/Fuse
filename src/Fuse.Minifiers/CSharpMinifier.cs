// -----------------------------------------------------------------------
// <copyright file="CSharpMinifier.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.RegularExpressions;
using Fuse.Core;

namespace Fuse.Minifiers;

/// <summary>
/// Provides minification functionality for C# source code files.
/// </summary>
/// <remarks>
/// <para>
/// This minifier performs various optimizations on C# code including:
/// </para>
/// <list type="bullet">
///     <item><description>Removal of single-line, multi-line, and XML documentation comments</description></item>
///     <item><description>Removal of #region and #endregion directives</description></item>
///     <item><description>Removal of namespace declarations (both file-scoped and classic)</description></item>
///     <item><description>Removal of using statements</description></item>
///     <item><description>Removal of debug code (Debug.WriteLine and #if DEBUG blocks)</description></item>
///     <item><description>Whitespace optimization and condensation</description></item>
/// </list>
/// <para>
/// The minification behavior is controlled through <see cref="FuseOptions"/>.
/// </para>
/// </remarks>
public static class CSharpMinifier
{
    /// <summary>
    /// Minifies C# source code by removing unnecessary content and optimizing whitespace.
    /// </summary>
    /// <param name="content">The C# source code content to minify.</param>
    /// <param name="options">The options that control which minification steps are applied.</param>
    /// <returns>The minified C# source code.</returns>
    /// <example>
    /// <code>
    /// var options = new FuseOptions { RemoveCSharpComments = true };
    /// var minified = CSharpMinifier.Minify(sourceCode, options);
    /// </code>
    /// </example>
    public static string Minify(string content, FuseOptions options)
    {
        // Step 1: Remove debug-specific code first as it may contain other elements
        // This includes Debug.WriteLine statements and #if DEBUG blocks
        content = RemoveDebugCode(content);

        // Step 2: Remove comments if the option is enabled
        // This removes single-line (//), multi-line (/* */), and XML documentation (///) comments
        if (options.RemoveCSharpComments)
        {
            content = RemoveComments(content);
        }

        // Step 3: Remove #region and #endregion directives if enabled
        // These are organizational markers that aren't needed in minified output
        if (options.RemoveCSharpRegions)
        {
            content = RemoveRegionDirectives(content);
        }

        // Step 4: Remove namespace declarations if enabled
        // Removes both file-scoped (namespace X;) and classic (namespace X { }) declarations
        if (options.RemoveCSharpNamespaceDeclarations)
        {
            content = RemoveNamespaceDeclarations(content);
        }

        // Step 5: Remove all using statements if enabled
        // This removes all 'using' directives at the top of the file
        if (options.RemoveCSharpUsings)
        {
            content = RemoveAllUsings(content);
        }

        // Step 6: Condense newlines and empty lines
        // Reduces multiple consecutive newlines to improve density
        content = RemoveNewlines(content);

        // Step 7: Final whitespace optimization
        // Removes unnecessary spaces around operators and punctuation
        content = OptimizeWhitespace(content);

        return content;
    }

    /// <summary>
    /// Removes all using statements from the source code.
    /// </summary>
    /// <param name="code">The C# source code.</param>
    /// <returns>The code with all using statements removed.</returns>
    private static string RemoveAllUsings(string code)
    {
        // Match lines that start with 'using' and end with semicolon
        // Uses multiline mode to match ^ at the start of each line
        return Regex.Replace(code, @"^\s*using\s+.*?;\s*$", "", RegexOptions.Multiline);
    }

    /// <summary>
    /// Removes namespace declarations from the source code.
    /// </summary>
    /// <param name="code">The C# source code.</param>
    /// <returns>The code with namespace declarations removed.</returns>
    /// <remarks>
    /// Handles both file-scoped namespace declarations (C# 10+) like <c>namespace MyApp;</c>
    /// and classic block-style declarations like <c>namespace MyApp { ... }</c>.
    /// </remarks>
    private static string RemoveNamespaceDeclarations(string code)
    {
        // Remove file-scoped namespace declaration (C# 10+ feature)
        // Example: "namespace MyApp.Services;"
        code = Regex.Replace(code, @"^\s*namespace\s+[\w.]+\s*;\s*$", "", RegexOptions.Multiline);

        // Remove classic namespace declaration opening brace
        // Example: "namespace MyApp.Services {"
        code = Regex.Replace(code, @"namespace\s+[\w.]+\s*\{", "");

        // Remove the closing brace that would have matched the namespace
        // This is a simplified approach and may remove other closing braces at line start
        code = Regex.Replace(code, @"^\s*\}\s*$", "", RegexOptions.Multiline);

        return code;
    }

    /// <summary>
    /// Removes all types of comments from C# source code.
    /// </summary>
    /// <param name="content">The C# source code.</param>
    /// <returns>The code with all comments removed.</returns>
    /// <remarks>
    /// Removes three types of comments:
    /// <list type="bullet">
    ///     <item><description>Single-line comments: <c>// comment</c></description></item>
    ///     <item><description>Multi-line comments: <c>/* comment */</c></description></item>
    ///     <item><description>XML documentation comments: <c>/// &lt;summary&gt;</c></description></item>
    /// </list>
    /// </remarks>
    private static string RemoveComments(string content)
    {
        // Remove single-line comments (// to end of line)
        // Note: This is a simple implementation that may affect string literals containing //
        content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);

        // Remove multi-line comments (/* ... */)
        // Uses Singleline option so . matches newlines within comments
        content = Regex.Replace(content, @"/\*[\s\S]*?\*/", "");

        // Remove XML documentation comments (/// ...)
        // These are commonly used for IntelliSense documentation
        content = Regex.Replace(content, @"^\s*///.*$", "", RegexOptions.Multiline);

        return content;
    }

    /// <summary>
    /// Removes #region and #endregion preprocessor directives.
    /// </summary>
    /// <param name="content">The C# source code.</param>
    /// <returns>The code with region directives removed.</returns>
    private static string RemoveRegionDirectives(string content)
    {
        // Remove #region directives with optional name
        // Matches: #region, #region Name, #region "Name", etc.
        content = Regex.Replace(content, @"^\s*#region\s*.*$", "", RegexOptions.Multiline);

        // Remove #endregion directives
        content = Regex.Replace(content, @"^\s*#endregion\s*.*$", "", RegexOptions.Multiline);

        return content;
    }

    /// <summary>
    /// Removes debug-specific code including Debug.WriteLine calls and #if DEBUG blocks.
    /// </summary>
    /// <param name="content">The C# source code.</param>
    /// <returns>The code with debug code removed.</returns>
    /// <remarks>
    /// This method targets:
    /// <list type="bullet">
    ///     <item><description>Debug.WriteLine and similar Debug class method calls</description></item>
    ///     <item><description>#if DEBUG ... #endif conditional compilation blocks</description></item>
    /// </list>
    /// </remarks>
    private static string RemoveDebugCode(string content)
    {
        // Remove Debug.WriteLine statements
        // Matches Debug.WriteLine(...) including nested parentheses
        content = Regex.Replace(content, @"^\s*Debug\.Write(Line)?\s*\(.*?\)\s*;\s*$", "", RegexOptions.Multiline);

        // Remove #if DEBUG blocks entirely
        // Uses Singleline mode to match across multiple lines
        content = Regex.Replace(content, @"#if\s+DEBUG[\s\S]*?#endif", "", RegexOptions.Singleline);

        return content;
    }

    /// <summary>
    /// Condenses multiple consecutive newlines into single newlines.
    /// </summary>
    /// <param name="content">The C# source code.</param>
    /// <returns>The code with condensed newlines.</returns>
    private static string RemoveNewlines(string content)
    {
        // Replace 3 or more consecutive newlines with 2 newlines
        // Preserves paragraph separation while removing excessive whitespace
        content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");

        // Remove blank lines that contain only whitespace
        content = Regex.Replace(content, @"^\s+$", "", RegexOptions.Multiline);

        return content;
    }

    /// <summary>
    /// Optimizes whitespace by removing unnecessary spaces.
    /// </summary>
    /// <param name="content">The C# source code.</param>
    /// <returns>The code with optimized whitespace.</returns>
    /// <remarks>
    /// This method removes:
    /// <list type="bullet">
    ///     <item><description>Trailing whitespace at end of lines</description></item>
    ///     <item><description>Multiple consecutive spaces (condensed to single space)</description></item>
    /// </list>
    /// </remarks>
    private static string OptimizeWhitespace(string content)
    {
        // Remove trailing whitespace from each line
        content = Regex.Replace(content, @"[\t ]+$", "", RegexOptions.Multiline);

        // Condense multiple spaces to single space (but not at line start - preserve indentation)
        content = Regex.Replace(content, @"(?<!^)[ ]{2,}", " ", RegexOptions.Multiline);

        return content;
    }
}
