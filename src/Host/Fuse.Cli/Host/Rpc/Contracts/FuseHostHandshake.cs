namespace Fuse.Cli.Rpc;

/// <summary>
///     The result of the <c>fuse/handshake</c> method: the host's package version, the wire protocol version
///     the client must match, and the session token required on every subsequent RPC call. A protocol mismatch is
///     surfaced as a clear notification rather than failing later with an opaque serialization error.
/// </summary>
/// <param name="HostVersion">The host package version (for example <c>4.4.0</c>).</param>
/// <param name="ProtocolVersion">The RPC protocol version; the client compares it to its own.</param>
/// <param name="SessionToken">The session token the client must pass on all RPC methods except handshake.</param>
public sealed record FuseHostHandshake(string HostVersion, int ProtocolVersion, string SessionToken);
