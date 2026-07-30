using Microsoft.Extensions.Logging;

namespace Fuse.Cli.Rpc;

// Tracks host-created scope payloads so shutdown removes only files owned by this host process.
internal sealed class HostPayloadTracker
{
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    internal HostPayloadTracker(ILogger logger)
    {
        _logger = logger;
    }

    internal void Track(string path)
    {
        lock (_lock)
            _paths.Add(path);
    }

    internal void DeleteTrackedPayloads()
    {
        string[] paths;
        lock (_lock)
        {
            paths = [.. _paths];
            _paths.Clear();
        }

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to delete payload file {Path}.", path);
            }
        }
    }
}
