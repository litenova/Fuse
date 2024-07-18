using System.Text.RegularExpressions;

namespace Fuse.Cli;

public static class CSharpMinifier
{
    public static string Minify(string code, bool aggressiveMinification = true, bool removeAllUsings = false, bool removeNamespaceDeclaration = false)
    {
        if (removeAllUsings)
        {
            code = RemoveAllUsings(code);
        }

        if (removeNamespaceDeclaration)
        {
            code = RemoveNamespaceDeclarations(code);
        }

        if (aggressiveMinification)
        {
            code = AggressiveMinify(code);
        }

        return code.Trim();
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

    private static string AggressiveMinify(string code)
    {
        // Preserve string literals and verbatim string literals
        var stringLiterals = new List<string>();
        code = Regex.Replace(code, @"(@""(?:[^""]|"""")*"")|""(?:[^""\n\\]|\\.)*""", match =>
        {
            stringLiterals.Add(match.Value);
            return $"__STRING__{stringLiterals.Count - 1}__";
        });

        // Remove comments
        code = Regex.Replace(code, @"//.*?$", "", RegexOptions.Multiline);
        code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);

        // Remove unnecessary whitespace
        code = Regex.Replace(code, @"\s+", " ");
        code = Regex.Replace(code, @"\s*([{}(),;:=+\-*/%&|^!~?<>])\s*", "$1");

        // Remove semicolons at the end of blocks
        code = Regex.Replace(code, @";\s*}", "}");

        // Remove unnecessary parentheses in control structures
        code = Regex.Replace(code, @"\bif\s*\((.*?)\)", match =>
        {
            var condition = match.Groups[1].Value;
            return condition.Contains(' ') ? $"if({condition})" : $"if{condition}";
        });

        // Shorten type names where possible
        code = Regex.Replace(code, @"\bString\b", "string");
        code = Regex.Replace(code, @"\bInt32\b", "int");
        code = Regex.Replace(code, @"\bBoolean\b", "bool");

        // Remove 'this.' where not necessary
        code = Regex.Replace(code, @"\bthis\.", "");

        // Shorten common method calls
        code = Regex.Replace(code, @"Console\.WriteLine", "Console.Write");
        code = Regex.Replace(code, @"\.ToString\(\)", "+''");

        // Remove unnecessary 'private' modifiers (they're implicit in classes)
        code = Regex.Replace(code, @"\bprivate\s+", "");

        // Restore string literals
        for (int i = 0; i < stringLiterals.Count; i++)
        {
            code = code.Replace($"__STRING__{i}__", stringLiterals[i]);
        }

        return code;
    }
}