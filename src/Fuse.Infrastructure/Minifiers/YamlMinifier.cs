using System.Text.RegularExpressions;

namespace Fuse.Infrastructure.Minifiers;

public static class YamlMinifier
{
    public static string Minify(string content)
    {
        // Remove comments (lines starting with #)
        content = Regex.Replace(content, @"^\s*#.*$", "", RegexOptions.Multiline);

        // Remove empty lines
        content = Regex.Replace(content, @"^\s*$\n", "", RegexOptions.Multiline);

        // Trim whitespace from each line while preserving YAML indentation structure
        var lines = content.Split('\n');
        var minifiedLines = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Preserve leading whitespace for YAML indentation, but trim trailing whitespace
            var trimmedLine = line.TrimEnd();
            minifiedLines.Add(trimmedLine);
        }

        return string.Join("\n", minifiedLines);
    }
}