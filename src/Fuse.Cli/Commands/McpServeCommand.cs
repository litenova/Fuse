// -----------------------------------------------------------------------
// <copyright file="McpServeCommand.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Core.Abstractions;
using Fuse.Engine;
using Fuse.Engine.FileSystem;
using Fuse.Engine.Git;
using Fuse.Engine.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Commands;

/// <summary>
///     CLI command that starts the Fuse MCP (Model Context Protocol) server.
/// </summary>
/// <remarks>
///     <para>
///         This command is invoked via <c>fuse serve</c> (or <c>fuse mcp</c>).
///         It starts a persistent process that communicates via stdio using the
///         JSON-RPC based Model Context Protocol, allowing AI agents to invoke
///         Fuse programmatically.
///     </para>
///     <para>
///         When running in MCP mode:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>stdout is reserved for JSON-RPC messages</description>
///         </item>
///         <item>
///             <description>All logging and console output is redirected to stderr</description>
///         </item>
///         <item>
///             <description>The server exposes tools and resources for codebase fusion</description>
///         </item>
///     </list>
/// </remarks>
/// <example>
///     <code>
/// fuse serve                  # Start MCP server on stdio
/// </code>
/// </example>
[CliCommand(
    Name = "serve",
    Description = "Start the Fuse MCP server for AI agent integration. Communicates via stdio using the Model Context Protocol.",
    Parent = typeof(FuseCliCommand))]
public sealed class McpServeCommand
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="McpServeCommand" /> class.
    /// </summary>
    /// <remarks>
    ///     Parameterless constructor required by DotMake.CommandLine source generator.
    /// </remarks>
    public McpServeCommand()
    {
    }

    /// <summary>
    ///     Executes the MCP server, listening for requests on stdin and responding on stdout.
    /// </summary>
    /// <param name="context">The CLI context containing cancellation token and other metadata.</param>
    /// <returns>A task that completes when the server shuts down.</returns>
    public async Task RunAsync(CliContext context)
    {
        // Build a .NET Generic Host with the MCP server configured
        var builder = Host.CreateApplicationBuilder();

        // ===== Configure Logging =====
        // All logs must go to stderr; stdout is reserved for MCP JSON-RPC
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // ===== Register Fuse Engine Services =====
        // Use StderrConsoleUI to avoid polluting stdout
        builder.Services.AddSingleton<IConsoleUI, StderrConsoleUI>();
        builder.Services.AddSingleton<IConfigurationResolver, ConfigurationResolver>();
        builder.Services.AddSingleton<IFileCollector, FileCollector>();
        builder.Services.AddSingleton<IContentProcessor, ContentProcessor>();
        builder.Services.AddSingleton<IOutputBuilder, OutputBuilder>();
        builder.Services.AddSingleton<PhysicalFileSystem>();
        builder.Services.AddSingleton<GitIgnoreParser>();
        builder.Services.AddSingleton<FuseEngine>();

        // ===== Configure MCP Server =====
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "fuse",
                    Version = "1.0.0"
                };
                options.ServerInstructions =
                    "Fuse is a codebase context optimizer. Use the 'get_optimized_context' tool " +
                    "to generate minified, token-efficient snapshots of project directories. " +
                    "You can also read fuse:// resources to get optimized views of codebases.";
            })
            .WithStdioServerTransport()
            .WithTools<FuseTools>()
            .WithResources<FuseResources>();

        // ===== Run =====
        await builder.Build().RunAsync(context.CancellationToken);
    }
}
