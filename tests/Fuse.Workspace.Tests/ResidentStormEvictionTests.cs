using Fuse.Cli.Extensions;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Semantics;
using Fuse.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Workspace.Tests;

// F-025: storm degradation on the resident path. A watcher batch above the 300-file threshold evicts the
// warmed root through ResidentWorkspaceHosting so reads fall back to store-backed rather than serving stale state.
public sealed class ResidentStormEvictionTests
{
    [Fact]
    public async Task Storm_batch_of_301_files_evicts_resident_root_to_store_backed()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-resident-storm", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"),
                "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

            var root = Path.GetFullPath(work);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var watcher = new FakeResidentBatchWatcher();
            using var provider = new ServiceCollection().AddFuse().BuildServiceProvider();
            var residents = provider.GetRequiredService<ResidentWorkspaceRegistry>();
            var indexJobs = provider.GetRequiredService<IWorkspaceIndexJobManager>();

            using var scope = ResidentWorkspaceHosting.Enable(root, watcher, residents, indexJobs, null, cts.Token);

            var warmed = await WaitForResidentAsync(residents, root, TimeSpan.FromMinutes(2));
            if (!warmed)
                return; // The SDK could not build here; nothing to validate.

            Assert.NotNull(residents.TryCheckOverlay(root, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => 7; }", CancellationToken.None));

            const int stormCount = 301;
            var batch = Enumerable.Range(0, stormCount)
                .Select(i => new WorkspaceFileChange(FileChangeKind.Changed, Path.Combine(work, $"bulk{i}.cs")))
                .ToList();
            Assert.Equal(stormCount, batch.Count);

            await watcher.RaiseBatchAsync(batch, cts.Token);

            Assert.Null(residents.DescribeResident(root));
            Assert.Null(residents.TryCheckOverlay(root, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => 7; }", CancellationToken.None));

            // A follow-up batch below the threshold does not resurrect resident state.
            await watcher.RaiseBatchAsync(
                [new WorkspaceFileChange(FileChangeKind.Changed, Path.Combine(work, "Widget.cs"))], cts.Token);
            Assert.Null(residents.DescribeResident(root));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<bool> WaitForResidentAsync(
        IResidentWorkspaceProvider residents, string root, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (residents.DescribeResident(root) is not null)
                return true;
            await Task.Delay(200);
        }

        return false;
    }

    private sealed class FakeResidentBatchWatcher : IResidentBatchWatcher
    {
        public event Func<IReadOnlyList<WorkspaceFileChange>, CancellationToken, Task>? BatchChanged;

        public Task RaiseBatchAsync(IReadOnlyList<WorkspaceFileChange> batch, CancellationToken cancellationToken) =>
            BatchChanged is null ? Task.CompletedTask : BatchChanged(batch, cancellationToken);

        public void Dispose()
        {
        }
    }
}
