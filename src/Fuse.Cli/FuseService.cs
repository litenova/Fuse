using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Fuse.Cli;

public sealed class FuseService
{
    private readonly FuseOptions _options;
    private readonly ILogger<FuseService> _logger;

    public FuseService(FuseOptions options, ILogger<FuseService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task FuseAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing directory: {SourceDirectory}", _options.SourceDirectory);
        if (!Directory.Exists(_options.SourceDirectory))
        {
            _logger.LogError("The directory {SourceDirectory} does not exist.", _options.SourceDirectory);
            return;
        }

        var (extensions, excludeFolders) = GetExtensionsAndExclusions();
        SetupOutputPath();

        var outputFilePath = Path.Combine(_options.OutputDirectory, _options.OutputFileName ?? $"Fuse_{Path.GetFileName(_options.SourceDirectory)}_{DateTime.Now:yyyyMMddHHmmss}.txt");
        _logger.LogInformation("Output file: {OutputFilePath}", outputFilePath);

        try
        {
            _logger.LogInformation("Searching for files...");
            var files = GetFiles(extensions, excludeFolders);
            _logger.LogInformation("Found {FileCount} files.", files.Count);

            if (File.Exists(outputFilePath) && !_options.Overwrite)
            {
                _logger.LogWarning("The file {OutputFilePath} already exists and overwrite is set to false. Operation aborted.", outputFilePath);
                return;
            }

            await CombineFilesAsync(files, outputFilePath, cancellationToken);

            if (_options.UseCondensing)
            {
                ApplyLineCondensing(outputFilePath);
            }

            _logger.LogInformation("Completed: Combined file content has been written to {OutputFilePath}", outputFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while processing files.");
        }
    }

    private void SetupOutputPath()
    {
        if (_options.OutputDirectory == Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) &&
            _options.SourceDirectory == Directory.GetCurrentDirectory())
        {
            _options.OutputDirectory = _options.OutputDirectory;
        }

        if (!Directory.Exists(_options.OutputDirectory))
        {
            Directory.CreateDirectory(_options.OutputDirectory);
        }
    }

    private (string[] Extensions, string[] ExcludeDirectories) GetExtensionsAndExclusions()
    {
        if (_options.Template.HasValue)
        {
            var (defaultExtensions, defaultExcludeDirectories) = ProjectTemplateRegistry.GetTemplate(_options.Template.Value);
            return (
                _options.IncludeExtensions ?? defaultExtensions,
                _options.ExcludeDirectories ?? defaultExcludeDirectories
            );
        }
        else
        {
            return (
                _options.IncludeExtensions ?? ["*"],
                _options.ExcludeDirectories ?? []
            );
        }
    }

    private List<string> GetFiles(string[] extensions, string[] excludeFolders)
    {
        var option = _options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(_options.SourceDirectory, "*.*", option)
            .AsParallel()
            .Where(file => extensions.Contains("*") || extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Where(file => !IsInExcludedFolder(file, excludeFolders))
            .Where(IsFileSizeAcceptable)
            .Where(file => !_options.IgnoreBinaryFiles || !IsBinaryFile(file))
            .ToList();
    }

    private bool IsInExcludedFolder(string filePath, string[] excludeFolders)
    {
        var relativePath = Path.GetRelativePath(_options.SourceDirectory, filePath);
        var pathParts = relativePath.Split(Path.DirectorySeparatorChar);
        return pathParts.Any(part => excludeFolders.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private bool IsFileSizeAcceptable(string filePath)
    {
        if (_options.MaxFileSizeKB == 0) return true;
        var fileInfo = new FileInfo(filePath);
        return fileInfo.Length <= _options.MaxFileSizeKB * 1024;
    }

    private bool IsBinaryFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(stream);
        int bytesToRead = Math.Min(512, (int)stream.Length);
        byte[] bytes = reader.ReadBytes(bytesToRead);
        return bytes.Any(b => b == 0);
    }

    private async Task CombineFilesAsync(List<string> files, string outputFilePath, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Combining files...");
        var processedFiles = 0;
        await using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await using var writer = new StreamWriter(outputStream, Encoding.UTF8);
        var contentQueue = new ConcurrentQueue<string?>();
        var writingTask = WriteContentAsync(writer, contentQueue, cancellationToken);

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (file, ct) =>
            {
                var fileInfo = new FileInfo(file);
                var sb = new StringBuilder();

                // Get relative path
                var relativePath = Path.GetRelativePath(_options.SourceDirectory, file);

                // Add relative file path at the start of the line
                sb.Append($"{relativePath}: ");

                if (_options.IncludeMetadata)
                {
                    sb.Append($"[Size: {fileInfo.Length} bytes | Created: {fileInfo.CreationTime:yyyy-MM-dd HH:mm:ss} | Modified: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}] ");
                }

                var content = await File.ReadAllTextAsync(file, ct);
                var processedContent = _options.TrimContent ? TrimFileContent(content) : content;

                if (_options.AggressiveMinification)
                {
                    var extension = Path.GetExtension(file).ToLowerInvariant();
                    if (extension == ".razor")
                    {
                        processedContent = RazorMinifier.Minify(processedContent);
                        processedContent = Regex.Replace(processedContent, @"@code\s*{([^}]*)}", match =>
                        {
                            var code = match.Groups[1].Value;
                            code = CSharpMinifier.Minify(code, aggressiveMinification: _options.AggressiveMinification);
                            return $"@code{{{code}}}";
                        });
                    }
                    else if (extension == ".cs")
                    {
                        processedContent = CSharpMinifier.Minify(processedContent, _options.AggressiveMinification, _options.RemoveAllUsings, _options.RemoveNamespaceDeclaration);
                    }
                }

                sb.Append(processedContent);
                sb.AppendLine();
                contentQueue.Enqueue(sb.ToString());
                Interlocked.Increment(ref processedFiles);
            });

        contentQueue.Enqueue(null); // Signal end of processing
        await writingTask;
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
                if (content == null) return; // End of processing
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