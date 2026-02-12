// -----------------------------------------------------------------------
// <copyright file="StderrConsoleUI.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Fuse.Core.Abstractions;

namespace Fuse.Cli.Services;

/// <summary>
///     A console UI implementation that writes all output to stderr.
/// </summary>
/// <remarks>
///     <para>
///         This is used in MCP server mode where stdout is reserved for JSON-RPC communication.
///         Any console output (progress messages, errors, etc.) must go to stderr to avoid
///         corrupting the MCP protocol stream.
///     </para>
/// </remarks>
public sealed class StderrConsoleUI : IConsoleUI
{
    /// <inheritdoc />
    public void WriteSuccess(string message)
    {
        Console.Error.WriteLine($"  [OK] {message}");
    }

    /// <inheritdoc />
    public void WriteError(string message)
    {
        Console.Error.WriteLine($"  [ERR] {message}");
    }

    /// <inheritdoc />
    public void WriteStep(string message)
    {
        Console.Error.WriteLine($"  {message}");
    }

    /// <inheritdoc />
    public void WriteResult(string message)
    {
        Console.Error.WriteLine($"  {message}");
    }
}
