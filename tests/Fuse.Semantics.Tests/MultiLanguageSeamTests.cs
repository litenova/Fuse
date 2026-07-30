using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Semantics.Tests;

// S10: the language-provider seam. A spike provider registered alongside C# is selected by extension and
// indexes a non-C# fixture end to end, and the C# path is unchanged.
public sealed class MultiLanguageSeamTests
{
    [Fact]
    public void PythonProviderExtractsClassesAndFunctions()
    {
        var provider = new PythonSyntaxProvider();
        const string source = """
            class PriceCalculator:
                def compute_total(self, items):
                    return 0

                def _private_helper(self):
                    return 1
            """;

        var result = provider.Extract("src/pricing.py", source);

        Assert.Contains(result.Symbols, s => s.Name == "PriceCalculator" && s.Kind == "class");
        Assert.Contains(result.Symbols, s => s.Name == "compute_total" && s.Kind == "function" && s.IsPublicApi);
        Assert.Contains(result.Symbols, s => s.Name == "_private_helper" && !s.IsPublicApi);
        Assert.Equal(result.Symbols.Count, result.Chunks.Count);
    }

    [Fact]
    public void JavaScriptProviderExtractsDeclarationsAndArrowFunctions()
    {
        // A5 breadth: the offline JS/TS provider extracts class and function declarations plus arrow-function
        // and function-expression assignments, marking exported declarations as the module's public API.
        var provider = new JavaScriptSyntaxProvider();
        const string source = """
            export class BasketService {
            }
            export function computeTotal(items) {
              return 0;
            }
            const applyDiscount = (price) => price * 0.9;
            let _internal = function () { return 1; };
            """;

        var result = provider.Extract("src/basket.ts", source);

        Assert.Contains(result.Symbols, s => s.Name == "BasketService" && s.Kind == "class" && s.IsPublicApi);
        Assert.Contains(result.Symbols, s => s.Name == "computeTotal" && s.Kind == "function" && s.IsPublicApi);
        Assert.Contains(result.Symbols, s => s.Name == "applyDiscount" && s.Kind == "function");
        Assert.Contains(result.Symbols, s => s.Name == "_internal" && !s.IsPublicApi);
        Assert.Equal(result.Symbols.Count, result.Chunks.Count);
        // The provider claims the common JS/TS extensions.
        Assert.Contains(".ts", provider.Extensions);
        Assert.Contains(".tsx", provider.Extensions);
        Assert.Contains(".js", provider.Extensions);
    }

    [Fact]
    public void RegistrySelectsProviderByExtensionAndReportsExtensions()
    {
        var registry = new LanguageSyntaxProviderRegistry([new CSharpSyntaxProvider(new SyntaxSymbolExtractor()), new PythonSyntaxProvider()]);

        Assert.Equal("csharp", registry.ForExtension(".cs")!.Language);
        Assert.Equal("python", registry.ForExtension(".py")!.Language);
        Assert.Null(registry.ForExtension(".rb"));
        Assert.Contains(".cs", registry.Extensions);
        Assert.Contains(".py", registry.Extensions);
    }

    [Fact]
    public async Task IndexesAMixedLanguageWorkspaceAndFindsTheNonCSharpFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-mixed-lang", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Widget.cs"), "namespace App; public class WidgetService { public void Run() {} }");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "pricing.py"), "class PriceCalculator:\n    def compute_total(self):\n        return 0\n");

        var databasePath = Path.Combine(root, ".fuse", "fuse.db");
        await using (var store = new WorkspaceIndexStore(databasePath))
        {
            await store.InitializeAsync(CancellationToken.None);
            var result = await CreateIndexer().IndexAsync(root, store, CancellationToken.None);

            // No project file: the workspace indexes at the syntax tier, which is the spike's scope.
            Assert.Equal("syntax", result.Mode);

            // Both languages are indexed through the seam: the C# type and the Python class and function.
            Assert.Contains(await store.FindSymbolsByNameAsync("WidgetService", 10, CancellationToken.None), s => s.FilePath.EndsWith("Widget.cs", StringComparison.Ordinal));
            Assert.Contains(await store.FindSymbolsByNameAsync("PriceCalculator", 10, CancellationToken.None), s => s.FilePath.EndsWith("pricing.py", StringComparison.Ordinal));

            // The non-C# file is full-text searchable, including by an identifier subword (S1 over the seam).
            var hits = await store.SearchAsync(new SearchQuery("compute"), CancellationToken.None);
            Assert.Contains(hits, h => h.FilePath.EndsWith("pricing.py", StringComparison.Ordinal));

            // S10b: each file is tagged with its provider's language, so retrieval can filter or blend by language.
            var csharpFiles = await store.GetFilesByLanguageAsync("csharp", CancellationToken.None);
            var pythonFiles = await store.GetFilesByLanguageAsync("python", CancellationToken.None);
            Assert.Contains(csharpFiles, p => p.EndsWith("Widget.cs", StringComparison.Ordinal));
            Assert.DoesNotContain(csharpFiles, p => p.EndsWith("pricing.py", StringComparison.Ordinal));
            Assert.Contains(pythonFiles, p => p.EndsWith("pricing.py", StringComparison.Ordinal));
            Assert.DoesNotContain(pythonFiles, p => p.EndsWith("Widget.cs", StringComparison.Ordinal));
        }

        TryDelete(root, databasePath);
    }

    private static SemanticIndexer CreateIndexer()
    {
        var fileSystem = new PhysicalFileSystem();
        var pipeline = new FileCollectionPipeline(
            fileSystem,
            new GitIgnoreParser(fileSystem),
            [new GitIgnoreFilter(), new ExtensionFilter(), new ExcludedDirectoryFilter(), new EmptyFileFilter(), new BinaryFileFilter(fileSystem)]);
        return new SemanticIndexer(
            new DotNetWorkspaceDiscoverer(),
            new RoslynWorkspaceLoader(),
            new WorkspaceFileScanner(pipeline),
            new SemanticSymbolExtractor(),
            new SyntaxSymbolExtractor(),
            new SyntaxRouteExtractor(),
            new FileHashService(),
            Fuse.Semantics.Analyzers.SemanticAnalysisRunner.CreateDefault());
    }

    private static void TryDelete(string root, string databasePath)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
