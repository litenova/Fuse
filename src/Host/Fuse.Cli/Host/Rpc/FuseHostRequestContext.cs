using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Reduction;
using Fuse.Reduction.Security;
using Fuse.Retrieval;
using Fuse.Semantics;

namespace Fuse.Cli.Rpc;

/// <summary>
///     Immutable application dependencies shared by the host RPC adapters and their focused operations.
/// </summary>
internal sealed class FuseHostRequestContext
{
    internal FuseHostRequestContext(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        ContentReductionPipeline reductionPipeline,
        ISecretRedactor redactor,
        IGeneratedCodeDetector generatedCodeDetector,
        IndexCoordinator indexCoordinator,
        IWorkspaceIndexJobManager indexJobs,
        IIndexAccessProvider? indexAccess = null,
        FuseMcpRuntime? runtime = null)
    {
        Indexer = indexer;
        ChangeSource = changeSource;
        ReductionPipeline = reductionPipeline;
        Redactor = redactor;
        GeneratedCodeDetector = generatedCodeDetector;
        IndexCoordinator = indexCoordinator;
        IndexJobs = indexJobs;
        IndexAccess = indexAccess;
        Runtime = runtime;
    }

    internal SemanticIndexer Indexer { get; }

    internal IChangeSource ChangeSource { get; }

    internal ContentReductionPipeline ReductionPipeline { get; }

    internal ISecretRedactor Redactor { get; }

    internal IGeneratedCodeDetector GeneratedCodeDetector { get; }

    internal IndexCoordinator IndexCoordinator { get; }

    internal IWorkspaceIndexJobManager IndexJobs { get; }

    internal IIndexAccessProvider? IndexAccess { get; }

    internal FuseMcpRuntime? Runtime { get; }
}
