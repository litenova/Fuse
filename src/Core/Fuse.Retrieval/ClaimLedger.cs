using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Indexing;

namespace Fuse.Retrieval;

/// <summary>
///     The grade of a claim Fuse emits in an answer (U2, the metrics dictionary). Grades are computed from the
///     evidence available, never asserted, so an agent and its human know which statements are compiler- or
///     test-grade truth and which are weaker inferences that may go stale or be contradicted.
/// </summary>
public enum ClaimGrade
{
    /// <summary>Backed by compiler- or test-grade evidence (a diagnostic, a build, a test verdict): the strongest grade.</summary>
    Verified,

    /// <summary>Backed by the persisted graph only (an edge, a stored flag): real signal, but not compiler-confirmed; the graph-grade cap.</summary>
    PartiallyVerified,

    /// <summary>The evidence a claim rested on has changed since the claim was computed (a watcher-known edit to the evidence file).</summary>
    Stale,

    /// <summary>A claim made earlier in a session conflicts with the current truth; both sides are cited.</summary>
    Contradicted,
}

/// <summary>
///     One graded claim in an answer (U2): a statement Fuse computed, its grade, and a reference to the evidence
///     that grade rests on. Grades attach only to statements Fuse emitted, never to prose a model wrote.
/// </summary>
/// <param name="Statement">The claim, as a short sentence Fuse computed.</param>
/// <param name="Grade">The computed grade.</param>
/// <param name="Evidence">A reference to the evidence (a file:line, a diagnostic id, a symbol id, a test id).</param>
public sealed record Claim(string Statement, ClaimGrade Grade, string Evidence)
{
    /// <summary>
    ///     Creates a claim from graph-grade evidence, capped at <see cref="ClaimGrade.PartiallyVerified" /> because
    ///     the persisted graph is real signal but not compiler-confirmed (the grade-inflation kill-risk mitigation).
    /// </summary>
    /// <param name="statement">The claim.</param>
    /// <param name="evidence">The graph evidence reference.</param>
    /// <returns>A partially-verified claim.</returns>
    public static Claim FromGraph(string statement, string evidence) => new(statement, ClaimGrade.PartiallyVerified, evidence);

    /// <summary>
    ///     Creates a claim from compiler- or test-grade evidence (a diagnostic, a build, a test verdict), graded
    ///     <see cref="ClaimGrade.Verified" />.
    /// </summary>
    /// <param name="statement">The claim.</param>
    /// <param name="evidence">The compiler/test evidence reference.</param>
    /// <returns>A verified claim.</returns>
    public static Claim FromCompiler(string statement, string evidence) => new(statement, ClaimGrade.Verified, evidence);

    /// <summary>
    ///     Creates a contradicted claim (U2): a statement made earlier in a session that the current truth now
    ///     conflicts with, citing both sides so neither is hidden.
    /// </summary>
    /// <param name="statement">The earlier claim.</param>
    /// <param name="was">What was claimed (the session side).</param>
    /// <param name="now">What is true now (the current side).</param>
    /// <returns>A contradicted claim citing both sides.</returns>
    public static Claim Contradicted(string statement, string was, string now) =>
        new(statement, ClaimGrade.Contradicted, $"was: {was}; now: {now}");
}

/// <summary>
///     Re-grades claims accumulated in a session against the current truth (U2): a claim goes
///     <see cref="ClaimGrade.Stale" /> when the evidence it rested on has changed since it was computed, and
///     <see cref="ClaimGrade.Contradicted" /> when the current truth conflicts with it. This is the pure transition
///     logic the session ledger applies when it re-reads accumulated claims; it holds no state itself.
/// </summary>
public static class ClaimReviewer
{
    /// <summary>
    ///     Re-grades a claim to <see cref="ClaimGrade.Stale" /> when its evidence file has changed since the claim
    ///     was computed. An already-stale or contradicted claim is returned unchanged (a terminal grade does not
    ///     revert), and a claim whose evidence has not changed keeps its grade.
    /// </summary>
    /// <param name="claim">The claim to review.</param>
    /// <param name="evidenceChanged">Whether the evidence the claim rested on has changed since it was computed (watcher-known).</param>
    /// <returns>The claim, re-graded stale when its evidence changed, else unchanged.</returns>
    public static Claim Regrade(Claim claim, bool evidenceChanged)
    {
        if (!evidenceChanged)
            return claim;
        if (claim.Grade is ClaimGrade.Stale or ClaimGrade.Contradicted)
            return claim;
        return claim with { Grade = ClaimGrade.Stale, Evidence = $"{claim.Evidence} (stale: evidence changed since computed)" };
    }
}

