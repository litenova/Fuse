using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fuse.Cli.Minifiers;

public static class JsonMinifier
{
    public static string Minify(string content)
    {
        string jsonWithoutComments = RemoveComments(content);

        using var doc = JsonDocument.Parse(jsonWithoutComments);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        doc.WriteTo(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string RemoveComments(string json)
    {
        var regex = new Regex(@"^\s*//.*$", RegexOptions.Multiline);
        return regex.Replace(json, string.Empty);
    }
}