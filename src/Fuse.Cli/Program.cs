using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fuse.Cli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddConsole())
            .AddTransient<FuseCommand>()
            .BuildServiceProvider();

        var logger = services.GetRequiredService<ILogger<FuseService>>();
        var fuseCommand = new FuseCommand(logger);
        var rootCommand = fuseCommand.CreateRootCommand();

        return await rootCommand.InvokeAsync(args);
    }
}