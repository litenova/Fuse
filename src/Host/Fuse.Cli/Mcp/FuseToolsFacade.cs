using Fuse.Cli.Services;
using Fuse.Collection.Templates;
using Fuse.Context;
using Fuse.Fusion;
using Fuse.Indexing;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction;
using Fuse.Retrieval;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

// Internal test and command facade. MCP registration uses the focused handler types rather than this facade.
internal static class FuseTools
{
    internal static Task<string> FuseWorkspaceAsync(
        SemanticIndexer indexer,
        IndexJobClient jobs,
        string action = "status",
        string path = ".",
        string detail = "all",
        int maxRows = 200,
        string file = "",
        string content = "",
        bool write = false,
        string expectedHash = "",
        bool refresh = false,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        FuseToolOperations.FuseWorkspaceAsync(
            indexer, jobs, action, path, detail, maxRows, file, content, write, expectedHash, refresh,
            cancellationToken, runtime);

    internal static Task<string> FuseFindAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        string query,
        string path = ".",
        string kind = "all",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        FuseToolOperations.FuseFindAsync(indexer, changeSource, query, path, kind, cancellationToken, runtime);

    internal static Task<string> FuseImpactAsync(
        SemanticIndexer indexer,
        string symbol = "",
        string path = ".",
        int limit = 50,
        string package = "",
        string fromVersion = "",
        string toVersion = "",
        string session = "",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        FuseToolOperations.FuseImpactAsync(
            indexer, symbol, path, limit, package, fromVersion, toVersion, session, cancellationToken, runtime);

    internal static Task<string> FuseTestAsync(
        SemanticIndexer indexer,
        string symbol = "",
        string path = ".",
        int limit = 20,
        string candidates = "",
        int maxCandidates = 4,
        bool analyzers = true,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        FuseToolOperations.FuseTestAsync(
            indexer, symbol, path, limit, candidates, maxCandidates, analyzers, cancellationToken, runtime);

    internal static Task<string> FuseCheckAsync(
        SemanticIndexer indexer,
        string path = ".",
        string file = "",
        string content = "",
        string session = "",
        bool full = false,
        bool markGreen = false,
        bool analyzers = true,
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        FuseToolOperations.FuseCheckAsync(
            indexer, path, file, content, session, full, markGreen, analyzers, cancellationToken, runtime);

    internal static Task<string> FuseReduceAsync(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        string path = ".",
        string[]? files = null,
        string? content = null,
        string extension = ".cs",
        ReductionLevel level = ReductionLevel.Standard,
        int maxTokens = 0,
        CancellationToken cancellationToken = default) =>
        FuseToolOperations.FuseReduceAsync(
            orchestrator, templateRegistry, path, files, content, extension, level, maxTokens, cancellationToken);

    internal static Task<string> FuseRefactorCoreAsync(
        string path,
        string symbol,
        string newName,
        string operation,
        string containingType,
        string parameterType,
        string parameterName,
        string argument,
        string newOrder,
        string diagnosticId,
        string file,
        CancellationToken cancellationToken,
        bool routeToHost = true,
        FuseMcpRuntime? runtime = null) =>
        FuseToolOperations.FuseRefactorCoreAsync(
            path, symbol, newName, operation, containingType, parameterType, parameterName, argument, newOrder,
            diagnosticId, file, cancellationToken, routeToHost, runtime);

    internal static Task<string> BuildHandoffAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        string root,
        string changedSince,
        string checkSession,
        CancellationToken cancellationToken,
        FuseMcpRuntime? runtime = null) =>
        FuseToolOperations.BuildHandoffAsync(
            indexer, changeSource, root, changedSince, checkSession, cancellationToken, runtime);

    internal static Task<CheckResult?> TryOracleFromCaptureBundleAsync(
        FuseMcpRuntime runtime,
        string root,
        string file,
        string content,
        BuildCaptureClient client,
        CancellationToken cancellationToken) =>
        FuseToolOperations.TryOracleFromCaptureBundleAsync(runtime, root, file, content, client, cancellationToken);

    internal static Task<string> OracleAvailabilityHeaderAsync(
        WorkspaceIndexStore store,
        string root,
        CancellationToken cancellationToken,
        string? indexStateOverride = null,
        Fuse.Workspace.IResidentWorkspaceProvider? residentWorkspaces = null) =>
        FuseToolOperations.OracleAvailabilityHeaderAsync(
            store, root, cancellationToken, indexStateOverride, residentWorkspaces);

    internal static Task<string> FormatNotIndexedAvailabilityHeaderAsync(
        string root,
        CancellationToken cancellationToken,
        Fuse.Workspace.IResidentWorkspaceProvider? residentWorkspaces = null) =>
        FuseToolOperations.FormatNotIndexedAvailabilityHeaderAsync(root, cancellationToken, residentWorkspaces);

    internal static Task<string> FormatBuildingSyntaxHeaderAsync(
        string root,
        CancellationToken cancellationToken) =>
        FuseToolOperations.FormatBuildingSyntaxHeaderAsync(root, cancellationToken);

    internal static Task<string> FormatBuildingSyntaxHeaderAsync(
        string root,
        IWorkspaceIndexJobManager? jobs,
        CancellationToken cancellationToken,
        Fuse.Workspace.IResidentWorkspaceProvider? residentWorkspaces = null) =>
        FuseToolOperations.FormatBuildingSyntaxHeaderAsync(root, jobs, cancellationToken, residentWorkspaces);

    internal static Task<string> ComputeIndexStateAsync(
        WorkspaceIndexStore store,
        WorkspaceIndexState state,
        string root,
        CancellationToken cancellationToken) =>
        FuseToolOperations.ComputeIndexStateAsync(store, state, root, cancellationToken);
}
