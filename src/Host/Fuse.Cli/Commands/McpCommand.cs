using DotMake.CommandLine;
using Fuse.Cli.Services;

namespace Fuse.Cli.Commands;

/// <summary>
///     Parent group for the Model Context Protocol surface: <c>fuse mcp install</c> registers Fuse with an AI
///     client, <c>fuse mcp doctor</c> inspects that connection, and <c>fuse mcp serve</c> is the stdio server the
///     client launches.
/// </summary>
[CliCommand(
    Name = "mcp",
    Description = "Manage the Fuse Model Context Protocol server: install, doctor, and serve.",
    Parent = typeof(FuseCliCommand))]
public sealed class McpCommand
{
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="McpCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>
    ///     Used by DotMake.CommandLine to bind options; the console UI is <see langword="null" />, so this instance
    ///     must not run.
    /// </remarks>
    public McpCommand() : this(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="McpCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for status output.</param>
    public McpCommand(IConsoleUI consoleUI)
    {
        _consoleUI = consoleUI;
    }

    /// <summary>
    ///     Points the user at the <c>install</c>, <c>doctor</c>, and <c>serve</c> subcommands when <c>fuse mcp</c>
    ///     is run bare.
    /// </summary>
    /// <param name="context">The CLI invocation context.</param>
    public void Run(CliContext context)
    {
        _consoleUI.WriteResult(
            "Use a subcommand: 'fuse mcp install' to register Fuse with an AI client, "
            + "'fuse mcp doctor' to inspect that connection, or 'fuse mcp serve' to run the stdio server.");
    }
}
