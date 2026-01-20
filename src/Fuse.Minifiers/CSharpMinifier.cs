// -----------------------------------------------------------------------
// <copyright file="CSharpMinifier.cs" company="Fuse">
// Copyright (c) Fuse. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
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
/// <item>
/// <description>Removal of comments (single-line, multi-line, XML)</description>
/// </item>
/// <item>
/// <description>Removal of preprocessor directives (#region, #pragma)</description>
/// </item>
/// <item>
/// <description>Structural flattening (removing namespaces and indentation)</description>
/// </item>
/// <item>
/// <description>Aggressive reduction (attributes, redundant keywords, auto-props)</description>
/// </item>
/// </list>
/// </remarks>
public static class CSharpMinifier
{
    /// <summary>
    /// Minifies C# content based on the provided options.
    /// </summary>
    /// <param name="content">The C# content to minify.</param>
    /// <param name="options">The fusion options.</param>
    /// <returns>The minified C# content.</returns>
    public static string Minify(string content, FuseOptions options)
    {
        // Step 1: Remove comments first to prevent interference with other regex patterns
        content = RemoveComments(content, options);

        // Step 2: Remove preprocessor directives (#region, #if, etc.)
        content = RemovePreprocessorDirectives(content, options);

        // Step 3: Remove using statements to reduce noise at the top of the file
        content = RemoveUsings(content, options);

        // Step 4: Remove namespace declarations and flatten the structure
        content = RemoveNamespaces(content, options);

        // Step 5: Apply aggressive optimizations (attributes, keywords, properties)
        // This is enabled if specifically requested OR if 'ApplyAllOptions' (fuse dotnet --all) is true
        if (options.AggressiveCSharpReduction || options.ApplyAllOptions)
        {
            content = ApplyAggressiveOptimization(content);
        }

        // Step 6: Final whitespace cleanup (condense newlines, trim lines)
        content = OptimizeWhitespace(content);

        return content.Trim();
    }

    /// <summary>
    /// Removes single-line, multi-line, and XML documentation comments.
    /// </summary>
    private static string RemoveComments(string content, FuseOptions options)
    {
        if (options is { RemoveCSharpComments: false, ApplyAllOptions: false })
        {
            return content;
        }

        // Remove XML documentation comments (/// ...)
        // Must be done before single-line comments to avoid partial matches
        content = Regex.Replace(content, @"///[^\r\n]*", "");

        // Remove Single-line comments (// ...)
        // Uses lookbehind (?<!:) to avoid matching URLs like http://
        content = Regex.Replace(content, @"(?<!:)//(?!/)[^\r\n]*", "");

        // Remove Multi-line comments (/* ... */)
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);

