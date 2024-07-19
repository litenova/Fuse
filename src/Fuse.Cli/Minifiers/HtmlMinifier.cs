using System.Text.RegularExpressions;

namespace Fuse.Cli.Minifiers;

public static class HtmlMinifier
{
    public static string Minify(string content)
    {
        // Remove HTML comments
        content = Regex.Replace(content, @"<!--.*?-->", "", RegexOptions.Singleline);

        // Remove whitespace between HTML tags
        content = Regex.Replace(content, @">\s+<", "><");

        // Remove unnecessary quotes in HTML attributes
        content = Regex.Replace(content, @"(\S+)=&quot;([^&]*)&quot;", m =>
            m.Groups[2].Value.Contains(' ') ? m.Value : $"{m.Groups[1].Value}={m.Groups[2].Value}");

        // Condense multiple spaces
        content = Regex.Replace(content, @"[ \t]+", " ");

        return content;
    }
}