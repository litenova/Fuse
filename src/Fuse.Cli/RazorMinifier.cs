using System.Text.RegularExpressions;

namespace Fuse.Cli;

public static class RazorMinifier
{
    public static string Minify(string razorContent)
    {
        // Preserve important whitespace in @code blocks
        var codeBlocks = new List<string>();
        razorContent = Regex.Replace(razorContent, @"@code\s*{([^}]*)}", match =>
        {
            codeBlocks.Add(match.Value);
            return $"@code{{CODEBLOCK{codeBlocks.Count - 1}}}";
        });

        // Remove comments
        razorContent = Regex.Replace(razorContent, @"@\*.*?\*@", "", RegexOptions.Singleline);
        razorContent = Regex.Replace(razorContent, @"<!--.*?-->", "", RegexOptions.Singleline);

        // Minimize whitespace
        razorContent = Regex.Replace(razorContent, @"\s+", " ");
        razorContent = Regex.Replace(razorContent, @"\s*>\s*<\s*", "><");
        razorContent = Regex.Replace(razorContent, @"\s*/?>", ">");

        // Minimize attributes
        razorContent = Regex.Replace(razorContent, @"\s+([a-zA-Z-]+)=""([^""]*)""", " $1=\"$2\"");

        // Restore @code blocks
        for (int i = 0; i < codeBlocks.Count; i++)
        {
            razorContent = razorContent.Replace($"@code{{CODEBLOCK{i}}}", codeBlocks[i]);
        }

        // Minimize @code blocks (carefully)
        razorContent = Regex.Replace(razorContent, @"@code\s*{([^}]*)}", match =>
        {
            var code = match.Groups[1].Value;
            code = Regex.Replace(code, @"^\s+|\s+$|\s*\r?\n\s*", " ");
            return $"@code{{{code}}}";
        });

        return razorContent.Trim();
    }
}