namespace Fuse.Cli.Mcp;

/// <summary>
///     The <c>index_meta</c> keys an index job writes so its lifecycle survives a process restart.
/// </summary>
/// <remarks>
///     The executor stamps the current job's identity, phase, depth, and terminal state; a later job that finds a
///     non-terminal stamp from a different job id copies it to the <c>Interrupted*</c> keys before overwriting, so
///     an interrupted run stays visible in status rather than being silently replaced.
/// </remarks>
internal static class IndexJobMetaKeys
{
    /// <summary>The identifier of the job that last wrote to the store.</summary>
    internal const string JobId = "index_job_id";

    /// <summary>The phase the current job last reported.</summary>
    internal const string Phase = "index_job_phase";

    /// <summary>The depth the current job requested.</summary>
    internal const string Depth = "index_job_depth";

    /// <summary>When the current job started, in round-trip format.</summary>
    internal const string StartedAt = "index_job_started_at";

    /// <summary>The current job's lifecycle state: running, completed, cancelled, or failed.</summary>
    internal const string State = "index_job_state";

    /// <summary>The stable error code of a failed job.</summary>
    internal const string ErrorCode = "index_job_error_code";

    /// <summary>The recovery message of a failed job.</summary>
    internal const string ErrorMessage = "index_job_error_message";

    /// <summary>The identifier of a job that stopped without recording a terminal state.</summary>
    internal const string InterruptedJobId = "index_interrupted_job_id";

    /// <summary>The phase an interrupted job last reported.</summary>
    internal const string InterruptedPhase = "index_interrupted_phase";

    /// <summary>The depth an interrupted job requested.</summary>
    internal const string InterruptedDepth = "index_interrupted_depth";

    /// <summary>When an interrupted job started.</summary>
    internal const string InterruptedStartedAt = "index_interrupted_started_at";

    /// <summary>The marker recording that a previous job was interrupted.</summary>
    internal const string InterruptedState = "index_interrupted_state";
}
