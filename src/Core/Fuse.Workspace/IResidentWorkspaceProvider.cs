using Fuse.Indexing;

namespace Fuse.Workspace;

/// <summary>
///     A one-line description of the resident workspace serving a repository root: how many projects it holds
///     live and as of when (S1). This is the light-weight status a read path reports in its availability header
///     ("resident, current as of ...") without exposing the heavy compilation state.
/// </summary>
/// <param name="ProjectCount">The number of projects held live in the resident workspace.</param>
/// <param name="AsOf">A human-readable stamp of when the resident state was last current (set by the host).</param>
public sealed record ResidentStatus(int ProjectCount, string AsOf);

/// <summary>
///     One signature resolved from a live resident compilation (U1b): the compiler-rendered declaration of a
///     symbol, sourced either from the project's own source or - the point of the resident path - from a
///     referenced assembly's real metadata, so a package API is answered from the compiler rather than guessed.
/// </summary>
/// <param name="Signature">The compiler-rendered declaration (accessibility, return type, name, parameters).</param>
/// <param name="Kind">The symbol kind (for example <c>Method</c>, <c>Property</c>, <c>NamedType</c>).</param>
/// <param name="Container">The containing type or namespace, or empty when there is none.</param>
/// <param name="Assembly">The declaring assembly's simple name (the metadata source, for a referenced symbol).</param>
public sealed record ResidentSignature(string Signature, string Kind, string Container, string Assembly);

/// <summary>
///     The seam by which a read path asks whether a live resident workspace is serving a repository root, so an
///     answer can be labelled resident-grade rather than store-grade (S1, Decision D8: resident truth first, the
///     store second). The default implementation reports no resident workspace, so a process that has not wired a
///     resident engine behaves exactly as the store-backed path always has.
/// </summary>
public interface IResidentWorkspaceProvider
{
    /// <summary>
    ///     Describes the resident workspace serving a root, if one is live.
    /// </summary>
    /// <param name="root">The absolute workspace root.</param>
    /// <returns>The resident status, or null when no resident workspace serves that root (store-backed).</returns>
    ResidentStatus? DescribeResident(string root);

    /// <summary>
    ///     Speculatively typechecks a proposed single-file edit against the resident workspace serving a root, if
    ///     one is live (S1: the oracle-grade check served from the live compilation, no build, no disk write).
    /// </summary>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>
    ///     The changed document's diagnostics, or null when no resident workspace serves the root or the file is
    ///     not in it (the caller then falls back to the build-capture worker or build-grade path).
    /// </returns>
    IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
        string root, string relativeFilePath, string newContent, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the whole-state diagnostics of the resident workspace serving a root, if one is live: the
    ///     current diagnostics across every held compilation (S1's <c>GetDiagnostics</c>). This is what
    ///     <c>fuse_check</c> delta mode (S2) diffs a persisted session baseline against, so a delta is computed
    ///     without running a build. Null when no resident workspace serves the root (delta mode then abstains,
    ///     since it must not run a build).
    /// </summary>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="cancellationToken">A token to cancel the diagnostic walk.</param>
    /// <returns>The current whole-state diagnostics, or null when no resident workspace serves the root.</returns>
    IReadOnlyList<CheckDiagnostic>? TryGetCurrentDiagnostics(string root, CancellationToken cancellationToken) => null;

    /// <summary>
    ///     Speculatively typechecks a proposed single-file edit against the resident workspace and, when requested,
    ///     also runs the repo's configured analyzers against the overlay (S4 analyzer parity): the changed
    ///     document's compiler diagnostics merged with its analyzer diagnostics at the repo's editorconfig
    ///     severities. When <paramref name="includeAnalyzers" /> is false this is the compiler-only overlay check.
    /// </summary>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="includeAnalyzers">Whether to run the configured analyzers and merge their diagnostics.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>
    ///     The changed document's diagnostics, or null when no resident workspace serves the root or the file is
    ///     not in it (the caller falls back to another grade). The default implementation returns null.
    /// </returns>
    Task<IReadOnlyList<CheckDiagnostic>?> TryCheckOverlayAsync(
        string root, string relativeFilePath, string newContent, bool includeAnalyzers, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CheckDiagnostic>?>(null);

    /// <summary>
    ///     Resolves a symbol's signature against the resident workspace serving a root, if one is live (U1b): the
    ///     compiler-rendered declaration of a type or member, from the project's source or - the point of this path
    ///     - from a referenced assembly's real metadata. This is the hallucinated-package-API killer: asking for a
    ///     package type or member by qualified name returns the compiler's own view of the shipped API, not a guess
    ///     or a store row that never indexed the package. Resolution needs a qualified name (a namespace-qualified
    ///     type, or <c>Type.Member</c>) because a metadata assembly cannot be searched by simple name without a full
    ///     walk; a simple name falls through to the source declarations only.
    /// </summary>
    /// <param name="root">The absolute workspace root.</param>
    /// <param name="symbolName">The symbol to resolve (a qualified type name, or <c>Type.Member</c>).</param>
    /// <param name="limitPerName">The maximum matches to return.</param>
    /// <param name="cancellationToken">A token to cancel the resolution.</param>
    /// <returns>
    ///     The resolved signatures, or null when no resident workspace serves the root (the caller then falls back
    ///     to the store-backed signature index). An empty list means a resident workspace served the root but did
    ///     not resolve the name.
    /// </returns>
    IReadOnlyList<ResidentSignature>? TryGetSignature(
        string root, string symbolName, int limitPerName, CancellationToken cancellationToken) => null;
}

/// <summary>
///     The default <see cref="IResidentWorkspaceProvider" />: reports no resident workspace for any root, so a
///     process without a wired resident engine answers store-backed, exactly as before S1. The host that holds a
///     resident workspace replaces this with a provider backed by its live state.
/// </summary>
public sealed class NullResidentWorkspaceProvider : IResidentWorkspaceProvider
{
    /// <summary>The shared instance.</summary>
    public static readonly NullResidentWorkspaceProvider Instance = new();

    private NullResidentWorkspaceProvider()
    {
    }

    /// <inheritdoc />
    public ResidentStatus? DescribeResident(string root) => null;

    /// <inheritdoc />
    public IReadOnlyList<CheckDiagnostic>? TryCheckOverlay(
        string root, string relativeFilePath, string newContent, CancellationToken cancellationToken) => null;
}
