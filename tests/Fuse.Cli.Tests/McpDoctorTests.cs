using Fuse.Cli.Services;

namespace Fuse.Cli.Tests;

public sealed class McpDoctorTests
{
    [Fact]
    public async Task DiagnoseAsync_ReportsRegistrationGuidanceAndUnavailableDaemon()
    {
        var root = CreateTempDirectory();
        var command = Environment.ProcessPath!;
        await new McpInstallService().InstallAsync(
            [McpInstallClient.Cursor],
            McpInstallScope.Project,
            root,
            command,
            writeRules: true,
            new RecordingConsoleUI(),
            CancellationToken.None);

        var report = await new McpDoctorService().DiagnoseAsync(
            [McpInstallClient.Cursor],
            McpInstallScope.Project,
            root,
            includeHooks: false,
            CancellationToken.None);

        Assert.Equal("resolved", report.RepositoryIdentity);
        Assert.Equal(root, report.RepositoryRoot);
        var client = Assert.Single(report.Clients);
        Assert.Equal("registered", client.Registration);
        Assert.Equal(command, client.ConfiguredCommand);
        Assert.Equal("valid", client.CommandState);
        Assert.Equal("current", client.Instructions);
        Assert.Equal("unavailable", report.Daemon.State);
        Assert.Equal("unknown", report.Index.State);
        Assert.False(report.Hooks.Requested);
        Assert.True(report.RestartRequired);
    }

    [Fact]
    public async Task DiagnoseAsync_ReportsInvalidExecutableAndMissingManagedBlock()
    {
        var root = CreateTempDirectory();
        var missingCommand = Path.Combine(root, "missing-fuse.exe");
        await new McpInstallService().InstallAsync(
            [McpInstallClient.Codex],
            McpInstallScope.Project,
            root,
            missingCommand,
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        var report = await new McpDoctorService().DiagnoseAsync(
            [McpInstallClient.Codex],
            McpInstallScope.Project,
            root,
            includeHooks: true,
            CancellationToken.None);

        var client = Assert.Single(report.Clients);
        Assert.Equal("registered", client.Registration);
        Assert.Equal("invalid_command_path", client.CommandState);
        Assert.Equal("missing", client.Instructions);
        Assert.Equal("missing", report.Hooks.State);
        Assert.Contains(report.Issues, issue => issue.Contains("invalid command path", StringComparison.Ordinal));
        Assert.Contains(report.Issues, issue => issue.Contains("managed guidance is missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnoseAsync_ReportsUnresolvedProjectIdentityWithoutStartingDaemon()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-mcp-doctor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var report = await new McpDoctorService().DiagnoseAsync(
                [McpInstallClient.Cursor],
                McpInstallScope.Project,
                root,
                includeHooks: false,
                CancellationToken.None);

            Assert.Equal("unresolved", report.RepositoryIdentity);
            Assert.Equal("unavailable", report.Daemon.State);
            Assert.Equal("unavailable", report.Index.State);
            Assert.Contains(report.Issues, issue => issue.StartsWith("workspace_identity_unresolved:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fuse-mcp-doctor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    private sealed class RecordingConsoleUI : IConsoleUI
    {
        public void WriteSuccess(string message)
        {
        }

        public void WriteError(string message)
        {
        }

        public void WriteStep(string message)
        {
        }

        public void WriteResult(string message)
        {
        }
    }
}
