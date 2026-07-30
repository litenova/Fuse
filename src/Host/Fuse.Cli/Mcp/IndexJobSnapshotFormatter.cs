using System.Text;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Renders an index job snapshot as the human-readable block the workspace tool and CLI both print.
/// </summary>
internal static class IndexJobSnapshotFormatter
{
    /// <summary>Formats one job snapshot.</summary>
    /// <param name="snapshot">The job snapshot to render.</param>
    /// <param name="joined">Whether the request joined an existing job, or null when that is not applicable.</param>
    /// <param name="usesDaemon">Whether the job is owned by the shared daemon rather than this process.</param>
    /// <returns>The rendered block, without a trailing newline.</returns>
    internal static string Format(IndexJobSnapshot snapshot, bool? joined, bool usesDaemon)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"index job: {snapshot.JobId}");
        builder.AppendLine($"index job owner: {(usesDaemon ? "daemon" : "in-process")}");
        if (joined is not null)
            builder.AppendLine($"index job joined: {joined.Value.ToString().ToLowerInvariant()}");
        builder.AppendLine($"index job state: {snapshot.State}");
        builder.AppendLine($"index job phase: {snapshot.Phase} ({snapshot.PhaseNumber}/{snapshot.PhaseCount})");
        builder.AppendLine(snapshot.TotalUnits is null
            ? $"index job progress: {snapshot.CompletedUnits} units"
            : $"index job progress: {snapshot.CompletedUnits}/{snapshot.TotalUnits} ({snapshot.PhasePercent ?? 0:F0}%)");
        if (!string.IsNullOrWhiteSpace(snapshot.CurrentItem))
            builder.AppendLine($"index job current item: {snapshot.CurrentItem}");
        builder.AppendLine(
            $"index storage: database {snapshot.Storage.DatabaseBytes} bytes, WAL {snapshot.Storage.WalBytes} bytes, "
            + $"total {snapshot.Storage.TotalFuseBytes} bytes");
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorCode))
            builder.AppendLine($"index job error: {snapshot.ErrorCode}: {snapshot.ErrorMessage}");
        if (snapshot.State is IndexJobState.Queued or IndexJobState.Running or IndexJobState.Cancelling)
            builder.AppendLine("next_action: call fuse_workspace with action=status for progress, or action=cancel to stop the job.");
        return builder.ToString().TrimEnd();
    }
}