        return content;
    }

    /// <summary>
    /// Removes #region, #endregion, and other compiler directives.
    /// </summary>
    private static string RemovePreprocessorDirectives(string content, FuseOptions options)
    {
        // Always remove regions if requested
        if (options.RemoveCSharpRegions || options.ApplyAllOptions)
        {
            content = Regex.Replace(content, @"^\s*#(region|endregion)[^\r\n]*", "", RegexOptions.Multiline);
        }

        // Remove other directives if aggressive reduction is enabled
        if (options.AggressiveCSharpReduction || options.ApplyAllOptions)
        {
            // Remove #pragma (warnings) and #nullable contexts
            content = Regex.Replace(content, @"^\s*#(pragma|nullable)[^\r\n]*", "", RegexOptions.Multiline);
        }

        return content;
    }

    /// <summary>
    /// Removes using statements and alias directives.
    /// </summary>
    private static string RemoveUsings(string content, FuseOptions options)
    {
        if (options is { RemoveCSharpUsings: false, ApplyAllOptions: false })
        {
            return content;
        }

        // Remove standard using statements: "using System.Text;"
        content = Regex.Replace(content, @"^\s*using\s+[\w\.]+;\s*(\r?\n)?", "", RegexOptions.Multiline);

        // Remove alias directives: "using Project = My.Project;"
        content = Regex.Replace(content, @"^\s*using\s+[A-Za-z0-9_]+\s*=\s*[\w\.]+;\s*(\r?\n)?", "", RegexOptions.Multiline);

        return content;
    }

    /// <summary>
    /// Removes namespace declarations and unindents the code to save horizontal tokens.
    /// </summary>
    private static string RemoveNamespaces(string content, FuseOptions options)
    {
        if (options is { RemoveCSharpNamespaceDeclarations: false, ApplyAllOptions: false })
        {
            return content;
        }

        // Handle file-scoped namespaces (C# 10+): "namespace X;"
        // These are easy to remove as they don't affect indentation structure
        content = Regex.Replace(content, @"^\s*namespace\s+[\w\.]+\s*;\s*(\r?\n)?", "", RegexOptions.Multiline);

        // Handle block-scoped namespaces: "namespace X { ... }"
        // 1. Remove the "namespace X {" line
        content = Regex.Replace(content, @"^\s*namespace\s+[\w\.]+\s*[\r\n\s]*\{", "", RegexOptions.Multiline);

        // 2. Unindent the content
        // Since we removed the wrapping namespace, the code inside is now indented unnecessarily.
        // We remove 4 spaces or 1 tab from the start of every line.
        content = Regex.Replace(content, @"^(\s{4}|\t)", "", RegexOptions.Multiline);

        // Note: We do not attempt to remove the closing '}' of the namespace.
        // Finding the exact matching brace via Regex is impossible (requires a parser).
        // Leaving a dangling '}' at the end of the file is acceptable for LLM context
        // as it prioritizes content density over compilation correctness.

        return content;
    }

    /// <summary>
    /// Applies aggressive optimizations to reduce token count significantly.
    /// Includes attribute removal, keyword reduction, and property compression.
    /// </summary>
    private static string ApplyAggressiveOptimization(string content)
    {
        // 1. Remove "Noise" Attributes
        // These attributes provide metadata for tools/compilers but add little semantic value for LLMs.
        var noiseAttributes = new[]
        {
            "DebuggerDisplay", "DebuggerStepThrough", "DebuggerNonUserCode",
            "MethodImpl", "EditorBrowsable", "Serializable", "Obsolete",
            "GeneratedCode", "CompilerGenerated", "ExcludeFromCodeCoverage"
        };

        // Pattern matches [Attribute] or [Attribute(...)]
        var attrPattern = $@"\[\s*({string.Join("|", noiseAttributes)})(\(.*\))?\s*\]\s*";
        content = Regex.Replace(content, attrPattern, "");

        // 2. Remove redundant "this." qualifier
        // "this.Property" -> "Property"
        content = Regex.Replace(content, @"\bthis\.", "");

        // 3. Compress Auto-Properties to single line
        // From:
        // public int Id
        // {
        //    get;
        //    set;
        // }
        // To: public int Id { get; set; }
        content = Regex.Replace(content, @"\{\s*get;\s*set;\s*\}", "{ get; set; }");
        content = Regex.Replace(content, @"\{\s*get;\s*\}", "{ get; }");
        content = Regex.Replace(content, @"\{\s*set;\s*\}", "{ set; }");

        return content;
    }

    /// <summary>
    /// Performs final whitespace cleanup including line trimming and condensation.
    /// </summary>
    private static string OptimizeWhitespace(string content)
    {
        // Remove trailing whitespace from each line
        content = Regex.Replace(content, @"[\t ]+$", "", RegexOptions.Multiline);

        // Condense multiple spaces to single space (preserving indentation at start of line)
        // Matches 2+ spaces that are NOT at the start of a line
        content = Regex.Replace(content, @"(?<!^)[ ]{2,}", " ", RegexOptions.Multiline);

        // Replace 3 or more consecutive newlines with 2 newlines
        // This preserves paragraph separation but removes massive gaps
        content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");

        // Remove blank lines that contain only whitespace
        content = Regex.Replace(content, @"^\s+$", "", RegexOptions.Multiline);

        return content;
    }
}