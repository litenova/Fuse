using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using DotNet.Globbing;
using Fuse.Cli.Utils;
using Fuse.Core;
using Fuse.Core.Abstractions;
using Fuse.Infrastructure;
using Fuse.Infrastructure.Git;
using Fuse.Infrastructure.Minifiers;
using Spectre.Console;
using TiktokenSharp;

namespace Fuse.Cli;

/// <summary>
/// Implements the core logic for the Fuse tool, orchestrating file discovery,
/// processing, minification, and combination.
/// </summary>
public sealed class FuseService : IFuseService
{
    private readonly IAnsiConsole _console;
    private readonly IFileSystem _fileSystem;
    private long _totalTokenCount = 0;
    private FuseOptions _options = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FuseService"/> class.
    /// </summary>
    /// <param name="fileSystem">The file system abstraction.</param>
    /// <param name="console">The ansi console for rich output.</param>
    public FuseService(IFileSystem fileSystem, IAnsiConsole console)
    {
        _fileSystem = fileSystem;
        _console = console;
    }

    /// <inheritdoc />
    public async Task FuseAsync(FuseOptions options, CancellationToken cancellationToken = default)
    {
        _options = options;

        if (!_fileSystem.DirectoryExists(_options.SourceDirectory))
        {
            _console.MarkupLine($"[red]Error:[/] The directory [yellow]'{_options.SourceDirectory}'[/] does not exist.");
            return;
        }

        var (extensions, excludeFolders, excludePatterns) = GetExtensionsAndExclusions();
        SetupOutputPath();

        var outputFileName = _options.OutputFileName ?? $"fused_{Path.GetFileName(_options.SourceDirectory)}_{DateTime.Now:yyyyMMddHHmmss}.txt";
        var outputFilePath = Path.Combine(_options.OutputDirectory, outputFileName);

        try
        {
            List<string> files = [];
            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("[yellow]Searching for files...[/]", async ctx =>
                {
                    files = GetFiles(extensions, excludeFolders, excludePatterns);
                });

            _console.MarkupLine($"Files Count: [green]{files.Count}[/]");

            if (_fileSystem.GetFileInfo(outputFilePath).Exists && !_options.Overwrite)
            {
                _console.MarkupLine($"[yellow]Warning:[/] The file [underline]'{outputFilePath}'[/] already exists and overwrite is disabled. Operation aborted.");
                return;
            }

            await CombineFilesAsync(files, outputFilePath, cancellationToken);

            if (_options.UseCondensing)
            {
                // This reads the file back, condenses it, and writes it out again.
                ApplyLineCondensing(outputFilePath);
            }

            // Final Summary Output
            _console.MarkupLine($"[bold]Output File:[/] [underline blue]{outputFilePath}[/]");
            _console.MarkupLine($"[bold]Final Size:[/] {FormattingUtils.FormatFileSize(_fileSystem.GetFileInfo(outputFilePath).Length)}");

            if (_options.ShowTokenCount)
            {
                _console.MarkupLine($"[bold]Est. Tokens:[/] [yellow]{_totalTokenCount:N0}[/]");
            }
        }
        catch (Exception ex)
        {
            _console.WriteException(ex, ExceptionFormats.ShortenPaths);
        }
    }

    private void ApplyLineCondensing(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        var condensedLines = lines.Where(line => !string.IsNullOrWhiteSpace(line));
        File.WriteAllLines(filePath, condensedLines);
    }

