using System.Text.RegularExpressions;

namespace Fuse.Cli.Minifiers;

public static class RazorMinifier
{
    public static string Minify(string content)
    {
        // Remove comments (HTML and C# style)
        content = Regex.Replace(content, @"<!--.*?-->", "", RegexOptions.Singleline);
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
        content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);

        // Optimize Razor syntax
        content = Regex.Replace(content, @"@\(\s*([^)]+)\s*\)", "@($1)");
        content = Regex.Replace(content, @"@\{\s+", "@{");
        content = Regex.Replace(content, @"\s+\}", "}");

        // Minimize HTML
        content = Regex.Replace(content, @">\s+<", "><");
        content = Regex.Replace(content, @"(\S+)=&quot;([^&]*)&quot;", m => 
            m.Groups[2].Value.Contains(' ') ? m.Value : $"{m.Groups[1].Value}={m.Groups[2].Value}");

        // Condense empty lines
        content = Regex.Replace(content, @"^\s*$\n(\s*$\n)+", "\n", RegexOptions.Multiline);

        return content;
    }
}