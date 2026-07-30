namespace Fuse.Cli.Services;

/// <summary>
///     The complete diagnostic result produced by <c>fuse mcp doctor</c>.
/// </summary>
/// <param name="RunningVersion">The version of the binary running the doctor command.</param>
/// <param name="RunningExecutable">The executable selected by the running binary.</param>
/// <param name="PathExecutable">The <c>fuse</c> executable discovered on PATH, when present.</param>
/// <param name="Scope">The checked registration scope.</param>
/// <param name="RepositoryIdentity">Whether the requested directory resolved to a Git repository.</param>
/// <param name="RepositoryRoot">The resolved repository root, when available.</param>
/// <param name="Clients">The selected client registration and instruction results.</param>
/// <param name="Daemon">The daemon handshake result for the resolved repository.</param>
/// <param name="Index">The current index-job result for the resolved repository.</param>
/// <param name="Hooks">The optional Claude hook result.</param>
/// <param name="RestartRequired">Whether a configured client must restart before it reads the registration.</param>
/// <param name="Issues">Actionable diagnostic issues found during the check.</param>
public sealed record McpDoctorReport(
    string RunningVersion,
    string RunningExecutable,
    string? PathExecutable,
    string Scope,
    string RepositoryIdentity,
    string? RepositoryRoot,
    McpDoctorClientReport[] Clients,
    McpDoctorDaemonReport Daemon,
    McpDoctorIndexReport Index,
    McpDoctorHooksReport Hooks,
    bool RestartRequired,
    string[] Issues);

/// <summary>
///     Registration and instruction state for one MCP client.
/// </summary>
/// <param name="Client">The documented client name.</param>
/// <param name="ConfigPath">The client configuration path, when this scope has one.</param>
/// <param name="Registration">The registration state.</param>
/// <param name="ConfiguredCommand">The executable recorded in the Fuse server entry, when found.</param>
/// <param name="CommandState">Whether the recorded executable is valid, missing, or cannot be verified.</param>
/// <param name="InstructionPath">The managed instruction file path, when this scope has one.</param>
/// <param name="Instructions">The managed v4.4 instruction-block state.</param>
/// <param name="Detail">An actionable detail for a missing or malformed item.</param>
public sealed record McpDoctorClientReport(
    string Client,
    string? ConfigPath,
    string Registration,
    string? ConfiguredCommand,
    string CommandState,
    string? InstructionPath,
    string Instructions,
    string? Detail);

/// <summary>
///     Daemon reachability and protocol compatibility for a repository.
/// </summary>
/// <param name="State">The daemon state: unavailable, reachable, or protocol_mismatch.</param>
/// <param name="Version">The daemon version, when a handshake succeeded.</param>
/// <param name="ProtocolVersion">The daemon protocol version, when a handshake succeeded.</param>
/// <param name="ProcessId">The daemon process identifier when known.</param>
/// <param name="Detail">An actionable detail for an unavailable or stale daemon.</param>
public sealed record McpDoctorDaemonReport(
    string State,
    string? Version,
    int? ProtocolVersion,
    int? ProcessId,
    string? Detail);

/// <summary>
///     Retained repository index-job state exposed without starting a daemon.
/// </summary>
/// <param name="State">The index state, or unavailable when repository identity was unresolved.</param>
/// <param name="JobId">The active or retained job identifier.</param>
/// <param name="JobState">The active or retained job state.</param>
/// <param name="Phase">The active or retained job phase.</param>
/// <param name="Detail">An actionable detail when no remote index status was available.</param>
public sealed record McpDoctorIndexReport(
    string State,
    string? JobId,
    string? JobState,
    string? Phase,
    string? Detail);

/// <summary>
///     Claude ambient-verification hook state requested by the caller.
/// </summary>
/// <param name="Requested">Whether the doctor was asked to inspect hooks.</param>
/// <param name="State">The hook state: not_requested, installed, missing, or unavailable.</param>
/// <param name="SettingsPath">The checked settings path, when applicable.</param>
/// <param name="Detail">An actionable detail for a missing hook.</param>
public sealed record McpDoctorHooksReport(bool Requested, string State, string? SettingsPath, string? Detail);
