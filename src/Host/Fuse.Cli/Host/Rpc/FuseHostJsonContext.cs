using System.Text.Json.Serialization;
using Fuse.Cli.Mcp;

namespace Fuse.Cli.Rpc;

/// <summary>
///     The source-generated JSON serialization context for every host RPC DTO. The host transport is configured
///     to use this resolver so all UI-facing JSON is reflection-free, satisfying the project invariant that JSON
///     goes through a <see cref="JsonSerializerContext" /> only. <c>protocol.ts</c> in the extension mirrors
///     these shapes and is pinned by a contract test.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FuseHostHandshake))]
[JsonSerializable(typeof(FuseHostStats))]
[JsonSerializable(typeof(GraphDto))]
[JsonSerializable(typeof(GraphNodeDto))]
[JsonSerializable(typeof(GraphEdgeDto))]
[JsonSerializable(typeof(IndexResultDto))]
[JsonSerializable(typeof(IndexJobStartResult))]
[JsonSerializable(typeof(IndexJobSnapshot))]
[JsonSerializable(typeof(IndexJobRequest))]
[JsonSerializable(typeof(IndexCountSnapshot))]
[JsonSerializable(typeof(IndexStorageSnapshot))]
[JsonSerializable(typeof(LanguageCountDto))]
[JsonSerializable(typeof(ScopeResultDto))]
[JsonSerializable(typeof(ScopeFileDto))]
[JsonSerializable(typeof(DiagnosticsDto))]
[JsonSerializable(typeof(SecretDiagnosticDto))]
[JsonSerializable(typeof(HotspotDiagnosticDto))]
[JsonSerializable(typeof(ExplainResultDto))]
[JsonSerializable(typeof(ExplainFileDto))]
[JsonSerializable(typeof(CheckDeltaDto))]
[JsonSerializable(typeof(CheckDiagnosticDto))]
[JsonSerializable(typeof(CheckOverlayResultDto))]
[JsonSerializable(typeof(OpenIndexedResultDto))]
[JsonSerializable(typeof(DoctorResultDto))]
[JsonSerializable(typeof(RefactorRequestDto))]
[JsonSerializable(typeof(RefactorResultDto))]
[JsonSerializable(typeof(CaptureCheckResultDto))]
// StreamJsonRpc serializes a failed method call's error payload as CommonErrorData through this same
// source-generated resolver; without it, any RPC error throws NotSupportedException instead of propagating.
[JsonSerializable(typeof(StreamJsonRpc.Protocol.CommonErrorData))]
public sealed partial class FuseHostJsonContext : JsonSerializerContext;
