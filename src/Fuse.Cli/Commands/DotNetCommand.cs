using CliFx.Attributes;
using CliFx.Infrastructure;

namespace Fuse.Cli.Commands;

[Command("dotnet", Description = "Process .NET projects with specialized options.")]
public class DotNetCommand : RootCommand
{
    // .NET specific options
    [CommandOption("remove-namespaces", Description = "Remove namespace declarations from C# files.")]
    public bool RemoveNamespaces { get; set; } = false;

    [CommandOption("remove-comments", Description = "Remove comments from C# files.")]
    public bool RemoveComments { get; set; } = false;

    [CommandOption("remove-regions", Description = "Remove #region and #endregion directives from C# files.")]
    public bool RemoveRegions { get; set; } = false;

    [CommandOption("remove-usings", Description = "Remove all using statements from C# files.")]
    public bool RemoveUsings { get; set; } = false;

    [CommandOption("minify-xml", Description = "Minify XML files, including .csproj files.")]
    public bool MinifyXml { get; set; } = true;

    [CommandOption("aggressive-minify", Description = "Aggressively reduce C# code size while maintaining logic.")]
    public bool AggressiveMinify { get; set; } = false;

    [CommandOption("minify-razor", Description = "Minify HTML and Razor syntax in .cshtml and .razor files.")]
    public bool MinifyRazor { get; set; } = true;

    [CommandOption("all", Description = "Apply all C# minification options.")]
    public bool ApplyAllOptions { get; set; } = false;

    // Override the base ExecuteAsync to add .NET specific messaging
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        await console.Output.WriteLineAsync("Processing .NET project...");
        if (ApplyAllOptions)
        {
            await console.Output.WriteLineAsync("Using comprehensive C# minification");
        }

        // Call the base class implementation to handle the actual processing
        await base.ExecuteAsync(console);
    }

    // Override the CreateOptions method to add .NET specific options
    protected override FuseOptions CreateOptions()
    {
        // Get the base options from the parent class
        var options = base.CreateOptions();

        // Always set template to DotNet
        options.Template = ProjectTemplate.DotNet;

        // Set .NET specific options
        options.RemoveCSharpNamespaceDeclarations = RemoveNamespaces || ApplyAllOptions;
        options.RemoveCSharpComments = RemoveComments || ApplyAllOptions;
        options.RemoveCSharpRegions = RemoveRegions || ApplyAllOptions;
        options.RemoveCSharpUsings = RemoveUsings || ApplyAllOptions;
        options.MinifyXmlFiles = MinifyXml;
        options.AggressiveCSharpReduction = AggressiveMinify || ApplyAllOptions;
        options.MinifyHtmlAndRazor = MinifyRazor;
        options.ApplyAllOptions = ApplyAllOptions;

        return options;
    }
}