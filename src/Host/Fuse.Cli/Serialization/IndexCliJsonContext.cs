using System.Text.Json.Serialization;
using Fuse.Cli.Commands;
using Fuse.Cli.Mcp;

namespace Fuse.Cli.Serialization;

/// <summary>Source-generated JSON metadata for the public index CLI lifecycle output.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IndexJobSnapshot))]
[JsonSerializable(typeof(IndexCountSnapshot))]
[JsonSerializable(typeof(IndexStorageSnapshot))]
[JsonSerializable(typeof(IndexStoreStatus))]
[JsonSerializable(typeof(IndexCliError))]
[JsonSerializable(typeof(IndexCliStatus))]
internal sealed partial class IndexCliJsonContext : JsonSerializerContext;
