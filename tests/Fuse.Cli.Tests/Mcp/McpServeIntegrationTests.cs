using Fuse.Cli.Mcp;
using Fuse.Cli.Rpc;
using Fuse.Cli.Tests.Host;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Fuse.Cli.Tests.Mcp;

/// <summary>
///     End-to-end tests that spawn <c>fuse mcp serve</c> as a stdio subprocess and drive the MCP client protocol.
/// </summary>
public sealed class McpServeIntegrationTests
{
    private const int IndexStatusPollAttempts = 600;

    // The eight-tool loop surface plus fuse_reduce, the one out-of-loop utility (it compacts arbitrary files
    // and raw content, which fuse_context's indexed-seed emission does not cover). v4 is a clean-slate first
    // public release (D14): no deprecation shims, no legacy names - the surface is exactly these nine tools.
    private static readonly string[] ExpectedToolNames =
    [
        "fuse_check",
        "fuse_context",
        "fuse_find",
        "fuse_impact",
        "fuse_reduce",
        "fuse_refactor",
        "fuse_review",
        "fuse_test",
        "fuse_workspace",
    ];

    [Fact]
    public async Task StdioServer_ListsExactlyTheNineLoopTools_AndFuseWorkspaceMapReturnsIndexedSymbols()
    {
        using var fixture = new McpFixtureProject();
        fixture.AddFile("Services/WidgetService.cs", """
            namespace App;
            public class WidgetService
            {
                public void Spin() { }
            }
            """);

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = DotNetExecutablePath(),
            Arguments = [FuseAssemblyPath(), "mcp", "serve"],
            Name = "fuse-mcp-integration-test",
        });

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestCancellation);

        var tools = await client.ListToolsAsync(cancellationToken: TestCancellation);
        var expected = ExpectedToolNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // fuse_workspace action=status stamps the verification grade and index availability (R3/T0).
        var status = await client.CallToolAsync(
            "fuse_workspace",
            new Dictionary<string, object?> { ["action"] = "status", ["path"] = fixture.ProjectPath },
            cancellationToken: TestCancellation);
        var statusText = TextContent(status);
        Assert.Contains("index_state:", statusText);
        Assert.Contains("availability:", statusText);
        Assert.Contains("index mode", statusText);
        Assert.Contains("tier-1 build capture", statusText);
        Assert.Contains("verify serves", statusText);
        Assert.Contains("full-text search:", statusText);

        // Start the daemon-owned job explicitly before asserting map content. A map request may correctly return
        // a building_syntax availability header when its bounded MCP read wait expires under parallel test load.
        var indexResult = await client.CallToolAsync(
            "fuse_workspace",
            new Dictionary<string, object?> { ["action"] = "index", ["path"] = fixture.ProjectPath },
            cancellationToken: TestCancellation);
        Assert.Contains("index job:", TextContent(indexResult));

        string indexStatus = string.Empty;
        for (var attempt = 0; attempt < IndexStatusPollAttempts; attempt++)
        {
            var indexStatusResult = await client.CallToolAsync(
                "fuse_workspace",
                new Dictionary<string, object?> { ["action"] = "status", ["path"] = fixture.ProjectPath },
                cancellationToken: TestCancellation);
            indexStatus = TextContent(indexStatusResult);
            if (indexStatus.Contains("index job state: Completed", StringComparison.Ordinal)
                || indexStatus.Contains("index job state: Failed", StringComparison.Ordinal)
                || indexStatus.Contains("index job state: Cancelled", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(100, TestCancellation);
        }

        Assert.Contains("index job state: Completed", indexStatus);

        // The fixture is a Git repo, so its completed store stays inside the fixture directory.
        var result = await client.CallToolAsync(
            "fuse_workspace",
            new Dictionary<string, object?> { ["action"] = "map", ["path"] = fixture.ProjectPath, ["detail"] = "symbols" },
            cancellationToken: TestCancellation);

        var text = TextContent(result);
        Assert.Contains("workspace map", text);
        Assert.Contains("WidgetService", text);

        // The fuse_find union works over the wire: kind=symbol is exact lookup; the union kinds route to the
        // formerly-separate engines (kind=task ranks candidates).
        var findSymbol = await client.CallToolAsync(
            "fuse_find",
            new Dictionary<string, object?> { ["path"] = fixture.ProjectPath, ["query"] = "WidgetService", ["kind"] = "symbol" },
            cancellationToken: TestCancellation);
        Assert.Contains("WidgetService", TextContent(findSymbol));

        var findTask = await client.CallToolAsync(
            "fuse_find",
            new Dictionary<string, object?> { ["path"] = fixture.ProjectPath, ["query"] = "spin the widget", ["kind"] = "task" },
            cancellationToken: TestCancellation);
        Assert.Contains("localize", TextContent(findTask));

        // (fuse_review --handoff (U2) is not exercised over the MCP subprocess here: calling it spawns git in the
        // test-host+subprocess combo, which crashes the test host in this environment - the same git-process
        // fragility the Fuse.Fusion GitStats test hits. The handoff carries a top-level guard that turns any such
        // failure into a graceful abstention string; its red-gate decision is a simple introduced-error count.)

        // fuse_impact carries the T2 public-surface line: a public type is flagged as external-facing so the agent
        // knows a change to it is contract-relevant before editing.
        var impact = await client.CallToolAsync(
            "fuse_impact",
            new Dictionary<string, object?> { ["path"] = fixture.ProjectPath, ["symbol"] = "WidgetService" },
            cancellationToken: TestCancellation);

        var impactText = TextContent(impact);
        Assert.Contains("public API surface:", impactText);
        Assert.Contains("WidgetService is on the public/protected surface", impactText);
        // fuse_impact carries the U2 graded claims block: its statements each carry a grade and an evidence ref,
        // and graph-grade claims are capped at partially verified (not compiler-confirmed).
        Assert.Contains("claims (", impactText);
        Assert.Contains("partially verified", impactText);

        // U3: the playbook prompts are registered and selectable by name.
        var prompts = await client.ListPromptsAsync(cancellationToken: TestCancellation);
        Assert.Equal(
            ["add-endpoint", "fix-build-error", "implement-feature", "rename-symbol", "review-pr"],
            prompts.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // A selected prompt expands with its anchor argument into the loop-shaped playbook.
        var fixPrompt = await client.GetPromptAsync(
            "fix-build-error",
            new Dictionary<string, object?> { ["diagnosticId"] = "CS1061" },
            cancellationToken: TestCancellation);
        var promptText = string.Concat(
            fixPrompt.Messages.Select(m => (m.Content as TextContentBlock)?.Text));
        Assert.Contains("CS1061", promptText);
        Assert.Contains("fuse_check", promptText);

        // U3: the addressable resources include the U2 session ledger and the new status/diff/diagnostics reads,
        // alongside the map/localize/context/review workflow resources.
        var resourceTemplates = await client.ListResourceTemplatesAsync(cancellationToken: TestCancellation);
        var templateUris = resourceTemplates.Select(r => r.UriTemplate).ToArray();
        Assert.Contains(templateUris, u => u.StartsWith("fuse://ledger/", StringComparison.Ordinal));
        Assert.Contains(templateUris, u => u.StartsWith("fuse://status/", StringComparison.Ordinal));
        Assert.Contains(templateUris, u => u.StartsWith("fuse://diff/", StringComparison.Ordinal));
        Assert.Contains(templateUris, u => u.StartsWith("fuse://diagnostics/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StdioServer_FuseFind_returns_updated_symbol_after_on_disk_edit()
    {
        using var fixture = new McpFixtureProject();
        fixture.AddFile("Services/WidgetService.cs", """
            namespace App;
            public class WidgetService
            {
                public void Spin() { }
            }
            """);

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = DotNetExecutablePath(),
            Arguments = [FuseAssemblyPath(), "mcp", "serve"],
            Name = "fuse-mcp-freshness-test",
        });

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestCancellation);

        var indexResult = await client.CallToolAsync(
            "fuse_workspace",
            new Dictionary<string, object?> { ["action"] = "index", ["path"] = fixture.ProjectPath },
            cancellationToken: TestCancellation);
        Assert.Contains("index job:", TextContent(indexResult));
        Assert.Contains("index job owner: daemon", TextContent(indexResult));

        string statusText = string.Empty;
        for (var attempt = 0; attempt < IndexStatusPollAttempts; attempt++)
        {
            var status = await client.CallToolAsync(
                "fuse_workspace",
                new Dictionary<string, object?> { ["action"] = "status", ["path"] = fixture.ProjectPath },
                cancellationToken: TestCancellation);
            statusText = TextContent(status);
            if (statusText.Contains("index job state: Completed", StringComparison.Ordinal))
                break;
            if (statusText.Contains("index job state: Failed", StringComparison.Ordinal)
                || statusText.Contains("index job state: Cancelled", StringComparison.Ordinal))
                break;
            await Task.Delay(100, TestCancellation);
        }

        Assert.Contains("index job state: Completed", statusText);

        var beforeEdit = await client.CallToolAsync(
            "fuse_find",
            new Dictionary<string, object?> { ["path"] = fixture.ProjectPath, ["query"] = "GearboxService", ["kind"] = "symbol" },
            cancellationToken: TestCancellation);
        Assert.DoesNotContain("GearboxService", TextContent(beforeEdit));

        fixture.AddFile("Services/WidgetService.cs", """
            namespace App;
            public class WidgetService
            {
                public void Spin() { }
            }
            public class GearboxService
            {
                public void Engage() { }
            }
            """);

        var afterEdit = await client.CallToolAsync(
            "fuse_find",
            new Dictionary<string, object?> { ["path"] = fixture.ProjectPath, ["query"] = "GearboxService", ["kind"] = "symbol" },
            cancellationToken: TestCancellation);
        if (!TextContent(afterEdit).Contains("GearboxService", StringComparison.Ordinal))
        {
            statusText = string.Empty;
            for (var attempt = 0; attempt < IndexStatusPollAttempts; attempt++)
            {
                var status = await client.CallToolAsync(
                    "fuse_workspace",
                    new Dictionary<string, object?> { ["action"] = "status", ["path"] = fixture.ProjectPath },
                    cancellationToken: TestCancellation);
                statusText = TextContent(status);
                if (statusText.Contains("index job state: Completed", StringComparison.Ordinal)
                    || statusText.Contains("index job state: Failed", StringComparison.Ordinal)
                    || statusText.Contains("index job state: Cancelled", StringComparison.Ordinal))
                    break;
                await Task.Delay(100, TestCancellation);
            }

            Assert.Contains("index job state: Completed", statusText);
            afterEdit = await client.CallToolAsync(
                "fuse_find",
                new Dictionary<string, object?> { ["path"] = fixture.ProjectPath, ["query"] = "GearboxService", ["kind"] = "symbol" },
                cancellationToken: TestCancellation);
        }

        Assert.Contains("GearboxService", TextContent(afterEdit));
    }

    private static CancellationToken TestCancellation => default;

    private static string DotNetExecutablePath() => "dotnet";

    private static string FuseAssemblyPath()
    {
        var location = typeof(FuseWorkspaceHandler).Assembly.Location;
        return File.Exists(location)
            ? location
            : throw new InvalidOperationException($"Could not locate fuse.dll for MCP integration tests at '{location}'.");
    }

    private static string TextContent(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private sealed class McpFixtureProject : IDisposable
    {
        public string ProjectPath { get; } =
            Path.Combine(Path.GetTempPath(), "fuse-mcp-serve-tests", Guid.NewGuid().ToString("N"));

        public McpFixtureProject()
        {
            Directory.CreateDirectory(ProjectPath);
            // Initialize a git repo so FuseStorePaths resolves the index to {ProjectPath}/.fuse, isolating the
            // store from the machine-wide ~/.fuse used for non-git directories.
            RunGit("init");
        }

        private void RunGit(string arguments)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = ProjectPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                process?.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Git not on PATH: the store falls back to the machine-wide location; the test still functions.
            }
        }

        public void AddFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(ProjectPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose()
        {
            // The serve subprocess may run a background semantic upgrade that holds the index db open briefly; on
            // shutdown the OS releases that handle a moment after the process is signalled, so retry the delete
            // through the transient IOException rather than failing teardown on the race.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(ProjectPath))
                        Directory.Delete(ProjectPath, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
