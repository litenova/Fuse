using System.Text;
using System.Text.Json;

namespace Fuse.Cli.Minifiers;

public static class JsonMinifier
{
    public static string Minify(string content)
    {
        using var doc = JsonDocument.Parse(content);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        doc.WriteTo(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}