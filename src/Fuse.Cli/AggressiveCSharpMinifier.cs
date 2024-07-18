using System.Text.RegularExpressions;

namespace Fuse.Cli;

public static class CSharpMinifier
{
    public static string Minify(string csharpCode)
    {
        // Preserve string literals and verbatim string literals
        var stringLiterals = new List<string>();
        csharpCode = Regex.Replace(csharpCode, @"(@""(?:[^""]|"""")*"")|""(?:[^""\n\\]|\\.)*""", match =>
        {
            stringLiterals.Add(match.Value);
            return $"__STRING__{stringLiterals.Count - 1}__";
        });

        // Remove comments
        csharpCode = Regex.Replace(csharpCode, @"//.*?$", "", RegexOptions.Multiline);
        csharpCode = Regex.Replace(csharpCode, @"/\*.*?\*/", "", RegexOptions.Singleline);

        // Remove unnecessary whitespace
        csharpCode = Regex.Replace(csharpCode, @"\s+", " ");
        csharpCode = Regex.Replace(csharpCode, @"\s*([{}(),;:=+\-*/%&|^!~?<>])\s*", "$1");

        // Restore string literals
        for (int i = 0; i < stringLiterals.Count; i++)
        {
            csharpCode = csharpCode.Replace($"__STRING__{i}__", stringLiterals[i]);
        }

        return csharpCode.Trim();
    }
}