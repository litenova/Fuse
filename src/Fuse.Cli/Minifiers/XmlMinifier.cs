namespace Fuse.Cli.Minifiers;

public static class XmlMinifier
{
    public static string Minify(string content)
    {
        var doc = System.Xml.Linq.XDocument.Parse(content);
        return doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }
}