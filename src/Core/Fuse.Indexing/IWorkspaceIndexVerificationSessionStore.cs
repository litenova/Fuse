namespace Fuse.Indexing;

/// <summary>
///     Persists diagnostic baselines and retrieval claim ledgers for an agent or editor session.
/// </summary>
public interface IWorkspaceIndexVerificationSessionStore
{
    /// <summary>Saves or replaces a session's diagnostic baseline.</summary>
    /// <param name="sessionId">The opaque session id.</param>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="baseline">The diagnostic baseline.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the baseline is persisted.</returns>
    Task SaveCheckSessionBaselineAsync(
        string sessionId, string root, IReadOnlyList<CheckDiagnostic> baseline, CancellationToken cancellationToken);

    /// <summary>Gets a session's diagnostic baseline.</summary>
    /// <param name="sessionId">The opaque session id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The baseline, or null when the session is unknown.</returns>
    Task<CheckSessionBaseline?> GetCheckSessionBaselineAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Saves or replaces a session's opaque retrieval claim ledger.</summary>
    /// <param name="sessionId">The opaque session id.</param>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="claimsJson">The serialized retrieval claim payload.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the ledger is persisted.</returns>
    Task SaveClaimLedgerAsync(string sessionId, string root, string claimsJson, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>Gets a session's opaque retrieval claim ledger.</summary>
    /// <param name="sessionId">The opaque session id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The ledger, or null when the session is unknown.</returns>
    Task<ClaimLedgerRecord?> GetClaimLedgerAsync(string sessionId, CancellationToken cancellationToken)
        => Task.FromResult<ClaimLedgerRecord?>(null);

    /// <summary>Lists sessions known for a workspace root.</summary>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>Known session summaries, newest first.</returns>
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string root, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SessionSummary>>([]);
}
