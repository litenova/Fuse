using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Fuse.Cli.Infrastructure;

namespace Fuse.Cli.Commands;

[Command(Description = "A tool to combine and process files in a directory.")]
public class RootCommand : ICommand
{
    // Common base options
    [CommandOption("directory", 'd', Description = "Path to the directory to process.")]
    public string SourceDirectory { get; set; } = Directory.GetCurrentDirectory();

    [CommandOption("output", 'o', Description = "Path to the output directory where the combined file will be saved.")]
    public string OutputDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // Project template
    [CommandOption("template", 't', Description = "Project template to use.")]
    public ProjectTemplate? Template { get; set; }

    // File extensions
    [CommandOption("include-extensions", Description = "Comma-separated list of file extensions to include in the processing.")]
    public string? IncludeExtensionsString { get; set; }

    // Exclude directories
    [CommandOption("exclude-directories", Description = "Comma-separated list of directories to exclude from processing.")]
    public string? ExcludeDirectoriesString { get; set; }

    // Exclude extensions
    [CommandOption("exclude-extensions", Description = "Comma-separated list of file extensions to exclude from processing.")]
    public string? ExcludeExtensionsString { get; set; }

    // Output filename
    [CommandOption("name", 'n', Description = "Name of the output file (without extension).")]
    public string? OutputFileName { get; set; }

    // Overwrite option
    [CommandOption("overwrite", 'w', Description = "Whether to overwrite the output file if it already exists.")]
    public bool Overwrite { get; set; } = true;

    // Recursive option
    [CommandOption("recursive", 'r', Description = "Whether to search recursively through subdirectories.")]
    public bool Recursive { get; set; } = true;

    // Trim content option
    [CommandOption("trim", Description = "Whether to trim leading and trailing whitespace from each line in the file contents.")]
    public bool TrimContent { get; set; } = true;

    // Max file size option
    [CommandOption("max-file-size", Description = "Maximum file size in KB to process. Files larger than this will be skipped. Set to 0 for unlimited size.")]
    public int MaxFileSizeKB { get; set; } = 10240;

    // Ignore binary files option
    [CommandOption("ignore-binary", Description = "Whether to ignore binary files.")]
    public bool IgnoreBinaryFiles { get; set; } = true;

    // Include metadata option
    [CommandOption("include-metadata", Description = "Whether to include file metadata in the output file.")]
    public bool IncludeMetadata { get; set; } = false;

    // Condensing option
    [CommandOption("condense", Description = "Whether to apply line condensing to the output file.")]
    public bool UseCondensing { get; set; } = true;

    // Standard processing implementation
    public virtual async ValueTask ExecuteAsync(IConsole console)
    {
        // Create a logger that uses the CliFx console
        var logger = new CliFxLogger<FuseService>(console);

        // Create options
        var options = CreateOptions();

        await console.Output.WriteLineAsync($"Processing files from: {SourceDirectory}");
        await console.Output.WriteLineAsync($"Template: {options.Template?.ToString() ?? "Generic"}");

        var startTime = DateTime.Now;
        var service = new FuseService(options, logger);
        await service.FuseAsync();

        var duration = DateTime.Now - startTime;
        var outputFile = new FileInfo(Path.Combine(options.OutputDirectory,
            options.OutputFileName ?? $"Fuse_{Path.GetFileName(options.SourceDirectory)}_{DateTime.Now:yyyyMMddHHmmss}.txt"));

        if (outputFile.Exists)
        {
            await console.Output.WriteLineAsync($"\nCompleted in {duration.TotalSeconds:F1}s");
            await console.Output.WriteLineAsync($"Output: {outputFile.FullName}");
            await console.Output.WriteLineAsync($"Size: {FormatFileSize(outputFile.Length)}");
        }
        else
        {
            await console.Output.WriteLineAsync("\nOperation completed but no output file was generated.");
            await console.Output.WriteLineAsync($"Elapsed time: {duration.TotalSeconds:F1}s");
        }
    }

    // Helper method to create FuseOptions object - virtual so it can be overridden
    protected virtual FuseOptions CreateOptions()
    {
        // Parse string options into arrays
        string[]? includeExtensions = IncludeExtensionsString?.Split(',', StringSplitOptions.RemoveEmptyEntries);
        string[]? excludeDirectories = ExcludeDirectoriesString?.Split(',', StringSplitOptions.RemoveEmptyEntries);
        string[]? excludeExtensions = ExcludeExtensionsString?.Split(',', StringSplitOptions.RemoveEmptyEntries);

        return new FuseOptions
        {
            SourceDirectory = SourceDirectory,
            OutputDirectory = OutputDirectory,
            Template = Template,
            IncludeExtensions = includeExtensions,
            ExcludeDirectories = excludeDirectories,
            ExcludeExtensions = excludeExtensions,
            OutputFileName = OutputFileName,
            Overwrite = Overwrite,
            Recursive = Recursive,
            TrimContent = TrimContent,
            MaxFileSizeKB = MaxFileSizeKB,
            IgnoreBinaryFiles = IgnoreBinaryFiles,
            IncludeMetadata = IncludeMetadata,
            UseCondensing = UseCondensing
        };
    }

    // Helper method to format file sizes
    protected static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:F2} {sizes[order]}";
    }
}