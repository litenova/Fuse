using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fuse.Infrastructure.Minifiers;

public static class JsonMinifier
{
    public static string Minify(string content)
    {
        // First attempt: Try standard JSON parsing
        try
        {
            // Remove comments first
            var cleanContent = RemoveComments(content);

            // Remove trailing commas
            cleanContent = RemoveTrailingCommas(cleanContent);

            using var doc = JsonDocument.Parse(cleanContent);
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
            doc.WriteTo(writer);
            writer.Flush();
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            // Second attempt: Aggressive minification without parsing
            return AggressiveMinify(content);
        }
    }

    private static string PreprocessJson(string json)
    {
        // Remove comments
        json = RemoveComments(json);

        // Remove trailing commas
        json = RemoveTrailingCommas(json);

        // Fix common issues
        json = FixCommonIssues(json);

        return json;
    }

    private static string AggressiveMinify(string content)
    {
        var result = content;

        // Remove all types of whitespace except within strings
        var inString = false;
        var escaped = false;
        var sb = new StringBuilder();

        for (var i = 0; i < result.Length; i++)
        {
            var c = result[i];

            if (c == '"' && !escaped)
            {
                inString = !inString;
                sb.Append(c);
            }
            else if (inString)
            {
                sb.Append(c);
                escaped = c == '\\' && !escaped;
            }
            else if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
                escaped = false;
            }
        }

        result = sb.ToString();

        // Remove comments
        result = RemoveComments(result);

        // Remove trailing commas
        result = RemoveTrailingCommas(result);

        // Compact spacing around operators
        result = Regex.Replace(result, @"\s*([\[\]{},:])\s*", "$1");

        // Remove empty lines
        result = Regex.Replace(result, @"^\s*$[\r\n]*", "", RegexOptions.Multiline);

        return result;
    }

    private static string RemoveComments(string json)
    {
        // Remove multi-line comments
        json = Regex.Replace(json, @"/\*.*?\*/", "", RegexOptions.Singleline);

        // Remove single-line comments
        json = Regex.Replace(json, @"//.*?(?:\r?\n|$)", "", RegexOptions.Multiline);

        return json;
    }

    private static string RemoveTrailingCommas(string json)
    {
        // Remove trailing commas in arrays and objects
        json = Regex.Replace(json, @",(\s*[\]}])", "$1");
        return json;
    }

    private static string FixCommonIssues(string json)
    {
        // Fix unquoted property names
        json = Regex.Replace(json, @"(\{|\,)\s*([a-zA-Z0-9_]+)\s*:", "$1\"$2\":");

        // Fix single quotes to double quotes
        json = Regex.Replace(json, @"'([^']*)'", "\"$1\"");

        // Fix missing quotes around string values
        json = Regex.Replace(json, @":\s*([a-zA-Z][a-zA-Z0-9_]*)\s*([,}])", ":\"$1\"$2");

        // Remove multiple commas
        json = Regex.Replace(json, @",\s*,", ",");

        // Remove comma after last property
        json = Regex.Replace(json, @",(\s*})", "$1");

        return json;
    }
}