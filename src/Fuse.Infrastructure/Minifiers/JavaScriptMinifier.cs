using System.Text.RegularExpressions;

namespace Fuse.Infrastructure.Minifiers;

public static class JavaScriptMinifier
{
    public static string Minify(string content)
    {
        // Remove comments
        content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);

        // Remove whitespace around operators and punctuation
        content = Regex.Replace(content, @"\s*([=+\-*/%<>!&|:;,{}()])\s*", "$1");

        // Remove unnecessary semicolons
        content = Regex.Replace(content, @";}", "}");

        return content;
    }
}