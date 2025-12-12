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

public sealed class FuseService : IFuseService
{
    private readonly IAnsiConsole _console;
    private readonly IFileSystem _fileSystem;
    private readonly TikToken _tokenizer;
    private long _totalTokenCount = 0;
    private FuseOptions _options = new();

    private sealed record FileProcessingInfo(string FullPath, string RelativePath, FileInfo Info);

    public FuseService(IFileSystem fileSystem, IAnsiConsole console)
    {
        _fileSystem = fileSystem;
        _console = console;
        _tokenizer = TikToken.GetEncoding("cl100k_base");
    }

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
            List<FileProcessingInfo> files = [];
            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("[yellow]Searching for files...[/]", _ =>
                {
                    files = GetFiles(extensions, excludeFolders, excludePatterns);
                    return Task.CompletedTask;
                });

            _console.MarkupLine($"Files Count: [green]{files.Count}[/]");

            if (_fileSystem.GetFileInfo(outputFilePath).Exists && !_options.Overwrite)
            {
                _console.MarkupLine($"[yellow]Warning:[/] The file [underline]'{outputFilePath}'[/] already exists and overwrite is disabled. Operation aborted.");
                return;
            }

            await CombineFilesAsync(files, outputFilePath, cancellationToken);

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

    private async Task CombineFilesAsync(List<FileProcessingInfo> files, string outputFilePath, CancellationToken cancellationToken)
    {
        _totalTokenCount = 0;
        var processedFiles = 0;
        var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await using var writer = new StreamWriter(outputStream, Encoding.UTF8);

        var contentQueue = new ConcurrentQueue<string?>();
        var writingTask = WriteContentAsync(writer, contentQueue, localCts.Token);

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = localCts.Token }, async (fileInfo, ct) =>
        {
            var sb = new StringBuilder();
            sb.Append($"<|{fileInfo.RelativePath}|>");

            if (_options.IncludeMetadata)
            {
                sb.Append($"[Size:{fileInfo.Info.Length}bytes | Modified:{fileInfo.Info.LastWriteTime:yyyy-MM-dd HH:mm:ss}]");
            }

            var content = await _fileSystem.ReadAllTextAsync(fileInfo.FullPath, ct);
            var processedContent = content;

            if (_options.TrimContent)
            {
                processedContent = Regex.Replace(processedContent, @"^[ \t]+|[ \t]+$", "", RegexOptions.Multiline);
            }

            if (_options.UseCondensing)
            {
                processedContent = Regex.Replace(processedContent, @"^\s*$\r?\n", string.Empty, RegexOptions.Multiline);
            }

            processedContent = ApplyNonInvasiveMinification(processedContent, fileInfo.Info.Extension.ToLowerInvariant());

            if (_options.ShowTokenCount || _options.MaxTokens.HasValue)
            {
                var tokenCount = _tokenizer.Encode(processedContent).Count;
                var currentTotal = Interlocked.Add(ref _totalTokenCount, tokenCount);

                if (_options.MaxTokens.HasValue && currentTotal > _options.MaxTokens.Value)
                {
                    await localCts.CancelAsync();
                    return;
                }
            }

            sb.Append(processedContent);
            sb.Append($"<|{fileInfo.RelativePath}|>");
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
        {
            _options = _options with { OutputDirectory = _options.SourceDirectory };
        }

        if (!_fileSystem.DirectoryExists(_options.OutputDirectory))
        {
            _fileSystem.CreateDirectory(_options.OutputDirectory);
        }
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

    private List<FileProcessingInfo> GetFiles(string[] extensions, string[] excludeFolders, string[] excludePatterns)
    {
        var option = _options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var gitignorePatterns = _options.RespectGitIgnore ? new GitIgnoreParser(_fileSystem).Parse(_options.SourceDirectory) : [];

        return _fileSystem.EnumerateFiles(_options.SourceDirectory, "*.*", option)
            .AsParallel()
            .Select(file => new { Path = file, Info = _fileSystem.GetFileInfo(file), RelativePath = _fileSystem.GetRelativePath(_options.SourceDirectory, file) })
            .Where(f => !gitignorePatterns.Any(p => p.IsMatch(f.Path.Replace(Path.DirectorySeparatorChar, '/'))))
            .Where(f => extensions.Contains("*") || extensions.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase))
            .Where(f => !IsInExcludedFolder(f.RelativePath, excludeFolders))
            .Where(f => !_options.ExcludeTestProjects || !IsInTestProjectFolder(f.RelativePath))
            .Where(f => _options.MaxFileSizeKB == 0 || f.Info.Length <= _options.MaxFileSizeKB * 1024)
            .Where(f => !_options.IgnoreBinaryFiles || !_fileSystem.IsBinaryFile(f.Path))
            .Where(f => !IsExcludedByPattern(f.Path, excludePatterns))
            .Select(f => new FileProcessingInfo(f.Path, f.RelativePath, f.Info))
            .ToList();
    }

    private bool IsInExcludedFolder(string relativePath, string[] excludeFolders)
    {
        var pathParts = relativePath.Split(Path.DirectorySeparatorChar);
        return pathParts.Any(part => excludeFolders.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private bool IsInTestProjectFolder(string relativePath)
    {
        var testProjectSuffixes = new[]
        {
            "UnitTests", "Tests", "IntegrationTests", "Specs", "Test", "Testing", "FunctionalTests", "AcceptanceTests", "EndToEndTests", "E2ETests", "TestProject", "TestSuite", "TestLib", "TestData",
            "TestFramework", "TestUtils", "TestUtilities", "TestHelper", "TestHelpers", "TestCommon", "TestShared", "TestSupport", "Benchmark", "Benchmarks", "Performance", "PerformanceTests",
            "LoadTests", "StressTests"
        };
        var pathParts = relativePath.Split(Path.DirectorySeparatorChar);
        return pathParts.Any(part => testProjectSuffixes.Any(suffix => part.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsExcludedByPattern(string filePath, string[] excludePatterns)
    {
        if (excludePatterns == null || excludePatterns.Length == 0) return false;
        var fileName = Path.GetFileName(filePath);
        foreach (var pattern in excludePatterns)
        {
            var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            try
            {
                if (Regex.IsMatch(fileName, regexPattern)) return true;
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
            case ".html": return HtmlMinifier.Minify(content);
            case ".css": return CssMinifier.Minify(content);
            case ".scss": return ScssMinifier.Minify(content);
            case ".js": return JavaScriptMinifier.Minify(content);
            case ".json": return JsonMinifier.Minify(content);
            case ".xml":
            case ".targets":
            case ".props":
            case ".csproj":
                return XmlMinifier.Minify(content);
            case ".md": return MarkdownMinifier.Minify(content);
            case ".yml":
            case ".yaml":
                return YamlMinifier.Minify(content);
            default: return content;
        }
    }

    private string ApplyCSharpMinification(string content, string fileExtension)
    {
        var isRazorFile = fileExtension == ".cshtml" || fileExtension == ".razor";
        if (isRazorFile && _options.MinifyHtmlAndRazor)
        {
            content = RazorMinifier.Minify(content);
        }

        content = CSharpMinifier.Minify(content, _options);
        return content;
    }

    private async Task WriteContentAsync(StreamWriter writer, ConcurrentQueue<string?> contentQueue, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            while (contentQueue.TryDequeue(out var content))
            {
                if (content == null) return;
                await writer.WriteAsync(content);
            }

            await Task.Delay(10, cancellationToken);
        }
    }
}