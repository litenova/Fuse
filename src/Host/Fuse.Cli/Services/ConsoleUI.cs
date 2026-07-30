namespace Fuse.Cli.Services;

/// <summary>
///     Console user interface implementation using System.Console.
/// </summary>
/// <remarks>
///     This implementation provides colored console output using ANSI color codes and ASCII status labels.
/// </remarks>
public sealed class ConsoleUI : IConsoleUI
{
    /// <summary>
    ///     Writes a success message to the console in green.
    /// </summary>
    /// <param name="message">The success message to display.</param>
    public void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  [OK] {message}");
        Console.ResetColor();
    }

    /// <summary>
    ///     Writes an error message to the console in red.
    /// </summary>
    /// <param name="message">The error message to display.</param>
    public void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  [ERROR] {message}");
        Console.ResetColor();
    }

    /// <summary>
    ///     Writes a step or progress message to the console in gray.
    /// </summary>
    /// <param name="message">The step message to display.</param>
    public void WriteStep(string message)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"  {message}");
        Console.ResetColor();
    }

    /// <summary>
    ///     Writes a result message to the console in gray.
    /// </summary>
    /// <param name="message">The result message to display.</param>
    public void WriteResult(string message)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"  {message}");
        Console.ResetColor();
    }

    /// <summary>
    ///     Converts a full file path to a user-friendly path by replacing
    ///     the user profile directory with ~.
    /// </summary>
    /// <param name="path">The full file path to convert.</param>
    /// <returns>A user-friendly path with ~ substitution.</returns>
    public static string GetFriendlyPath(string path)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(userProfile) && path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return "~" + path.Substring(userProfile.Length);
        }

        return path;
    }
}
