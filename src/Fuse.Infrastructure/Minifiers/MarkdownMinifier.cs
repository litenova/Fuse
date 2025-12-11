using System.Text.RegularExpressions;

namespace Fuse.Infrastructure.Minifiers;

public static class MarkdownMinifier
{
    public static string Minify(string content)
    {
        // Remove comments
        content = Regex.Replace(content, @"<!--[\s\S]*?-->", "");

        // Trim each line
        content = string.Join("\n", content.Split('\n').Select(line => line.Trim()));

        // Remove multiple newlines (preserving two for paragraph breaks)
        content = Regex.Replace(content, @"\n{3,}", "\n\n");

        // Remove spaces before and after bold/italic markers
        content = Regex.Replace(content, @"\s*([*_]{1,2})\s*", "$1");

        // Remove spaces inside link syntax
        content = Regex.Replace(content, @"\[\s*(.*?)\s*\]\(\s*(.*?)\s*\)", "[$1]($2)");

        // Remove spaces inside inline code blocks
        content = Regex.Replace(content, @"`\s*(.*?)\s*`", "`$1`");

        // Compress horizontal rules
        content = Regex.Replace(content, @"^\s*[-*_]{3,}\s*$", "---", RegexOptions.Multiline);

        // Compress headings
        content = Regex.Replace(content, @"^(#+)\s+", "$1", RegexOptions.Multiline);

        // Remove empty lines at the start and end of the file
        content = Regex.Replace(content, @"^\s+|\s+$", "");

        return content;
    }
}