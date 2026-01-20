// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using DotMake.CommandLine;
using Fuse.Cli;
using Fuse.Cli.Commands;
using Fuse.Engine;
using Fuse.Engine.FileSystem;
using Fuse.Engine.Git;
using Fuse.Engine.Services;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

// ============================================================================
// FUSE CLI APPLICATION ENTRY POINT
// ============================================================================
// This file configures the dependency injection container and bootstraps the
// CLI application using DotMake.CommandLine.
//
// The application follows a clean architecture with:
// - Presentation Layer: Fuse.Cli and Fuse.Commands (CLI commands and options)
// - Application Layer: Fuse.Engine (orchestration and services)
// - Domain Layer: Fuse.Core (entities and abstractions)
// - Infrastructure: Fuse.Minifiers (file processing)
// ============================================================================

// ===== Step 1: Configure Dependency Injection =====
// Register all services with the DI container used by DotMake.CommandLine
Cli.Ext.ConfigureServices(services =>
{
    // ----- Presentation Layer -----
    // Register the Spectre.Console instance for rich terminal output
    services.AddSingleton<IAnsiConsole>(AnsiConsole.Console);

    // ----- Engine and Services -----
    // Register the main fusion engine as singleton (stateless)
    services.AddSingleton<FuseEngine>();

    // Register all engine services implementing the single responsibility principle
    services.AddSingleton<IConfigurationResolver, ConfigurationResolver>();
    services.AddSingleton<IFileCollector, FileCollector>();
    services.AddSingleton<IContentProcessor, ContentProcessor>();
    services.AddSingleton<IOutputBuilder, OutputBuilder>();

    // ----- Engine Dependencies -----
    // File system and Git support services
    services.AddSingleton<PhysicalFileSystem>();
    services.AddSingleton<GitIgnoreParser>();

    // ----- CLI Commands -----
    // Register all command classes as transient (new instance per invocation)
    services.AddTransient<FuseCliCommand>();
    services.AddTransient<DotNetCommand>();
    services.AddTransient<AzureDevOpsWikiCommand>();

    // NOTE: Add additional command registrations here as new commands are created
    // Example: services.AddTransient<PythonCommand>();
});

// ===== Step 2: Run the Application =====
// Execute the CLI with the root command type
// DotMake.CommandLine handles:
// - Argument parsing
// - Help text generation
// - Subcommand routing
// - Error handling and exit codes
await Cli.RunAsync<FuseCliCommand>(args);