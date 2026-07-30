using Fuse.Cli.Rpc;
using Xunit;

namespace Fuse.Cli.Tests.Host;

// The daemon launch arguments. The spawned daemon runs `host --directory <root>` syntax-first with an idle
// window, whether the current process is the published apphost or `dotnet fuse.dll`.
public sealed class DaemonProcessLauncherTests
{
    [Fact]
    public void Apphost_launch_runs_syntax_first_host_for_the_root_with_idle_set()
    {
        var psi = DaemonProcessLauncher.BuildStartInfo(
            processPath: "/tools/fuse", fuseDllPath: "/tools/fuse.dll", root: "/repo", idleMinutes: 30);

        Assert.Equal("/tools/fuse", psi.FileName);
        Assert.Equal(["host", "--directory", "/repo"], psi.ArgumentList); // no dll arg for the apphost
        Assert.Equal("/repo", psi.WorkingDirectory);
        Assert.Equal("0", psi.Environment["FUSE_RESIDENT"]);
        Assert.Equal("0", psi.Environment["FUSE_EAGER_INDEX"]);
        Assert.Equal("30", psi.Environment["FUSE_DAEMON_IDLE_MINUTES"]);
    }

    [Fact]
    public void Dotnet_hosted_launch_passes_the_fuse_dll_before_host()
    {
        var psi = DaemonProcessLauncher.BuildStartInfo(
            processPath: "/usr/bin/dotnet", fuseDllPath: "/app/fuse.dll", root: "/repo", idleMinutes: 15);

        Assert.Equal("/usr/bin/dotnet", psi.FileName);
        Assert.Equal(["/app/fuse.dll", "host", "--directory", "/repo"], psi.ArgumentList);
        Assert.Equal("15", psi.Environment["FUSE_DAEMON_IDLE_MINUTES"]);
    }
}
