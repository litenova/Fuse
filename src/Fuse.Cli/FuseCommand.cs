using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;

namespace Fuse.Cli;

public sealed class FuseCommand(ILogger<FuseService> logger)
{
    private readonly Option<DirectoryInfo> _directoryOption = new(
        ["--directory", "-d"],
        () => new DirectoryInfo(Directory.GetCurrentDirectory()),
        "Path to the directory to process.")
    {
        IsRequired = true
    };

    private readonly Option<DirectoryInfo> _outputOption = new(
        ["--output", "-o"],
        () => new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        "Path to the output directory where the combined file will be saved.")
    {
        IsRequired = true
    };

    private readonly Option<ProjectTemplate?> _templateOption = new(
        ["--template", "-t"],
        "Project template to use.");

    private readonly Option<string?> _extensionsOption = new(
        ["--extensions", "-e"],
        "Comma-separated list of file extensions to include in the processing.");

    private readonly Option<string?> _excludeOption = new(
        ["--exclude", "-x"],
        "Comma-separated list of directories to exclude from processing.");

    private readonly Option<string?> _nameOption = new(
        ["--name", "-n"],
        "Name of the output file (without extension).");

    private readonly Option<bool> _overwriteOption = new(
        ["--overwrite", "-w"],
        () => true,
        "Whether to overwrite the output file if it already exists.");

    private readonly Option<bool> _recursiveOption = new(
        ["--recursive", "-r"],
        () => true,
        "Whether to search recursively through subdirectories.");

    private readonly Option<bool> _trimOption = new(
        "--trim",
        () => true,
        "Whether to trim leading and trailing whitespace from each line in the file contents.");

    private readonly Option<int> _maxFileSizeOption = new(
        "--max-file-size",
        () => 10240,
        "Maximum file size in KB to process. Files larger than this will be skipped. Set to 0 for unlimited size.");

    private readonly Option<bool> _ignoreBinaryOption = new(
        "--ignore-binary",
        () => true,
        "Whether to ignore binary files.");

    private readonly Option<bool> _includeMetadataOption = new(
        "--include-metadata",
        () => false,
        "Whether to include file metadata in the output file.");

    private readonly Option<bool> _condensingOption = new(
        "--condense",
        () => true,
        "Whether to apply line condensing to the output file.");

    private readonly Option<bool> _removeCSharpNamespaceDeclarations = new("--remove-csharp-namespaces", () => false, "Remove namespace declarations from C# files.");
    private readonly Option<bool> _removeCSharpCommentsOption = new("--remove-csharp-comments", () => false, "Remove comments from C# files.");
    private readonly Option<bool> _removeCSharpRegionsOption = new("--remove-csharp-regions", () => false, "Remove #region and #endregion directives from C# files.");
    private readonly Option<bool> _removeCSharpUsingsOption = new("--remove-csharp-usings", () => false, "Remove all using statements from C# files.");
    private readonly Option<bool> _minifyXmlFilesOption = new("--minify-xml-files", () => false, "Minify XML files, including .csproj files.");
    private readonly Option<bool> _aggressiveCSharpReductionOption = new("--aggressive-csharp-reduction", () => false, "Aggressively reduce C# code size while maintaining logic.");
    private readonly Option<bool> _minifyHtmlAndRazorOption = new("--minify-html-razor", () => false, "Minify HTML and Razor syntax in .cshtml and .razor files.");
    private readonly Option<bool> _comprehensiveCSharpMinificationOption = new("--comprehensive-csharp-minify", () => false, "Apply all C# minification options.");

    public RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("Fuse - A tool to combine and process files in a directory.");

        rootCommand.AddOption(_directoryOption);
        rootCommand.AddOption(_outputOption);
        rootCommand.AddOption(_templateOption);
        rootCommand.AddOption(_extensionsOption);
        rootCommand.AddOption(_excludeOption);
        rootCommand.AddOption(_nameOption);
        rootCommand.AddOption(_overwriteOption);
        rootCommand.AddOption(_recursiveOption);
        rootCommand.AddOption(_trimOption);
        rootCommand.AddOption(_maxFileSizeOption);
        rootCommand.AddOption(_ignoreBinaryOption);
        rootCommand.AddOption(_includeMetadataOption);
        rootCommand.AddOption(_condensingOption);
        rootCommand.AddOption(_removeCSharpNamespaceDeclarations);
        rootCommand.AddOption(_removeCSharpCommentsOption);
        rootCommand.AddOption(_removeCSharpRegionsOption);
        rootCommand.AddOption(_removeCSharpUsingsOption);
        rootCommand.AddOption(_minifyXmlFilesOption);
        rootCommand.AddOption(_aggressiveCSharpReductionOption);
        rootCommand.AddOption(_minifyHtmlAndRazorOption);
        rootCommand.AddOption(_comprehensiveCSharpMinificationOption);

