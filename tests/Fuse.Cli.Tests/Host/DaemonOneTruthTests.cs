using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fuse.Cli.Rpc;
using Xunit;

namespace Fuse.Cli.Tests.Host;

// G5 gate (one truth): two clients reading the same daemon see one truth. A daemon holds the single resident
// workspace; two independent pipe clients checking the same edit get the same diagnostics from it. With R13,
// `fuse mcp serve` delegates to a shared daemon by default (see AgentDefaultsTests for the default-on contract).
// Category=RequiresSdk (see RequiresSdkIntegration): CI excludes it from the default test run; publish smoke fails
// when the SDK or bundled worker cannot reach a resident workspace.
[Trait(RequiresSdkIntegration.TraitName, RequiresSdkIntegration.TraitValue)]
public sealed class DaemonOneTruthTests
{
    private static string? RepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static string? FuseDll()
    {
        var root = RepoRoot();
        if (root is null)
            return null;
        foreach (var config in new[] { "Release", "Debug" })
        {
            var dll = Path.Combine(root, "src", "Host", "Fuse.Cli", "bin", config, "net10.0", "fuse.dll");
            if (File.Exists(dll))
                return dll;
        }

        return null;
    }

    [Fact]
    public async Task Two_clients_reading_one_daemon_see_one_truth()
    {
        var fuseDll = FuseDll();
        RequiresSdkIntegration.RequireArtifact(fuseDll, "fuse.dll");
        var executable = fuseDll!;

        var work = Path.Combine(Path.GetTempPath(), "fuse-onetruth-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.Combine(work, ".git"));
        await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"),
            "namespace Sample; public sealed class Widget { public int Spin() => 1; }");

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = work,
        };
        psi.ArgumentList.Add(executable);
        psi.ArgumentList.Add("host");
        psi.ArgumentList.Add("--directory");
        psi.ArgumentList.Add(work);
        psi.Environment["FUSE_RESIDENT"] = "1";
        psi.Environment["FUSE_BUILD_CAPTURE"] = "1";

        Process? daemon = null;
        try
        {
            daemon = Process.Start(psi);
            RequiresSdkIntegration.RequireCondition(daemon is not null, "could not start fuse host daemon");

            // Wait for the daemon to serve a resident workspace (build + rehydrate). Poll the overlay check until
            // it answers resident-grade or the window elapses; a non-resident environment abstains cleanly.
            var broken = "namespace Sample; public sealed class Widget { public Nonexistent Spin() => null!; }";
            CheckOverlayResultDto? a = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                a = await FuseHostClient.TryCheckOverlayAsync(work, "Widget.cs", broken, includeAnalyzers: false, TimeSpan.FromSeconds(2), CancellationToken.None);
                if (a is { HasResident: true })
                    break;
                await Task.Delay(1000);
            }

            RequiresSdkIntegration.RequireCondition(
                a is { HasResident: true },
                "the daemon never reached a resident workspace (no SDK or bundled build-capture worker)");

            // A second, independent client checks the same edit against the same daemon: one truth.
            var b = await FuseHostClient.TryCheckOverlayAsync(work, "Widget.cs", broken, includeAnalyzers: false, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.NotNull(b);
            Assert.True(b!.HasResident);

            // Both clients see the same daemon-held truth: the same diagnostic ids for the same edit.
            var idsA = a!.Diagnostics.Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var idsB = b.Diagnostics.Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(idsA, idsB);
            Assert.Contains("CS0246", idsA); // the broken edit's undefined-type error, from the one workspace
        }
        finally
        {
            try { if (daemon is { HasExited: false }) daemon.Kill(entireProcessTree: true); } catch { /* best effort */ }
            daemon?.Dispose();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Two_clients_see_identical_index_state_from_daemon()
    {
        var fuseDll = FuseDll();
        RequiresSdkIntegration.RequireArtifact(fuseDll, "fuse.dll");
        var executable = fuseDll!;

        var work = Path.Combine(Path.GetTempPath(), "fuse-index-onetruth-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.Combine(work, ".git"));
        await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"),
            "namespace Sample; public sealed class Widget { public int Spin() => 1; }");

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = work,
        };
        psi.ArgumentList.Add(executable);
        psi.ArgumentList.Add("host");
        psi.ArgumentList.Add("--directory");
        psi.ArgumentList.Add(work);
        // A first open below owns the full index synchronously. Do not also start the eager syntax pass, because
        // its background upgrade can hold the single writer while the two-client assertion is trying to read it.
        psi.Environment["FUSE_EAGER_INDEX"] = "0";

        Process? daemon = null;
        try
        {
            daemon = Process.Start(psi);
            RequiresSdkIntegration.RequireCondition(daemon is not null, "could not start fuse host daemon");

            OpenIndexedResultDto? a = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                // A two-second cancellation can abort the daemon's first full index repeatedly on a cold SDK
                // runner. Let one request hold the writer long enough to finish, then the second client reads it.
                a = await FuseHostClient.TryOpenIndexedAsync(work, TimeSpan.FromSeconds(30), CancellationToken.None);
                if (a is { Status: "ready", FileCount: > 0 })
                    break;
                await Task.Delay(500);
            }

            RequiresSdkIntegration.RequireCondition(
                a is { Status: "ready", FileCount: > 0 },
                "daemon never prepared a readable index");

            OpenIndexedResultDto? b = null;
            var secondClientDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < secondClientDeadline)
            {
                // The first completed write can be followed by a watcher reconcile. A second client must observe
                // the daemon's settled read state, not treat the bounded index_busy reply as a different truth.
                b = await FuseHostClient.TryOpenIndexedAsync(work, TimeSpan.FromSeconds(30), CancellationToken.None);
                if (b is { Status: "ready", FileCount: > 0 })
                    break;
                await Task.Delay(500);
            }

            Assert.NotNull(b);
            Assert.Equal("ready", b!.Status);
            Assert.Equal(a!.FileCount, b.FileCount);
            Assert.Equal(a.Mode, b.Mode);
        }
        finally
        {
            try { if (daemon is { HasExited: false }) daemon.Kill(entireProcessTree: true); } catch { /* best effort */ }
            daemon?.Dispose();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
