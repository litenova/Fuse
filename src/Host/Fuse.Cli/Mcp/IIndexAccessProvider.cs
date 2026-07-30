using Fuse.Indexing;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Opens the workspace index for MCP read tools and explicit index actions. The local implementation starts or
///     joins the host-owned repository job manager; the remote implementation delegates that job to a shared
///     <c>fuse host</c> daemon and opens the committed store read-only locally for queries.
/// </summary>
public interface IIndexAccessProvider
{
    /// <summary>
    ///     Starts or joins the syntax index job without waiting for a readable store. Resident compiler metadata can
    ///     answer an exact query immediately while the repository index continues through its shared owner.
    /// </summary>
    /// <param name="indexer">The semantic indexer used by a local fallback.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="cancellationToken">A token to cancel only the start request.</param>
    /// <returns>The accepted job snapshot and join outcome.</returns>
    Task<IndexJobStartResult> StartSyntaxAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken);

    /// <summary>
    ///     Opens the store for a read tool: starts or joins the syntax job on the owning process (or daemon), then
    ///     returns a readable store handle after syntax data is committed.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="cancellationToken">A token to cancel the open.</param>
    /// <returns>A readable store ready for queries.</returns>
    Task<WorkspaceIndexStore> OpenIndexedAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken);

    /// <summary>
    ///     Runs an explicit syntax index build or refresh through the repository job manager (or daemon RPC when
    ///     delegated).
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="cancellationToken">A token to cancel the index pass.</param>
    /// <returns>The index pass summary.</returns>
    Task<SemanticIndexResult> IndexAsync(
        SemanticIndexer indexer, string path, CancellationToken cancellationToken);
}
