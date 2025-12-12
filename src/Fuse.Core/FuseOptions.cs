namespace Fuse.Core;

public sealed record FuseOptions
{
    public string SourceDirectory { get; init; } = Directory.GetCurrentDirectory();

    public string OutputDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public ProjectTemplate? Template { get; init; }

    public string[]? IncludeExtensions { get; init; }

    public string[]? ExcludeExtensions { get; init; }

    public string[]? ExcludeDirectories { get; init; }

    public string? OutputFileName { get; init; }

    public bool Overwrite { get; init; } = true;

    public bool Recursive { get; init; } = true;

    public bool TrimContent { get; init; } = true;

    public int MaxFileSizeKB { get; init; } = 0;

    public bool IgnoreBinaryFiles { get; init; } = true;

    public bool IncludeMetadata { get; init; } = false;

    public bool UseCondensing { get; init; } = true;

    public bool RemoveCSharpNamespaceDeclarations { get; init; } = false;

    public bool RemoveCSharpComments { get; init; } = false;

    public bool RemoveCSharpRegions { get; init; } = false;

    public bool RemoveCSharpUsings { get; init; } = false;

    public bool MinifyXmlFiles { get; init; } = true;

    public bool MinifyHtmlAndRazor { get; init; } = true;

    public bool AggressiveCSharpReduction { get; init; } = false;

    public bool ApplyAllOptions { get; init; }

    public bool ExcludeTestProjects { get; init; } = false;

    public bool RespectGitIgnore { get; init; } = true;

    public int? MaxTokens { get; init; }

    public bool ShowTokenCount { get; init; }
}