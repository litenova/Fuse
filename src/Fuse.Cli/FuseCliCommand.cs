using DotMake.CommandLine;
using Fuse.Core;
using Fuse.Core.Abstractions;
using Spectre.Console;

namespace Fuse.Cli;

/// <summary>
/// A flexible file combining tool for developers.
/// This class defines the root command, its options, and the execution logic using DotMake.CommandLine.
/// </summary>
[CliCommand(Description = "A flexible file combining tool for developers.")]
public sealed class FuseCliCommand
{
    private readonly IFuseService _fuseService;
    private readonly IAnsiConsole _console; // Change from ILogger to IAnsiConsole

    public FuseCliCommand(IFuseService fuseService, IAnsiConsole console)
    {
        _fuseService = fuseService;
        _console = console;
    }

    #region Options & Arguments

    [CliOption(Description = "Path to the directory to process.")]
    public string Directory { get; set; } = System.IO.Directory.GetCurrentDirectory();

    [CliOption(Description = "Path to the output directory.")]
    public string Output { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    [CliOption(Description = "Project template for default file types and exclusions.", Required = false)]
    public ProjectTemplate? Template { get; set; } = null;

    [CliOption(Description = "Comma-separated list of file extensions to include.", Required = false)]
    public string[]? IncludeExtensions { get; set; } = null;

    [CliOption(Description = "Comma-separated list of file extensions to exclude.", Required = false)]
    public string[]? ExcludeExtensions { get; set; } = null;

    [CliOption(Description = "Comma-separated list of directories to exclude.", Required = false)]
    public string[]? ExcludeDirectories { get; set; } = null;

    [CliOption(Name = "name", Description = "Name of the output file (without extension).", Required = false)]
    public string? OutputFileName { get; set; } = null;

    [CliOption(Description = "Overwrite the output file if it exists.")]
    public bool Overwrite { get; set; } = true;

    [CliOption(Description = "Search recursively through subdirectories.")]
    public bool Recursive { get; set; } = true;

    [CliOption(Name = "trim", Description = "Trim leading/trailing whitespace from each line.")]
    public bool TrimContent { get; set; } = true;

    [CliOption(Description = "Maximum file size in KB to process (0 for unlimited).")]
    public int MaxFileSize { get; set; } = 0;

    [CliOption(Description = "Ignore binary files.")]
    public bool IgnoreBinary { get; set; } = true;

    [CliOption(Description = "Include file metadata (size, dates) in the output.")]
    public bool IncludeMetadata { get; set; } = false;

    [CliOption(Name = "condense", Description = "Remove empty lines from the final output file.")]
    public bool UseCondensing { get; set; } = true;

    [CliOption(Description = "Exclude common test project directories.")]
    public bool ExcludeTestProjects { get; set; } = false;

    [CliOption(Description = "Remove namespace declarations from C# files.")]
    public bool RemoveCSharpNamespaces { get; set; } = false;

    [CliOption(Description = "Remove comments from C# files.")]
    public bool RemoveCSharpComments { get; set; } = false;

    [CliOption(Description = "Remove #region directives from C# files.")]
    public bool RemoveCSharpRegions { get; set; } = false;

    [CliOption(Description = "Remove all using statements from C# files.")]
    public bool RemoveCSharpUsings { get; set; } = false;

    [CliOption(Description = "Aggressively reduce C# code size.")]
    public bool AggressiveCSharpReduction { get; set; } = false;

    [CliOption(Name = "all-csharp-options", Description = "Apply all C# minification options.")]
    public bool ApplyAllOptions { get; set; } = false;

    [CliOption(Description = "Minify XML files (.csproj, .xml, etc.).")]
    public bool MinifyXmlFiles { get; set; } = true;

    [CliOption(Description = "Minify HTML and Razor files.")]
    public bool MinifyHtmlAndRazor { get; set; } = true;

    [CliOption(Description = "Respect rules from .gitignore files found in the directory tree.")]
    public bool RespectGitIgnore { get; set; } = true;

    // Add these properties within the #region Options & Arguments
    [CliOption(Description = "Stops processing when token count is reached. Useful for context window limits.", Required = false)]
    public int? MaxTokens { get; set; } = null;

    [CliOption(Description = "Displays the final estimated token count upon completion.")]
    public bool ShowTokenCount { get; set; } = true;

    #endregion

    public async Task RunAsync(CliContext context)
    {
        var options = new FuseOptions
        {
            SourceDirectory = this.Directory,
            OutputDirectory = this.Output,
            Template = this.Template,
            IncludeExtensions = this.IncludeExtensions,
            ExcludeExtensions = this.ExcludeExtensions,
            ExcludeDirectories = this.ExcludeDirectories,
            OutputFileName = this.OutputFileName,
            Overwrite = this.Overwrite,
            Recursive = this.Recursive,
            TrimContent = this.TrimContent,
            MaxFileSizeKB = this.MaxFileSize,
            IgnoreBinaryFiles = this.IgnoreBinary,
            IncludeMetadata = this.IncludeMetadata,
            UseCondensing = this.UseCondensing,
            ExcludeTestProjects = this.ExcludeTestProjects,
            RemoveCSharpNamespaceDeclarations = this.RemoveCSharpNamespaces || this.ApplyAllOptions,
            RemoveCSharpComments = this.RemoveCSharpComments || this.ApplyAllOptions,
            RemoveCSharpRegions = this.RemoveCSharpRegions || this.ApplyAllOptions,
            RemoveCSharpUsings = this.RemoveCSharpUsings || this.ApplyAllOptions,
            AggressiveCSharpReduction = this.AggressiveCSharpReduction || this.ApplyAllOptions,
            ApplyAllOptions = this.ApplyAllOptions,
            MinifyXmlFiles = this.MinifyXmlFiles,
            MinifyHtmlAndRazor = this.MinifyHtmlAndRazor,
            RespectGitIgnore = this.RespectGitIgnore,
            MaxTokens = this.MaxTokens,
            ShowTokenCount = this.ShowTokenCount
        };

        var startTime = DateTime.Now;
        _console.Write(new Rule());

        // CORRECTED: Create a compact table without expanding.
        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn(new TableColumn("").NoWrap());
        table.AddColumn(new TableColumn(""));
        table.AddRow("[bold]Source:[/]", options.SourceDirectory);
        table.AddRow("[bold]Output Dir:[/]", options.OutputDirectory);
        table.AddRow("[bold]Template:[/]", options.Template?.ToString() ?? "[grey]None[/]");
        _console.Write(table);

        // The service now handles its own output, so we just call it.
        await _fuseService.FuseAsync(options, context.CancellationToken);

        var duration = DateTime.Now - startTime;

        // Create a title for the rule that includes the duration.
        // Using [dim] makes the time less prominent than the main title.
        var ruleTitle = $"[bold green]Operation Complete[/] [dim]({duration.TotalSeconds:F1}s)[/]";

        // Write the new rule and remove the separate MarkupLine call.
        _console.Write(new Rule(ruleTitle).LeftJustified());
    }
}