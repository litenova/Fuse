// src/Fuse.Cli/Program.cs

using CliFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fuse.Cli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Configure services
        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddConsole())
            .BuildServiceProvider();

        // Run the application with CliFx
        return await new CliApplicationBuilder()
            .AddCommandsFromThisAssembly()
            .UseTypeActivator(type => ActivatorUtilities.CreateInstance(services, type))
            .Build()
            .RunAsync(args);
    }
}