using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

public sealed class ProjectOwnershipResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-project-owner", Guid.NewGuid().ToString("N"));

    public ProjectOwnershipResolverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Resolves_default_source_when_project_also_has_a_linked_compile_item()
    {
        Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="../../Shared/Shared.cs" Link="Shared.cs" />
              </ItemGroup>
            </Project>
            """);
        Write("src/App/App.cs", "namespace App; public sealed class AppType { }");
        Write("Shared/Shared.cs", "namespace Shared; public sealed class SharedType { }");

        var ownership = await new ProjectOwnershipResolver().ResolveAsync(
            _root, "src/App/App.cs", CancellationToken.None);

        var project = Assert.Single(ownership.ProjectPaths);
        Assert.Equal(Path.Combine(_root, "src", "App", "App.csproj"), project);
        Assert.False(ownership.IsLinked);
    }

    [Fact]
    public async Task Resolves_each_project_that_links_the_same_source_file()
    {
        WriteProjectWithLink("src/One/One.csproj");
        WriteProjectWithLink("src/Two/Two.csproj");
        Write("Shared/Shared.cs", "namespace Shared; public sealed class SharedType { }");

        var ownership = await new ProjectOwnershipResolver().ResolveAsync(
            _root, "Shared/Shared.cs", CancellationToken.None);

        Assert.True(ownership.IsLinked);
        Assert.Equal(2, ownership.ProjectPaths.Count);
        Assert.Contains(Path.Combine(_root, "src", "One", "One.csproj"), ownership.ProjectPaths);
        Assert.Contains(Path.Combine(_root, "src", "Two", "Two.csproj"), ownership.ProjectPaths);
    }

    [Fact]
    public async Task Compile_remove_excludes_an_sdk_default_source_file()
    {
        Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Remove="Excluded.cs" />
              </ItemGroup>
            </Project>
            """);
        Write("src/App/Excluded.cs", "namespace App; public sealed class Excluded { }");

        var ownership = await new ProjectOwnershipResolver().ResolveAsync(
            _root, "src/App/Excluded.cs", CancellationToken.None);

        Assert.False(ownership.IsResolved);
        Assert.Empty(ownership.ProjectPaths);
    }

    [Fact]
    public async Task Rejects_a_path_that_escapes_the_repository_root()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => new ProjectOwnershipResolver().ResolveAsync(_root, "../outside.cs", CancellationToken.None));

        Assert.Equal("relativeFilePath", exception.ParamName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void WriteProjectWithLink(string relativePath) => Write(relativePath, """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <Compile Include="../../Shared/Shared.cs" Link="Shared.cs" />
          </ItemGroup>
        </Project>
        """);

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
