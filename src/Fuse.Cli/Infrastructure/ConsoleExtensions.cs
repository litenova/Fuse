// src/Fuse.Cli/Infrastructure/ConsoleExtensions.cs
using CliFx.Infrastructure;

namespace Fuse.Cli.Infrastructure;

public static class ConsoleExtensions
{
    public static void WithForegroundColor(this IConsole console, ConsoleColor color, Action action)
    {
        var originalColor = console.ForegroundColor;
        try
        {
            console.ForegroundColor = color;
            action();
        }
        finally
        {
            console.ForegroundColor = originalColor;
        }
    }
}