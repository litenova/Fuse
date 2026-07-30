using DotMake.CommandLine;
using Fuse.Cli;
using Fuse.Cli.Commands;
using Fuse.Cli.Extensions;
using Fuse.Cli.Services;
using Microsoft.Extensions.DependencyInjection;

Cli.Ext.ConfigureServices(services =>
{
    services.AddSingleton<IConsoleUI, ConsoleUI>();
    services.AddFuse();

    services.AddTransient<FuseCliCommand>();
    services.AddTransient<IndexCommand>();
    services.AddTransient<IndexStatusCommand>();
    services.AddTransient<IndexCancelCommand>();
    services.AddTransient<IndexCleanCommand>();
    services.AddTransient<MapCommand>();
    services.AddTransient<ContextCommand>();
    services.AddTransient<ReviewCommand>();
    services.AddTransient<ImpactCommand>();
    services.AddTransient<DiagnosticsCommand>();
    services.AddTransient<DoctorCommand>();
    services.AddTransient<FindCommand>();
    services.AddTransient<InitCommand>();
    services.AddTransient<McpCommand>();
    services.AddTransient<InstallCommand>();
    services.AddSingleton<McpInstallService>();
    services.AddSingleton<McpDoctorService>();
    services.AddTransient<McpDoctorCommand>();
    services.AddTransient<McpServeCommand>();
    services.AddTransient<ReduceCommand>();
});

await Cli.RunAsync<FuseCliCommand>(args);
