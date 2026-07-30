using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Fuse.Cli.Mcp;
using Fuse.Cli.Rpc;
using ModelContextProtocol.Server;
using StreamJsonRpc;
using Xunit;

namespace Fuse.Cli.Tests;

// R6 docs drift guard: every shipped MCP tool and host RPC request method must appear in the surface
// registry table on reference/api-surfaces.mdx so integrators have one contract page.
public sealed class ApiSurfacesDocParityTests
{
    private static readonly Regex RegistryRow = new(
        @"^\|\s*(mcp|rpc)\s*\|\s*`([^`]+)`\s*\|\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string? RepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    [Fact]
    public void Every_McpServerTool_and_JsonRpcMethod_appears_in_api_surfaces_doc()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        var pagePath = Path.Combine(root!, "site", "content", "docs", "reference", "api-surfaces.mdx");
        Assert.True(File.Exists(pagePath), $"api-surfaces page not found at {pagePath}");

        var page = File.ReadAllText(pagePath);
        var (mcpFromDoc, rpcFromDoc) = ParseSurfaceRegistry(page);

        var mcpFromCode = GetMcpToolNames();
        var rpcFromCode = GetJsonRpcMethodNames();

        foreach (var name in mcpFromCode)
            Assert.True(mcpFromDoc.Contains(name), $"MCP tool '{name}' is missing from the surface registry (docs/code drift).");

        foreach (var name in rpcFromCode)
            Assert.True(rpcFromDoc.Contains(name), $"Host RPC method '{name}' is missing from the surface registry (docs/code drift).");

        AssertExtraDocEntries(mcpFromDoc, mcpFromCode, "MCP");
        AssertExtraDocEntries(rpcFromDoc, rpcFromCode, "Host RPC");
    }

    private static void AssertExtraDocEntries(HashSet<string> fromDoc, HashSet<string> fromCode, string label)
    {
        foreach (var name in fromDoc)
            Assert.True(fromCode.Contains(name), $"{label} surface '{name}' is listed in the doc but has no code attribute.");
    }

    private static HashSet<string> GetMcpToolNames()
    {
        return typeof(FuseWorkspaceHandler).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null && !string.IsNullOrEmpty(a!.Name))
            .Select(a => a!.Name!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> GetJsonRpcMethodNames()
    {
        return typeof(FuseHostService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<JsonRpcMethodAttribute>())
            .Where(a => a is not null && !string.IsNullOrEmpty(a!.Name))
            .Select(a => a!.Name!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static (HashSet<string> Mcp, HashSet<string> Rpc) ParseSurfaceRegistry(string page)
    {
        var inRegistry = false;
        var mcp = new HashSet<string>(StringComparer.Ordinal);
        var rpc = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in page.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("## Surface registry", StringComparison.Ordinal))
            {
                inRegistry = true;
                continue;
            }

            if (inRegistry && trimmed.StartsWith("## ", StringComparison.Ordinal))
                break;

            if (!inRegistry)
                continue;

            var match = RegistryRow.Match(trimmed);
            if (!match.Success)
                continue;

            var surface = match.Groups[1].Value;
            var identifier = match.Groups[2].Value;
            if (surface == "mcp")
                mcp.Add(identifier);
            else
                rpc.Add(identifier);
        }

        Assert.NotEmpty(mcp);
        Assert.NotEmpty(rpc);
        return (mcp, rpc);
    }
}
