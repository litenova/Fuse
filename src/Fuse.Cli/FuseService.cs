using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Fuse.Cli;

public sealed class FuseService(FuseOptions options, ILogger<FuseService> logger)
{
    public async Task FuseAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Processing directory: {SourceDirectory}", options.SourceDirectory);

        if (!Directory.Exists(options.SourceDirectory))
        {
            logger.LogError("The directory {SourceDirectory} does not exist.", options.SourceDirectory);
            return;
        }

        var (extensions, excludeFolders) = GetExtensionsAndExclusions();

        SetupOutputPath();

        var outputFilePath = Path.Combine(options.OutputDirectory, options.OutputFileName ?? $"Fuse_{Path.GetFileName(options.SourceDirectory)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

        logger.LogInformation("Output file: {OutputFilePath}", outputFilePath);

        try
        {
            logger.LogInformation("Searching for files...");
            var files = GetFiles(extensions, excludeFolders);
            logger.LogInformation("Found {FileCount} files.", files.Count);

            if (File.Exists(outputFilePath) && !options.Overwrite)
            {
                logger.LogWarning("The file {OutputFilePath} already exists and overwrite is set to false. Operation aborted.", outputFilePath);
                return;
            }

            await CombineFilesAsync(files, outputFilePath, cancellationToken);

            logger.LogInformation("Completed: Combined file content has been written to {OutputFilePath}", outputFilePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while processing files.");
        }
    }

    private void SetupOutputPath()
    {
        if (options.OutputDirectory == Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) &&
            options.SourceDirectory == Directory.GetCurrentDirectory())
        {
            options.OutputDirectory = options.OutputDirectory;
        }

        if (!Directory.Exists(options.OutputDirectory))
        {
            Directory.CreateDirectory(options.OutputDirectory);
        }
    }

    private (string[] Extensions, string[] ExcludeDirectories) GetExtensionsAndExclusions()
    {
        if (options.Template.HasValue)
        {
            var (defaultExtensions, defaultExcludeDirectories) = ProjectTemplateRegistry.GetTemplate(options.Template.Value);
            return (
                options.IncludeExtensions ?? defaultExtensions,
                options.ExcludeDirectories ?? defaultExcludeDirectories
            );
        }
        else
        {
            return (
                options.IncludeExtensions ?? ["*"], // Include all files if no extensions specified
                options.ExcludeDirectories ?? []
            );
        }
    }

    private List<string> GetFiles(string[] extensions, string[] excludeFolders)
    {
        var option = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        return Directory.EnumerateFiles(options.SourceDirectory, "*.*", option)
            .AsParallel()
            .Where(file => extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Where(file => !IsInExcludedFolder(file, excludeFolders))
            .Where(IsFileSizeAcceptable)
            .Where(file => !options.IgnoreBinaryFiles || !IsBinaryFile(file))
            .ToList();
    }

    private bool IsInExcludedFolder(string filePath, string[] excludeFolders)
    {
        var relativePath = Path.GetRelativePath(options.SourceDirectory, filePath);
        var pathParts = relativePath.Split(Path.DirectorySeparatorChar);
        return pathParts.Any(part => excludeFolders.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private bool IsFileSizeAcceptable(string filePath)
    {
        if (options.MaxFileSizeKB == 0) return true;
        var fileInfo = new FileInfo(filePath);
        return fileInfo.Length <= options.MaxFileSizeKB * 1024;
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
        logger.LogInformation("Combining files...");
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
                sb.AppendLine($"--- {file} ---");
                sb.AppendLine($"Size: {fileInfo.Length} bytes | Created: {fileInfo.CreationTime:yyyy-MM-dd HH:mm:ss} | Modified: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();

                var content = await File.ReadAllTextAsync(file, ct);
                var processedContent = options.TrimContent ? TrimFileContent(content) : content;
                sb.Append(processedContent);
                sb.AppendLine();
                sb.AppendLine();

                contentQueue.Enqueue(sb.ToString());

                Interlocked.Increment(ref processedFiles);
            });

        contentQueue.Enqueue(null); // Signal end of processing
        await writingTask;
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