/// <summary>
///     Renders a set of graded claims as the text block appended to an answer (U2). The block is a plain,
///     scannable section - one line per claim with its grade and evidence - matching the availability-header and
///     API-surface lines the read tools already emit, since the tools return rendered strings rather than a
///     structured envelope.
/// </summary>
public static class ClaimLedger
{
    /// <summary>
    ///     Renders the claims block, or an empty string when there are no claims.
    /// </summary>
    /// <param name="claims">The graded claims.</param>
    /// <returns>The rendered block (a header plus one line per claim), or empty when there are none.</returns>
    public static string Render(IReadOnlyList<Claim> claims)
    {
        if (claims.Count == 0)
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"claims ({claims.Count}, each graded and evidence-referenced):");
        foreach (var claim in claims)
            builder.AppendLine($"  [{GradeLabel(claim.Grade)}] {claim.Statement}  (evidence: {claim.Evidence})");
        return builder.ToString().TrimEnd();
    }

    private static string GradeLabel(ClaimGrade grade) => grade switch
    {
        ClaimGrade.Verified => "verified",
        ClaimGrade.PartiallyVerified => "partially verified",
        ClaimGrade.Stale => "stale",
        ClaimGrade.Contradicted => "contradicted",
        _ => "unknown",
    };
}

/// <summary>Source-generated JSON context for the claims ledger payload (the reflection-free serialization invariant).</summary>
[JsonSerializable(typeof(List<Claim>))]
public sealed partial class ClaimJsonContext : JsonSerializerContext;

/// <summary>
///     Persists and reloads a session's accumulated claims through the index store (U2), serializing the claim
///     shape the store keeps opaque. The reloaded claims are the substrate the session-ledger resource exposes and
///     that <see cref="ClaimReviewer" /> re-grades against current truth.
/// </summary>
public static class SessionClaimLedger
{
    /// <summary>Persists a session's claims.</summary>
    /// <param name="store">The index store.</param>
    /// <param name="sessionId">The opaque session id.</param>
    /// <param name="root">The workspace root.</param>
    /// <param name="claims">The claims to persist.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the claims are persisted.</returns>
    public static Task SaveAsync(
        IWorkspaceIndexVerificationSessionStore store,
        string sessionId,
        string root,
        IReadOnlyList<Claim> claims,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(claims.ToList(), ClaimJsonContext.Default.ListClaim);
        return store.SaveClaimLedgerAsync(sessionId, root, json, cancellationToken);
    }

    /// <summary>
    ///     Appends claims to a session's ledger, accumulating across the session (load the existing claims, add the
    ///     new ones, save). This is how a claim-emitting tool contributes to the running ledger without discarding
    ///     what earlier calls recorded.
    /// </summary>
    /// <param name="store">The index store.</param>
    /// <param name="sessionId">The opaque session id.</param>
    /// <param name="root">The workspace root.</param>
    /// <param name="newClaims">The claims to append.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the appended ledger is persisted.</returns>
    public static async Task AppendAsync(
        IWorkspaceIndexVerificationSessionStore store,
        string sessionId,
        string root,
        IReadOnlyList<Claim> newClaims,
        CancellationToken cancellationToken)
    {
        if (newClaims.Count == 0)
            return;
        var existing = await LoadAsync(store, sessionId, cancellationToken);
        await SaveAsync(store, sessionId, root, existing.Concat(newClaims).ToList(), cancellationToken);
    }

    /// <summary>Loads a session's persisted claims, or an empty list when the session is unknown.</summary>
    /// <param name="store">The index store.</param>
    /// <param name="sessionId">The opaque session id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The persisted claims, or empty when the session has none.</returns>
    public static async Task<IReadOnlyList<Claim>> LoadAsync(
        IWorkspaceIndexVerificationSessionStore store, string sessionId, CancellationToken cancellationToken)
    {
        var record = await store.GetClaimLedgerAsync(sessionId, cancellationToken);
        if (record is null)
            return [];
        return JsonSerializer.Deserialize(record.ClaimsJson, ClaimJsonContext.Default.ListClaim) ?? [];
    }
}
