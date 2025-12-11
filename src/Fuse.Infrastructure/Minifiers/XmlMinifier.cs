using System.Xml.Linq;

namespace Fuse.Infrastructure.Minifiers;

public static class XmlMinifier
{
    public static string Minify(string content)
    {
        var doc = XDocument.Parse(content);
        return doc.ToString(SaveOptions.DisableFormatting);
    }
}