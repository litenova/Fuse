using System.Text.RegularExpressions;

namespace Fuse.Infrastructure.Minifiers;

public static class CssMinifier
{
    public static string Minify(string content)
    {
        // Remove comments
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);

        // Remove whitespace around selectors, properties, and values
        content = Regex.Replace(content, @"\s*([{}:;,])\s*", "$1");

        // Remove last semicolon in each rule
        content = Regex.Replace(content, @";}", "}");

        return content;
    }
}