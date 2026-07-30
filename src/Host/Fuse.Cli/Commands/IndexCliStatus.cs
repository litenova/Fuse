using Fuse.Cli.Mcp;

namespace Fuse.Cli.Commands;

/// <summary>The JSON and human status payload for index lifecycle commands.</summary>
/// <param name="Status">The status command outcome.</param>
/// <param name="Job">The retained index job, when one exists.</param>
/// <param name="Storage">The current derived-index storage usage.</param>
/// <param name="Store">The persisted index state and counts.</param>
public sealed record IndexCliStatus(
    string Status,
    IndexJobSnapshot? Job,
    IndexStorageSnapshot Storage,
    IndexStoreStatus Store);
