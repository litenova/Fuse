using System.Diagnostics.CodeAnalysis;

namespace Fuse.Cli;

/// <summary>
/// Represents the various options for the Fuse tool.
/// </summary>
[SuppressMessage("ReSharper", "InconsistentNaming")]
public sealed class FuseOptions
{
    /// <summary>
    /// Gets or sets the path to the directory to process.
    /// </summary>
    public string SourceDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// Gets or sets the path to the output directory.
    /// </summary>
    public string OutputDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>
    /// Gets or sets the optional project template to use.
    /// </summary>
    public ProjectTemplate? Template { get; init; }

    /// <summary>
    /// Gets or sets the custom file extensions to include.
    /// </summary>
    public string[]? IncludeExtensions { get; init; }

    /// <summary>
    /// Gets or sets the directories to exclude from processing.
    /// </summary>
    public string[]? ExcludeDirectories { get; init; }

    /// <summary>
    /// Gets or sets the name of the output file (without extension).
    /// </summary>
    public string? OutputFileName { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether to overwrite the output file if it exists.
    /// </summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to search recursively through subdirectories.
    /// </summary>
    public bool Recursive { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to trim whitespace from file contents.
    /// </summary>
    public bool TrimContent { get; init; } = true;

    /// <summary>
    /// Gets or sets the maximum file size in KB to process (0 for unlimited).
    /// </summary>
    public int MaxFileSizeKB { get; init; } = 0;

    /// <summary>
    /// Gets or sets a value indicating whether to ignore binary files.
    /// </summary>
    public bool IgnoreBinaryFiles { get; init; } = true;

    /// <summary>
    /// Indicates whether to perform aggressive minification.
    /// </summary>
    public bool AggressiveMinification { get; init; } = true;

    /// <summary>
    /// Indicates whether to include metadata in the output.
    /// </summary>
    public bool IncludeMetadata { get; init; } = false;

    /// <summary>
    /// Indicates whether to use condensing.
    /// </summary>
    public bool UseCondensing { get; init; } = true;

    /// <summary>
    /// Removes all using statements from C# files.
    /// </summary>
    public bool RemoveAllUsings { get; init; } = false;

    /// <summary>
    /// Removes the namespace declaration from C# files.
    /// </summary>
    public bool RemoveNamespaceDeclaration { get; init; } = false;
}