        rootCommand.SetHandler(ExecuteAsync);

        return rootCommand;
    }

    private async Task ExecuteAsync(InvocationContext context)
    {
        var sourceDirectory = context.ParseResult.GetValueForOption(_directoryOption);
        var outputDirectory = context.ParseResult.GetValueForOption(_outputOption);

        if (sourceDirectory == null || outputDirectory == null)
        {
            throw new InvalidOperationException("Source and output directories are required.");
        }

        var options = new FuseOptions
        {
            SourceDirectory = sourceDirectory.FullName,
            OutputDirectory = outputDirectory.FullName,
            Template = context.ParseResult.GetValueForOption(_templateOption),
            IncludeExtensions = context.ParseResult.GetValueForOption(_extensionsOption)?.Split(',', StringSplitOptions.RemoveEmptyEntries),
            ExcludeDirectories = context.ParseResult.GetValueForOption(_excludeOption)?.Split(',', StringSplitOptions.RemoveEmptyEntries),
            OutputFileName = context.ParseResult.GetValueForOption(_nameOption),
            Overwrite = context.ParseResult.GetValueForOption(_overwriteOption),
            Recursive = context.ParseResult.GetValueForOption(_recursiveOption),
            TrimContent = context.ParseResult.GetValueForOption(_trimOption),
            MaxFileSizeKB = context.ParseResult.GetValueForOption(_maxFileSizeOption),
            IgnoreBinaryFiles = context.ParseResult.GetValueForOption(_ignoreBinaryOption),
            IncludeMetadata = context.ParseResult.GetValueForOption(_includeMetadataOption),
            UseCondensing = context.ParseResult.GetValueForOption(_condensingOption),
            RemoveCSharpNamespaceDeclarations = context.ParseResult.GetValueForOption(_removeCSharpNamespaceDeclarations),
            RemoveCSharpComments = context.ParseResult.GetValueForOption(_removeCSharpCommentsOption),
            RemoveCSharpRegions = context.ParseResult.GetValueForOption(_removeCSharpRegionsOption),
            RemoveCSharpUsings = context.ParseResult.GetValueForOption(_removeCSharpUsingsOption),
            MinifyXmlFiles = context.ParseResult.GetValueForOption(_minifyXmlFilesOption),
            AggressiveCSharpReduction = context.ParseResult.GetValueForOption(_aggressiveCSharpReductionOption),
            MinifyHtmlAndRazor = context.ParseResult.GetValueForOption(_minifyHtmlAndRazorOption),
            ComprehensiveCSharpMinification = context.ParseResult.GetValueForOption(_comprehensiveCSharpMinificationOption)
        };

        if (options.ComprehensiveCSharpMinification)
        {
            options.RemoveCSharpNamespaceDeclarations = true;
            options.RemoveCSharpComments = true;
            options.RemoveCSharpRegions = true;
            options.RemoveCSharpUsings = true;
            options.AggressiveCSharpReduction = true;
        }

        Console.WriteLine("Starting Fuse process...");

        var processor = new FuseService(options, logger);
        await processor.FuseAsync();

        Console.WriteLine("Process completed successfully!");

        PrintSummary(options);
    }

    private static void PrintSummary(FuseOptions options)
    {
        Console.WriteLine("\nSummary of options used:");
        Console.WriteLine($"Source Directory: {options.SourceDirectory}");
        Console.WriteLine($"Output Directory: {options.OutputDirectory}");
        Console.WriteLine($"Template: {options.Template}");
        Console.WriteLine($"Include Extensions: {(options.IncludeExtensions != null ? string.Join(", ", options.IncludeExtensions) : "Default")}");
        Console.WriteLine($"Exclude Directories: {(options.ExcludeDirectories != null ? string.Join(", ", options.ExcludeDirectories) : "None")}");
        Console.WriteLine($"Output File Name: {options.OutputFileName ?? "Auto-generated"}");
        Console.WriteLine($"Overwrite: {options.Overwrite}");
        Console.WriteLine($"Recursive: {options.Recursive}");
        Console.WriteLine($"Trim Content: {options.TrimContent}");
        Console.WriteLine($"Max File Size (KB): {options.MaxFileSizeKB}");
        Console.WriteLine($"Ignore Binary Files: {options.IgnoreBinaryFiles}");
    }
}