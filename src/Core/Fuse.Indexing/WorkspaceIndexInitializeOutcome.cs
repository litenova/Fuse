namespace Fuse.Indexing;

/// <summary>
///     The result of <see cref="IWorkspaceIndexLifecycleStore.InitializeAsync" />, reporting whether the store was
///     rebuilt empty and must be re-indexed from source before serving reads.
/// </summary>
/// <param name="RebuiltEmptyStore">
///     <see langword="true" /> when initialization dropped and recreated the schema (version drift, schema
///     migration, or corrupt recovery); callers should return <c>index_rebuilding:</c> and retry later.
/// </param>
/// <param name="Detail">A short human-readable reason (for example an upgrade target version).</param>
public sealed record WorkspaceIndexInitializeOutcome(bool RebuiltEmptyStore, string? Detail)
{
    /// <summary>A normal initialization that left existing index data intact.</summary>
    public static WorkspaceIndexInitializeOutcome Normal { get; } = new(false, null);
}
