using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     Plans the repository inventory used by every index stage, combining language-provider source extensions
///     with the project and configuration files required for workspace discovery.
/// </summary>
internal sealed class WorkspaceInventoryPlanner
{
    private static readonly string[] ConfigurationExtensions = [".csproj", ".props", ".targets", ".json"];
    private readonly WorkspaceFileScanner _scanner;
    private readonly LanguageSyntaxProviderRegistry _syntaxProviders;

    internal WorkspaceInventoryPlanner(WorkspaceFileScanner scanner, LanguageSyntaxProviderRegistry syntaxProviders)
    {
        _scanner = scanner;
        _syntaxProviders = syntaxProviders;
    }

    internal Task<FileScanResult> ScanAsync(string root, CancellationToken cancellationToken)
    {
        var extensions = _syntaxProviders.Extensions
            .Concat(ConfigurationExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return _scanner.ScanWithSkipsAsync(new FileScanRequest(root, extensions), cancellationToken);
    }

    internal async Task<bool> IsCurrentAsync(
        string root,
        IWorkspaceIndexQueryStore store,
        CancellationToken cancellationToken)
    {
        var stored = await store.GetAllFileHashesAsync(cancellationToken);
        var scan = await ScanAsync(root, cancellationToken);
        if (stored.Count != scan.Files.Count)
            return false;

        foreach (var file in scan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stored.TryGetValue(file.NormalizedPath, out var storedHash)
                || !string.Equals(storedHash, file.ContentHash, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
