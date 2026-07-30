using System.Diagnostics;
using Fuse.Cli.Services;
using Fuse.Cli.Tests;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fuse.Cli.Tests.Services;

// S1: the concrete resident-workspace provider. It answers the availability description and resident-grade
// overlay checks for its root, and applies watcher batches to keep the held compilation current, advancing a
// revision. Backed by a real resident workspace built from a temp project binlog (guarded to skip when the SDK
// cannot produce one).
public sealed class ResidentWorkspaceServiceTests
{
    [Fact]
    public async Task Service_describes_its_root_checks_overlays_and_advances_on_batches()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-resident-svc-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var binlog = Path.Combine(work, "build.binlog");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            var widget = Path.Combine(work, "Widget.cs");
            await File.WriteAllTextAsync(widget, "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

            if (!await TryBuildWithBinlogAsync(work, binlog))
                return;

            var root = Path.GetFullPath(work);
            using var service = new ResidentWorkspaceService(root, ResidentWorkspace.LoadFromBinlog(binlog, CancellationToken.None));

            // Describes its own root, not another.
            var status = service.DescribeResident(root);
            Assert.NotNull(status);
            Assert.Equal("revision 0", status!.AsOf);
            Assert.Null(service.DescribeResident(Path.Combine(Path.GetTempPath(), "other")));

            // Resident-grade overlay check answers for its root.
            var clean = service.TryCheckOverlay(root, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => 7; }", CancellationToken.None);
            Assert.NotNull(clean);
            Assert.DoesNotContain(clean!, d => d.Severity == "Error");
            Assert.Null(service.TryCheckOverlay(Path.Combine(Path.GetTempPath(), "other"), "Widget.cs", "x", CancellationToken.None));

            // Applying a batch that adds a Helper advances the revision, and a later check binds against it.
            var helper = Path.Combine(work, "Helper.cs");
            await File.WriteAllTextAsync(helper, "namespace Sample; public static class Helper { public static int Base() => 1; }");
            var result = service.ApplyBatch([new WorkspaceFileChange(FileChangeKind.Created, helper)], CancellationToken.None);
            Assert.Equal(1, result.Added);
            Assert.Equal("revision 1", service.DescribeResident(root)!.AsOf);

            var afterAdd = service.TryCheckOverlay(root, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => Helper.Base(); }", CancellationToken.None);
            Assert.NotNull(afterAdd);
            Assert.DoesNotContain(afterAdd!, d => d.Severity == "Error");
        }
        finally
        {
            DeleteTempDirectory(work);
        }
    }

    [Fact]
    public async Task ProjectChangedAsync_makes_an_added_type_queryable_in_the_store()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-resident-svc-proj-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var binlog = Path.Combine(work, "build.binlog");
        var databasePath = Path.Combine(work, ".fuse", "fuse.db");
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
            await RunGitAsync(work, "init", "-q");
            await RunGitAsync(work, "add", "Widget.csproj", "Widget.cs");

            if (!await TryBuildWithBinlogAsync(work, binlog))
                return;

            var root = Path.GetFullPath(work);
            using var service = new ResidentWorkspaceService(root, ResidentWorkspace.LoadFromBinlog(binlog, CancellationToken.None));

            // Add Helper.cs on disk and to the resident state, then project the changed project into a store.
            var helper = Path.Combine(work, "Helper.cs");
            await File.WriteAllTextAsync(helper, "namespace Sample; public static class Helper { public static int Base() => 1; }");
            service.ApplyBatch([new WorkspaceFileChange(FileChangeKind.Created, helper)], CancellationToken.None);

            using var provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
            var indexer = provider.GetRequiredService<SemanticIndexer>();
            await using var store = new WorkspaceIndexStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);
            await indexer.IndexSyntaxFirstAsync(root, store, CancellationToken.None);

            var projected = await service.ProjectChangedAsync(indexer, store, [helper], CancellationToken.None);
            Assert.True(projected >= 1);

            var names = (await store.ListSymbolsAsync(500, CancellationToken.None)).Select(s => s.Name).ToHashSet();
            Assert.Contains("Helper", names);
            Assert.Contains("Widget", names);

            var freshness = await indexer.ReconcileDirtyFilesAsync(root, store, CancellationToken.None);
            Assert.Equal(0, freshness.Reconciled);

            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"));
        }
        finally
        {
            DeleteTempDirectory(work);
        }
    }

    private static async Task<bool> TryBuildWithBinlogAsync(string projectDir, string binlogPath)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectDir,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(Path.Combine(projectDir, "Widget.csproj"));
        psi.ArgumentList.Add($"-bl:{binlogPath}");
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v:quiet");

        try
        {
            using var process = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0 && File.Exists(binlogPath);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        await process!.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
    }

    private static void DeleteTempDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        // Git object files are read-only on Windows. Clear that attribute before deleting this exact fixture root.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
