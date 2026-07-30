namespace Fuse.Semantics;

/// <summary>
///     A stage reported by the workspace indexer while it prepares or writes derived data.
/// </summary>
public enum SemanticIndexStage
{
    /// <summary>Scanning the repository inventory.</summary>
    Inventory,

    /// <summary>Reading and parsing source declarations.</summary>
    SyntaxExtraction,

    /// <summary>Writing syntax records and search documents.</summary>
    SyntaxPersistence,

    /// <summary>Resolving the requested compiler target.</summary>
    SemanticPreparation,

    /// <summary>Loading compiler state and extracting semantic facts.</summary>
    SemanticExtraction,

    /// <summary>Writing compiler-derived facts.</summary>
    SemanticPersistence,

    /// <summary>Completing metadata and bounded database maintenance.</summary>
    Finalization,
}

/// <summary>
///     One observable update from the syntax or semantic indexing pipeline.
/// </summary>
/// <param name="Stage">The indexing stage that owns the reported work.</param>
/// <param name="CompletedUnits">The number of completed work units in the stage.</param>
/// <param name="TotalUnits">The total stage work units, when known.</param>
/// <param name="CurrentItem">The current file, project, or operation, when known.</param>
public sealed record SemanticIndexProgress(
    SemanticIndexStage Stage,
    long CompletedUnits,
    long? TotalUnits,
    string? CurrentItem = null);
