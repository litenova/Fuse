using System.Text.RegularExpressions;

namespace Fuse.Infrastructure.Minifiers;

public static class ScssMinifier
{
    public static string Minify(string content)
    {
        // Remove comments
        content = Regex.Replace(content, @"/\*[\s\S]*?\*/|//.*", "");

        // Remove whitespace around operators
        content = Regex.Replace(content, @"\s*([{};:,+>])\s*", "$1");

        // Remove whitespace at the beginning and end of the content
        content = content.Trim();

        // Remove semicolons before closing braces
        content = Regex.Replace(content, @";\s*}", "}");

        // Remove unnecessary semicolons
        content = Regex.Replace(content, @";;+", ";");

        // Compress multiple spaces into one
        content = Regex.Replace(content, @"\s+", " ");

        // Remove spaces around parentheses
        content = Regex.Replace(content, @"\s*([()])\s*", "$1");

        // Preserve space after commas in function calls and property values
        content = Regex.Replace(content, @",\s*", ", ");

        // Remove newlines
        content = Regex.Replace(content, @"\r?\n", "");

        // Compress color values
        content = Regex.Replace(content, @"#([0-9a-fA-F])\1([0-9a-fA-F])\2([0-9a-fA-F])\3", "#$1$2$3");

        // Remove leading zeros
        content = Regex.Replace(content, @"([-+])?0+([1-9.]\d*)", "$1$2");

        // Remove units from zero values
        content = Regex.Replace(content, @"(^|\s|:)0(px|em|rem|%|in|cm|mm|pc|pt|ex|vw|vh|vmin|vmax)", "$10");

        return content;
    }
}