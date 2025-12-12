using DotMake.CommandLine;
using Fuse.Cli;
using Fuse.Core.Abstractions;
using Fuse.Infrastructure.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

// 1. Configure Dependency Injection
// The source generator in DotMake.CommandLine creates the `Cli.Ext` class
// when it detects a reference to Microsoft.Extensions.DependencyInjection.
Cli.Ext.ConfigureServices(services =>
{
    services.AddSingleton<IAnsiConsole>(AnsiConsole.Console);

    // Register our application services
    services.AddSingleton<IFuseService, FuseService>();
    services.AddSingleton<IFileSystem, PhysicalFileSystem>();

    // IMPORTANT: Register the command class itself with the DI container.
    // DotMake.CommandLine will resolve it and its dependencies automatically.
    services.AddTransient<FuseCliCommand>();
});

// 2. Run the application
// Pass the command class as the generic type parameter.
// DotMake.CommandLine handles the rest: creating the command, injecting dependencies,
// parsing arguments, binding properties, and calling the RunAsync handler.
await Cli.RunAsync<FuseCliCommand>(args);