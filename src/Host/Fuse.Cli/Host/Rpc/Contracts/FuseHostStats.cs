namespace Fuse.Cli.Rpc;

/// <summary>
///     The result of the <c>fuse/stats</c> method: cheap process-level health for a status readout. It carries the
///     host liveness fields that need no scoping run.
/// </summary>
/// <param name="HostVersion">The host package version.</param>
/// <param name="ProcessId">The host operating-system process id.</param>
/// <param name="UptimeMs">Milliseconds since the host started serving.</param>
/// <param name="WorkingSetBytes">The host process working-set size in bytes, reported as host RSS.</param>
public sealed record FuseHostStats(string HostVersion, int ProcessId, long UptimeMs, long WorkingSetBytes);