    private async Task CombineFilesAsync(List<string> files, string outputFilePath, CancellationToken cancellationToken)
    {
        _totalTokenCount = 0; // Reset token count for each run
        var processedFiles = 0;

        var tokenizer = await TikToken.GetEncodingAsync("cl100k_base");
        var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await using var writer = new StreamWriter(outputStream, Encoding.UTF8);

        var contentQueue = new ConcurrentQueue<string?>();
        var writingTask = WriteContentAsync(writer, contentQueue, cancellationToken);

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken }, async (file, ct) =>
        {
            var fileInfo = _fileSystem.GetFileInfo(file);
            var sb = new StringBuilder();
            var relativePath = _fileSystem.GetRelativePath(_options.SourceDirectory, file);

            // Ultra-compact start marker
            sb.Append($"<|{relativePath}|>");

            if (_options.IncludeMetadata)
                sb.Append($"[Size: {fileInfo.Length} bytes | Created: {fileInfo.CreationTime:yyyy-MM-dd HH:mm:ss} | Modified: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}] ");

            var content = await _fileSystem.ReadAllTextAsync(file, ct);

            // Token counting logic
            if (_options.ShowTokenCount || _options.MaxTokens.HasValue)
            {
                var tokenCount = tokenizer.Encode(content).Count;
                var currentTotal = Interlocked.Add(ref _totalTokenCount, tokenCount);

                if (_options.MaxTokens.HasValue && currentTotal > _options.MaxTokens.Value)
                {
                    await localCts.CancelAsync(); // Stop the Parallel.ForEachAsync
                    return;
                }
            }

            var processedContent = _options.TrimContent ? TrimFileContent(content) : content;

            processedContent = ApplyNonInvasiveMinification(processedContent, Path.GetExtension(file).ToLowerInvariant());

            sb.Append(processedContent);

            // Ultra-compact start marker
            sb.Append($"<|{relativePath}|>");

            contentQueue.Enqueue(sb.ToString());
            Interlocked.Increment(ref processedFiles);
        });

        contentQueue.Enqueue(null);
        await writingTask;
    }

    private void SetupOutputPath()
    {
        if (_options.OutputDirectory == Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) &&
            _options.SourceDirectory == Directory.GetCurrentDirectory())
            _options = _options with { OutputDirectory = _options.SourceDirectory };

        if (!_fileSystem.DirectoryExists(_options.OutputDirectory))
            _fileSystem.CreateDirectory(_options.OutputDirectory);
    }

    private (string[] Extensions, string[] ExcludeDirectories, string[] ExcludePatterns) GetExtensionsAndExclusions()
    {
        string[] extensions = ["*"];
        string[] excludeDirectories = [];
        string[] excludePatterns = [];

        if (_options.Template.HasValue)
        {
            (extensions, excludeDirectories) = ProjectTemplateRegistry.GetTemplate(_options.Template.Value);
            excludePatterns = ProjectTemplateRegistry.GetExcludedPatterns(_options.Template.Value);

            if (_options.ExcludeExtensions != null)
                extensions = extensions.Except(_options.ExcludeExtensions).ToArray();

            if (_options.IncludeExtensions != null)
                extensions = extensions.Concat(_options.IncludeExtensions).ToArray();

            if (_options.ExcludeDirectories != null)
                excludeDirectories = excludeDirectories.Concat(_options.ExcludeDirectories).ToArray();
        }

        return (extensions, excludeDirectories, excludePatterns);
    }

    private List<string> GetFiles(string[] extensions, string[] excludeFolders, string[] excludePatterns)
    {
        var option = _options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // Create and use the GitIgnoreParser
        List<Glob> gitignorePatterns = [];
        if (_options.RespectGitIgnore)
        {
            var gitIgnoreParser = new GitIgnoreParser(_fileSystem);
            gitignorePatterns = gitIgnoreParser.Parse(_options.SourceDirectory);
        }

        return _fileSystem.EnumerateFiles(_options.SourceDirectory, "*.*", option).AsParallel()
            .Where(file => extensions.Contains("*") || extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Where(file => !IsInExcludedFolder(file, excludeFolders))
            .Where(file => !_options.ExcludeTestProjects || !IsInTestProjectFolder(file))
            .Where(IsFileSizeAcceptable)
            .Where(file => !_options.IgnoreBinaryFiles || !_fileSystem.IsBinaryFile(file))
            .Where(file => !IsExcludedByPattern(file, excludePatterns))

            // Add the new filter for gitignore
            .Where(file => !gitignorePatterns.Any(p => p.IsMatch(file.Replace(Path.DirectorySeparatorChar, '/'))))
            .ToList();
    }

    private bool IsInExcludedFolder(string filePath, string[] excludeFolders)
    {
        var relativePath = _fileSystem.GetRelativePath(_options.SourceDirectory, filePath);
        var pathParts = relativePath.Split(Path.DirectorySeparatorChar);
        return pathParts.Any(part => excludeFolders.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private bool IsInTestProjectFolder(string filePath)
    {
        // Common test project directory suffixes
        var testProjectSuffixes = new[]
        {
            "UnitTests",
            "Tests",
            "IntegrationTests",
            "Specs",
            "Test",
            "Testing",
            "FunctionalTests",
            "AcceptanceTests",
            "EndToEndTests",
            "E2ETests",
            "TestProject",
            "TestSuite",
            "TestLib",
            "TestData",
            "TestFramework",
            "TestUtils",
            "TestUtilities",
            "TestHelper",
            "TestHelpers",
            "TestCommon",
            "TestShared",
            "TestSupport",
            "Benchmark",
            "Benchmarks",
            "Performance",
            "PerformanceTests",
            "LoadTests",
            "StressTests"
        };

        var relativePath = _fileSystem.GetRelativePath(_options.SourceDirectory, filePath);
        var pathParts = relativePath.Split(Path.DirectorySeparatorChar);

        return pathParts.Any(part =>
            testProjectSuffixes.Any(suffix =>
                part.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsFileSizeAcceptable(string filePath)
    {
        if (_options.MaxFileSizeKB == 0)
            return true;

        var fileInfo = _fileSystem.GetFileInfo(filePath);
        return fileInfo.Length <= _options.MaxFileSizeKB * 1024;
    }

    private bool IsExcludedByPattern(string filePath, string[] excludePatterns)
    {
        if (excludePatterns == null || excludePatterns.Length == 0)
            return false;

        var fileName = Path.GetFileName(filePath);

        foreach (var pattern in excludePatterns)
        {
            // Convert glob pattern to regex pattern
            // Escape special regex characters except * and ?
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            try
            {
                if (Regex.IsMatch(fileName, regexPattern))
                    return true;
            }
            catch (RegexParseException ex)
            {
                _console.MarkupLine($"[red]Warning:[/] Invalid exclude pattern '[yellow]{pattern}[/]': {ex.Message}");
            }
        }

        return false;
    }

    private string ApplyNonInvasiveMinification(string content, string fileExtension)
    {
        switch (fileExtension)
        {
            case ".cs":
            case ".cshtml":
            case ".razor":
                return ApplyCSharpMinification(content, fileExtension);
            case ".html":
                return HtmlMinifier.Minify(content);
            case ".css":
                return CssMinifier.Minify(content);
            case ".scss":
                return ScssMinifier.Minify(content);
            case ".js":
                return JavaScriptMinifier.Minify(content);
            case ".json":
                return JsonMinifier.Minify(content);
            case ".xml":
            case ".targets":
            case ".props":
            case ".csproj":
                return XmlMinifier.Minify(content);
            case ".md":
                return MarkdownMinifier.Minify(content);
            case ".yml":
            case ".yaml":
                return YamlMinifier.Minify(content);
            default:
                return content;
        }
    }

    private string ApplyCSharpMinification(string content, string fileExtension)
    {
        var isRazorFile = fileExtension == ".cshtml" || fileExtension == ".razor";

        // First, apply Razor-specific minification if it's a Razor file
        if (isRazorFile && _options.MinifyHtmlAndRazor)
            content = RazorMinifier.Minify(content);

        // Then apply C# minification
        content = CSharpMinifier.Minify(content, _options);

        // Finally, apply HTML minification for Razor files
        if (isRazorFile && _options.MinifyHtmlAndRazor)
            content = RazorMinifier.Minify(content);

        return content;
    }

    private async Task WriteContentAsync(StreamWriter writer, ConcurrentQueue<string?> contentQueue, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            while (contentQueue.TryDequeue(out var content))
            {
                if (content == null)
                    return; // End of processing

                await writer.WriteAsync(content);
            }

            await Task.Delay(10, cancellationToken); // Small delay to reduce CPU usage
        }
    }

    private string TrimFileContent(string content)
    {
        var lines = content.Split('\n');
        var trimmedLines = lines.Select(line => line.Trim()).Where(line => !string.IsNullOrWhiteSpace(line));
        return string.Join('\n', trimmedLines);
    }
}