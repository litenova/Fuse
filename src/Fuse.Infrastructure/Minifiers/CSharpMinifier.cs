using System.Text.RegularExpressions;
using Fuse.Core;

namespace Fuse.Infrastructure.Minifiers;

public static class CSharpMinifier
{
    public static string Minify(string content, FuseOptions options)
    {
        // Remove unused using statements (basic implementation)
        // content = RemoveUnusedUsings(content);

        // Remove debug code
        content = RemoveDebugCode(content);

        // Remove comments
        if (options.RemoveCSharpComments)
        {
            content = RemoveComments(content);
        }

        // Remove region directives
        if (options.RemoveCSharpRegions)
        {
            content = RemoveRegionDirectives(content);
        }

        // Remove namespace declarations
        if (options.RemoveCSharpNamespaceDeclarations)
        {
            content = RemoveNamespaceDeclarations(content);
        }

        // Remove all using statements
        if (options.RemoveCSharpUsings)
        {
            content = RemoveAllUsings(content);
        }

        // Condense empty lines
        content = RemoveNewlines(content);

        // Optimize whitespace
        content = OptimizeWhitespace(content);

        return content;
    }

    private static string RemoveAllUsings(string code)
    {
        return Regex.Replace(code, @"^\s*using\s+.*?;\s*$", "", RegexOptions.Multiline);
    }

    private static string RemoveNamespaceDeclarations(string code)
    {
        // Remove file-scoped namespace declaration
        code = Regex.Replace(code, @"^\s*namespace\s+[\w.]+\s*;\s*$", "", RegexOptions.Multiline);

        // Remove classic namespace declaration
        code = Regex.Replace(code, @"namespace\s+[\w.]+\s*\{", "");
        code = Regex.Replace(code, @"^\s*\}\s*$", "", RegexOptions.Multiline);

        return code;
    }

    private static string RemoveComments(string content)
    {
        // Remove single-line comments
        content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);

        // Remove multi-line comments
        content = Regex.Replace(content, @"/\*[\s\S]*?\*/", "");

        // Remove XML documentation comments
        content = Regex.Replace(content, @"^\s*///.*$", "", RegexOptions.Multiline);

        return content;
    }

    private static string OptimizeWhitespace(string content)
    {
        // Replace any sequence of whitespace characters with a single space
        content = Regex.Replace(content, @"\s+", " ");

        // Remove spaces around brackets, braces, parentheses, semicolons, and colons
        // This includes (), {}, [], ;, and :
        content = Regex.Replace(content, @"\s*([(){}\[\];,:])\s*", "$1");

        // Remove spaces around compound operators and the ternary operator
        // This includes ++, --, +=, -=, *=, /=, %=, &&, ||, ==, !=, <=, >=, ?, and =>
        content = Regex.Replace(content, @"\s*(\+\+|--|\+=|-=|\*=|/=|%=|&&|\|\||==|!=|<=|>=|\?|=>)\s*", "$1");

        // Remove spaces around single-character operators
        // This includes =, +, -, *, /, %, <, >, ^, |, and &
        // Note: This might affect readability of complex expressions
        content = Regex.Replace(content, @"\s*(=|\+|-|\*|/|%|<|>|\^|\||&)\s*", "$1");

        // Remove any leading or trailing whitespace from the entire content
        return content.Trim();
    }

    private static string RemoveUnusedUsings(string content)
    {
        // This is a basic implementation and might not catch all cases
        var lines = content.Split('\n');
        var usings = new HashSet<string>();
        var nonUsingLines = new List<string>();

        foreach (var line in lines)
            if (line.TrimStart().StartsWith("using ") && line.TrimEnd().EndsWith(";"))
            {
                usings.Add(line.Trim());
            }
            else
            {
                nonUsingLines.Add(line);
            }

        var usedUsings = usings.Where(u => nonUsingLines.Any(l => l.Contains(u.Split()[1].TrimEnd(';')))).ToList();
        usedUsings.AddRange(usings.Where(u => u.Contains("System") || u.Contains("Microsoft"))); // Keep common namespaces

        return string.Join("\n", usedUsings.Concat(nonUsingLines));
    }

    private static string RemoveDebugCode(string content)
    {
        // Remove Debug.WriteLine statements
        content = Regex.Replace(content, @"Debug\.WriteLine\(.*?\);", "");

        // Remove code blocks wrapped in #if DEBUG ... #endif
        content = Regex.Replace(content, @"#if\s+DEBUG.*?#endif", "", RegexOptions.Singleline);

        return content;
    }

    private static string RemoveRegionDirectives(string content)
    {
        // Remove #region and #endregion directives
        content = Regex.Replace(content, @"#region.*?$", "", RegexOptions.Multiline);
        content = Regex.Replace(content, @"#endregion.*?$", "", RegexOptions.Multiline);

        return content;
    }

    private static string RemoveNewlines(string content)
    {
        // Preserve newlines for preprocessor directives
        content = Regex.Replace(content, @"(?<!^#.*)\n\s*", " ", RegexOptions.Multiline);

        // Preserve newlines for multiline string literals
        content = Regex.Replace(content, @"(?<!@""[^""]*)\n(?![^""]*"")", " ");

        // Remove any remaining multiple spaces
        content = Regex.Replace(content, @"\s+", " ");

        return content.Trim();
    }
}