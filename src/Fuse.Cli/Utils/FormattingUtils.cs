namespace Fuse.Cli.Utils;

internal static class FormattingUtils
{
    /// <summary>
    /// Formats a file size in bytes into a string like "XXX.XX KB (Y.YY MB)".
    /// </summary>
    /// <param name="bytes">The file size in bytes.</param>
    /// <returns>A formatted string.</returns>
    public static string FormatFileSize(long bytes)
    {
        if (bytes < 0)
        {
            return "0 KB (0.00 MB)";
        }

        double kb = bytes / 1024.0;
        double mb = kb / 1024.0;

        return $"{kb:N2} KB ({mb:N2} MB)";
    }
}