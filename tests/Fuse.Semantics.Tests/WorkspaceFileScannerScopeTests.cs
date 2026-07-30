using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// R25: indexing scope excludes vendored and generated trees. A non-git fixture (so the directory-walk path runs)
// with node_modules, a nested-.git vendored checkout, and a fuse.json-ignored directory indexes only the repo's
// own source; a src symbol file remains findable.
public sealed class WorkspaceFileScannerScopeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-scan-scope", Guid.NewGuid().ToString("N"));

    public WorkspaceFileScannerScopeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Scan_ExcludesVendoredGeneratedAndConfiguredTrees_IncludesSource()
    {
        Write("src/App/Good.cs", "namespace App; public class Good { }");
        Write("node_modules/pkg/Bad.cs", "class NodeBad { }");
        Write("vendored/Lib.cs", "class VendorBad { }");
        Directory.CreateDirectory(Path.Combine(_root, "vendored", ".git")); // marks vendored as a nested repo root.
        Write("thirdparty/Dep.cs", "class ThirdParty { }");
        Write("fuse.json", "{ \"ignore\": [\"thirdparty\"] }");

        var records = await CreateScanner().ScanAsync(new FileScanRequest(_root), CancellationToken.None);
        var paths = records.Select(r => r.NormalizedPath.Replace('\\', '/')).ToList();

        Assert.Contains(paths, p => p.EndsWith("src/App/Good.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("vendored", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("thirdparty", StringComparison.OrdinalIgnoreCase));
    }

    private static WorkspaceFileScanner CreateScanner()
    {
        var fileSystem = new PhysicalFileSystem();
        var pipeline = new FileCollectionPipeline(
            fileSystem,
            new GitIgnoreParser(fileSystem),
            [new GitIgnoreFilter(), new ExtensionFilter(), new ExcludedDirectoryFilter(), new EmptyFileFilter(), new BinaryFileFilter(fileSystem)]);
        return new WorkspaceFileScanner(pipeline);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
