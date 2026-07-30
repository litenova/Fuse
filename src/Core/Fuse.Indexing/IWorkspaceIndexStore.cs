namespace Fuse.Indexing;

/// <summary>
///     The complete persistent workspace index contract. New application services should depend on one of its
///     focused parent ports instead of this aggregate when they do not need the full store surface.
/// </summary>
public interface IWorkspaceIndexStore :
    IWorkspaceIndexLifecycleStore,
    IWorkspaceIndexWriteStore,
    IWorkspaceIndexQueryStore,
    IWorkspaceIndexGraphStore,
    IWorkspaceIndexMetadataStore,
    IWorkspaceIndexVerificationSessionStore,
    IAsyncDisposable
{
}

/// <summary>
///     One session the store knows for a root: its id, latest write time, and persisted diagnostic and claim data.
/// </summary>
/// <param name="SessionId">The opaque session id.</param>
/// <param name="UpdatedUtc">The ISO-8601 UTC time the session was last written.</param>
/// <param name="HasBaseline">Whether the session has a recorded check-diagnostics baseline.</param>
/// <param name="HasClaims">Whether the session has an accumulated claim ledger.</param>
public sealed record SessionSummary(string SessionId, string UpdatedUtc, bool HasBaseline, bool HasClaims);

/// <summary>
///     A persisted check-session baseline: diagnostics at the session's start or latest mark-green.
/// </summary>
/// <param name="SessionId">The opaque session id.</param>
/// <param name="Root">The absolute workspace root the session is rooted at.</param>
/// <param name="Diagnostics">The diagnostics captured for the baseline.</param>
/// <param name="UpdatedUtc">The ISO-8601 UTC time the baseline was last written.</param>
public sealed record CheckSessionBaseline(
    string SessionId, string Root, IReadOnlyList<CheckDiagnostic> Diagnostics, string UpdatedUtc);

/// <summary>
///     A persisted claims ledger: an opaque retrieval-layer JSON payload with its workspace and write time.
/// </summary>
/// <param name="SessionId">The opaque session id.</param>
/// <param name="Root">The absolute workspace root the session is rooted at.</param>
/// <param name="ClaimsJson">The serialized claims payload.</param>
/// <param name="UpdatedUtc">The ISO-8601 UTC time the ledger was last written.</param>
public sealed record ClaimLedgerRecord(string SessionId, string Root, string ClaimsJson, string UpdatedUtc);
