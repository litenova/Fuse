namespace Fuse.Indexing;

/// <summary>
///     Persists and traverses the semantic dependency graph and wiring facts.
/// </summary>
public interface IWorkspaceIndexGraphStore
{
    /// <summary>Inserts or updates semantic graph nodes.</summary>
    /// <param name="nodes">The nodes to persist.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertNodesAsync(IReadOnlyList<NodeRecord> nodes, CancellationToken cancellationToken);

    /// <summary>Inserts or updates semantic dependency edges.</summary>
    /// <param name="edges">The edges to persist.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertEdgesAsync(IReadOnlyList<SemanticEdgeRecord> edges, CancellationToken cancellationToken);

    /// <summary>Inserts or updates route facts.</summary>
    /// <param name="routes">The routes to persist.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertRoutesAsync(IReadOnlyList<RouteRecord> routes, CancellationToken cancellationToken);

    /// <summary>Inserts or updates dependency-injection registration facts.</summary>
    /// <param name="registrations">The registrations to persist.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertDiRegistrationsAsync(IReadOnlyList<DiRegistrationRecord> registrations, CancellationToken cancellationToken);

    /// <summary>Inserts or updates configuration binding facts.</summary>
    /// <param name="bindings">The bindings to persist.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertOptionsBindingsAsync(IReadOnlyList<OptionsBindingRecord> bindings, CancellationToken cancellationToken);

    /// <summary>Gets dependency edges projected to distinct file pairs.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The file dependency edges.</returns>
    Task<IReadOnlyList<FileDependencyEdge>> GetFileDependencyEdgesAsync(CancellationToken cancellationToken);

    /// <summary>Gets a node by its stable id.</summary>
    /// <param name="nodeId">The node id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The node, or null when absent.</returns>
    Task<NodeRecord?> GetNodeAsync(string nodeId, CancellationToken cancellationToken);

    /// <summary>Finds nodes by exact display name.</summary>
    /// <param name="displayName">The display name to match.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching nodes.</returns>
    Task<IReadOnlyList<NodeRecord>> FindNodesByDisplayNameAsync(string displayName, CancellationToken cancellationToken);

    /// <summary>Gets nodes declared in a normalized file path.</summary>
    /// <param name="normalizedPath">The normalized file path.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The declared nodes.</returns>
    Task<IReadOnlyList<NodeRecord>> GetNodesByFileAsync(string normalizedPath, CancellationToken cancellationToken);

    /// <summary>Gets every graph edge.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>All graph edges.</returns>
    Task<IReadOnlyList<SemanticEdgeRecord>> GetAllEdgesAsync(CancellationToken cancellationToken);

    /// <summary>Gets graph edges leaving a node.</summary>
    /// <param name="nodeId">The source node id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The outgoing edges.</returns>
    Task<IReadOnlyList<SemanticEdgeRecord>> GetOutgoingEdgesAsync(string nodeId, CancellationToken cancellationToken);

    /// <summary>Gets graph edges entering a node.</summary>
    /// <param name="nodeId">The target node id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The incoming edges.</returns>
    Task<IReadOnlyList<SemanticEdgeRecord>> GetIncomingEdgesAsync(string nodeId, CancellationToken cancellationToken);
}
