// -----------------------------------------------------------------------
// <copyright file="IConsoleUI.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Fuse.Core.Abstractions;

/// <summary>
///     Defines the contract for console user interface operations.
/// </summary>
/// <remarks>
///     <para>
///         This interface abstracts console output operations, allowing for
///         testable and replaceable console implementations.
///     </para>
/// </remarks>
public interface IConsoleUI
{
    /// <summary>
    ///     Writes a success message to the console.
    /// </summary>
    /// <param name="message">The success message to display.</param>
    void WriteSuccess(string message);

    /// <summary>
    ///     Writes an error message to the console.
    /// </summary>
    /// <param name="message">The error message to display.</param>
    void WriteError(string message);

    /// <summary>
    ///     Writes a step or progress message to the console.
    /// </summary>
    /// <param name="message">The step message to display.</param>
    void WriteStep(string message);

    /// <summary>
    ///     Writes a result message to the console (e.g., output paths, stats).
    /// </summary>
    /// <param name="message">The result message to display.</param>
    void WriteResult(string message);
}
