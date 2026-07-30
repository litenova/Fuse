using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Cli.Configuration.McpInstall;
using Fuse.Cli.Commands;
using Fuse.Cli.Mcp;
using Fuse.Cli.Rpc;
using Fuse.Cli.Services;

namespace Fuse.Cli.Serialization;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(ClaudeMcpConfig))]
[JsonSerializable(typeof(CursorMcpConfig))]
[JsonSerializable(typeof(CopilotMcpConfig))]
[JsonSerializable(typeof(LocalArrayMcpConfig))]
[JsonSerializable(typeof(LocalArrayMcpServer))]
[JsonSerializable(typeof(McpDoctorReport))]
[JsonSerializable(typeof(RaceCandidateInput[]))]
[JsonSerializable(typeof(DaemonDescriptor))]
[JsonSerializable(typeof(WarmServiceRecent))]
internal partial class FuseCliJsonContext : JsonSerializerContext;
