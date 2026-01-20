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
/// <description>Removal of preprocessor directives (#region, #pragma, #if)</description>
/// </item>
/// <item>
/// <description>Structural flattening (removing namespaces and indentation)</description>
/// </item>
/// <item>
/// <description>Aggressive reduction (attributes, redundant keywords, auto-props)</description>
/// </item>
/// <item>
/// <description>Syntax compression (removing whitespace around delimiters, flattening lines)</description>
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

        // Step 2: Remove preprocessor directives
        // CRITICAL: Must be done before flattening, as directives require newlines to terminate.
        content = RemovePreprocessorDirectives(content, options);

        // Step 3: Remove using statements to reduce noise at the top of the file
        content = RemoveUsings(content, options);

        // Step 4: Remove namespace declarations and flatten the structure
        content = RemoveNamespaces(content, options);

        // Step 5: Apply aggressive optimizations
        if (options.AggressiveCSharpReduction || options.ApplyAllOptions)
        {
            content = ApplyAggressiveOptimization(content);

            // Step 6: Aggressive Syntax Compression
            // This flattens the file and removes almost all newlines and unnecessary spaces
            // while preserving string literals.
            content = CompressSyntax(content);
        }
        else
        {
            // Standard whitespace cleanup (preserves line structure)
            content = OptimizeWhitespace(content);
        }

        return content.Trim();
    }

    /// <summary>
    /// Removes single-line, multi-line, and XML documentation comments.
    /// </summary>
    private static string RemoveComments(string content, FuseOptions options)
    {
        if (!options.RemoveCSharpComments && !options.ApplyAllOptions)
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
        // Standard removal: Regions only
        if (options.RemoveCSharpRegions && !options.ApplyAllOptions && !options.AggressiveCSharpReduction)
        {
            content = Regex.Replace(content, @"^\s*#(region|endregion)[^\r\n]*", "", RegexOptions.Multiline);
            return content;
        }

        // Aggressive removal: ALL directives (#region, #if, #pragma, #define, etc.)
        // We must remove these because we are about to flatten the file, and directives
        // rely on newlines to terminate. If we flatten without removing them, we break the code.
        if (options.AggressiveCSharpReduction || options.ApplyAllOptions)
        {
            content = Regex.Replace(content, @"^\s*#.*", "", RegexOptions.Multiline);
        }

        return content;
    }

    /// <summary>
    /// Removes using statements and alias directives.
    /// </summary>
    private static string RemoveUsings(string content, FuseOptions options)
    {
        if (!options.RemoveCSharpUsings && !options.ApplyAllOptions)
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
        if (!options.RemoveCSharpNamespaceDeclarations && !options.ApplyAllOptions)
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
            "GeneratedCode", "CompilerGenerated", "ExcludeFromCodeCoverage",
            "SuppressMessage", "AssemblyVersion", "AssemblyFileVersion",
            "AssemblyTitle", "AssemblyDescription", "AssemblyConfiguration",
            "AssemblyCompany", "AssemblyProduct", "AssemblyCopyright",
            "AssemblyTrademark", "AssemblyCulture"
        };

        // Pattern matches [Attribute] or [Attribute(...)]
        var attrPattern = $@"\[\s*({string.Join("|", noiseAttributes)})(\(.*\))?\s*\]\s*";
        content = Regex.Replace(content, attrPattern, "");

        // 2. Remove assembly-level attributes specifically (often found in GlobalSuppressions.cs)
        // Matches [assembly: SuppressMessage(...)]
        content = Regex.Replace(content, @"^\s*\[assembly:\s*SuppressMessage.*\]\s*$", "", RegexOptions.Multiline);

        // 3. Remove redundant "this." qualifier
        // "this.Property" -> "Property"
        content = Regex.Replace(content, @"\bthis\.", "");

        // 4. Compress Auto-Properties to single line
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
    /// Aggressively compresses syntax by removing whitespace around delimiters and flattening lines.
    /// </summary>
    /// <remarks>
    /// This method transforms code into a dense format similar to minified JavaScript.
    /// Example: "public class X { int y; }" becomes "public class X{int y;}"
    /// </remarks>
    private static string CompressSyntax(string content)
    {
        // Store string literals to preserve them during minification
        var literals = new List<string>();

        // Regex matches:
        // 1. Verbatim strings (@"...") handling double-quote escaping ("")
        // 2. Regular strings ("...") handling backslash escaping (\")
        // Note: Interpolated strings ($"...") are handled because the quote part matches #2, 
        // leaving the $ as a token which is preserved.
        string stringPattern = @"@""(?:""""|[^""])*""|""(?:\\.|[^""\\])*""";

        // Step 1: Protect literals by replacing them with placeholders
        content = Regex.Replace(content, stringPattern, m =>
        {
            literals.Add(m.Value);
            return $"__FUSE_STR_{literals.Count - 1}__";
        });

        // Step 2: Remove whitespace around delimiters
        // We include: { } ; , : ( ) = [ ]
        // This turns "int x = 1;" into "int x=1;"
        // This turns "public class X { }" into "public class X{}"
        // This turns "if ( x )" into "if(x)"
        content = Regex.Replace(content, @"\s*([{};,:()=\[\]])\s*", "$1");

        // Step 3: Collapse all remaining whitespace sequences (including newlines) to a single space
        // This handles the keywords: "public    static   void" -> "public static void"
        // It also merges lines: "int x; \n int y;" -> "int x;int y;" (because ; was handled above)
        content = Regex.Replace(content, @"\s+", " ");

        // Step 4: Restore literals from placeholders
        content = Regex.Replace(content, @"__FUSE_STR_(\d+)__", m =>
        {
            if (int.TryParse(m.Groups[1].Value, out int index) && index >= 0 && index < literals.Count)
            {
                return literals[index];
            }

            return m.Value;
        });

        return content;
    }

    /// <summary>
    /// Standard whitespace optimization (preserves line structure).
    /// </summary>
    private static string OptimizeWhitespace(string content)
    {
        // 1. Remove trailing whitespace from each line
        content = Regex.Replace(content, @"[\t ]+$", "", RegexOptions.Multiline);

        // 2. Condense multiple spaces to single space (preserving indentation at start of line)
        // Matches 2+ spaces that are NOT at the start of a line
        content = Regex.Replace(content, @"(?<!^)[ ]{2,}", " ", RegexOptions.Multiline);

        // 3. Remove ALL blank lines.
        // Replace 2 or more consecutive newlines with a single newline.
        content = Regex.Replace(content, @"(\r?\n){2,}", "\n");

        // 4. Remove blank lines that contain only whitespace (if any remain)
        content = Regex.Replace(content, @"^\s+$", "", RegexOptions.Multiline);

        return content;
    }
}