using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Fuse.Core;
using Fuse.Core.Abstractions;
using Fuse.Infrastructure;
using Fuse.Infrastructure.Minifiers;
using Microsoft.Extensions.Logging;

namespace Fuse.Cli;

public sealed class FuseService : IFuseService
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<FuseService> _logger;
    private FuseOptions _options = new();

    public FuseService(ILogger<FuseService> logger, IFileSystem fileSystem)
    {
        _logger = logger;
        _fileSystem = fileSystem;
    }

    public async Task FuseAsync(FuseOptions options, CancellationToken cancellationToken = default)
    {
        _options = options;
        _logger.LogInformation("Processing directory: {SourceDirectory}", _options.SourceDirectory);
        if (!_fileSystem.DirectoryExists(_options.SourceDirectory))
        {
            _logger.LogError("The directory {SourceDirectory} does not exist.", _options.SourceDirectory);
            return;
        }

        var (extensions, excludeFolders, excludePatterns) = GetExtensionsAndExclusions();

        SetupOutputPath();

        var outputFileName = _options.OutputFileName ?? "fused.txt";
        var outputFilePath = Path.Combine(_options.OutputDirectory, outputFileName);
        _logger.LogInformation("Output file: {OutputFilePath}", outputFilePath);

        try
        {
            _logger.LogInformation("Searching for files...");
            var files = GetFiles(extensions, excludeFolders, excludePatterns);
            _logger.LogInformation("Found {FileCount} files.", files.Count);

            if (_fileSystem.GetFileInfo(outputFilePath).Exists && !_options.Overwrite)
            {
                _logger.LogWarning("The file {OutputFilePath} already exists and overwrite is set to false. Operation aborted.", outputFilePath);
                return;
            }

            await CombineFilesAsync(files, outputFilePath, cancellationToken);

            if (_options.UseCondensing)
                ApplyLineCondensing(outputFilePath);

            _logger.LogInformation("Completed: Combined file content has been written to {OutputFilePath}", outputFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while processing files.");
        }
    }

    private async Task CombineFilesAsync(List<string> files, string outputFilePath, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Combining files...");
        var processedFiles = 0;

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
            var processedContent = _options.TrimContent ? TrimFileContent(content) : content;

            processedContent = ApplyNonInvasiveMinification(processedContent, Path.GetExtension(file).ToLowerInvariant());

            sb.Append(processedContent);

            // Ultra-compact start marker
            sb.Append($"<|{relativePath}|>");

            contentQueue.Enqueue(sb.ToString());
            Interlocked.Increment(ref processedFiles);

            _logger.LogDebug($"Processed file: {relativePath}");
        });

        contentQueue.Enqueue(null);
        await writingTask;

        _logger.LogInformation($"Processed {processedFiles} files.");
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
        return _fileSystem.EnumerateFiles(_options.SourceDirectory, "*.*", option)
            .AsParallel()
            .Where(file => extensions.Contains("*") || extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Where(file => !IsInExcludedFolder(file, excludeFolders))
            .Where(file => !_options.ExcludeTestProjects || !IsInTestProjectFolder(file))
            .Where(IsFileSizeAcceptable)
            .Where(file => !_options.IgnoreBinaryFiles || !_fileSystem.IsBinaryFile(file))
            .Where(file => !IsExcludedByPattern(file, excludePatterns))
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
                _logger.LogWarning("Invalid regex pattern: {Pattern}. Error: {Error}", pattern, ex.Message);
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

    private void ApplyLineCondensing(string filePath)
    {
        _logger.LogInformation("Applying line condensing...");
        var lines = File.ReadAllLines(filePath);
        var condensedLines = lines.Where(line => !string.IsNullOrWhiteSpace(line));
        File.WriteAllLines(filePath, condensedLines);
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