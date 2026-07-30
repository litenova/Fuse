using System.ComponentModel;
using Fuse.Cli.Services;
using Fuse.Collection.Templates;
using Fuse.Context;
using Fuse.Fusion;
using Fuse.Indexing;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction;
using Fuse.Retrieval;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

[McpServerToolType]
internal sealed class FuseWorkspaceMcpHandler
{
    [McpServerTool(Name = "fuse_workspace", ReadOnly = false)]
    [Description("Workspace status and lifecycle. Use action=status before broad discovery, action=index to start or join syntax indexing, and action=cancel to stop the active job.")]
    public static Task<string> ExecuteAsync(
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
}

[McpServerToolType]
internal sealed class FuseFindMcpHandler
{
    [McpServerTool(Name = "fuse_find", ReadOnly = true)]
    [Description("Find symbols, paths, text, wiring, signatures, neighbors, or ranked task candidates.")]
    public static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        string query,
        string path = ".",
        string kind = "all",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        FuseToolOperations.FuseFindAsync(indexer, changeSource, query, path, kind, cancellationToken, runtime);
}

[McpServerToolType]
internal sealed class FuseContextMcpHandler
{
    [McpServerTool(Name = "fuse_context", ReadOnly = true)]
    [Description("Emit reduced scoped source for symbol, file, wiring, request, configuration, or route seeds.")]
    public static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        ContextSessionStore sessionStore,
        string path = ".",
        string[]? seeds = null,
        string[]? files = null,
        string[]? services = null,
        string[]? requests = null,
        string[]? configs = null,
        string[]? routes = null,
        int depth = 2,
        int maxTokens = 0,
        string format = "xml",
        string? sessionId = null,
        CancellationToken cancellationToken = default) =>
        FuseToolOperations.FuseContextAsync(
            indexer, reductionPipeline, sessionStore, path, seeds, files, services, requests, configs, routes,
            depth, maxTokens, format, sessionId, cancellationToken);
}

[McpServerToolType]
internal sealed class FuseImpactMcpHandler
{
    [McpServerTool(Name = "fuse_impact", ReadOnly = true)]
    [Description("Report callers, implementers, consumers, and referencing types before a public change.")]
    public static Task<string> ExecuteAsync(
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
}

[McpServerToolType]
internal sealed class FuseCheckMcpHandler
{
    [McpServerTool(Name = "fuse_check", ReadOnly = true)]
    [Description("Typecheck one proposed file edit without writing it, at the best available verification grade.")]
    public static Task<string> ExecuteAsync(
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
}

[McpServerToolType]
internal sealed class FuseTestMcpHandler
{
    [McpServerTool(Name = "fuse_test", ReadOnly = true)]
    [Description("Run the selected covering tests for a symbol, or race bounded candidate edits in a resident workspace.")]
    public static Task<string> ExecuteAsync(
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
}

[McpServerToolType]
internal sealed class FuseRefactorMcpHandler
{
    [McpServerTool(Name = "fuse_refactor", ReadOnly = true)]
    [Description("Stage compiler-executed refactors as verified diffs without writing the workspace.")]
    public static Task<string> ExecuteAsync(
        string path = ".",
        string symbol = "",
        string newName = "",
        string operation = "rename",
        string containingType = "",
        string parameterType = "",
        string parameterName = "",
        string argument = "default",
        string newOrder = "",
        string diagnosticId = "",
        string file = "",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        FuseToolOperations.FuseRefactorAsync(
            path, symbol, newName, operation, containingType, parameterType, parameterName, argument, newOrder,
            diagnosticId, file, cancellationToken, runtime);
}

[McpServerToolType]
internal sealed class FuseReviewMcpHandler
{
    [McpServerTool(Name = "fuse_review", ReadOnly = true)]
    [Description("Review a diff's impact and return the scoped context needed for handoff.")]
    public static Task<string> ExecuteAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        IChangeSource changeSource,
        ContextSessionStore sessionStore,
        string path = ".",
        string changedSince = "HEAD",
        int maxTokens = 0,
        bool includeTests = true,
        string format = "xml",
        string? sessionId = null,
        bool handoff = false,
        string checkSession = "",
        int maxChangedFiles = 0,
        CancellationToken cancellationToken = default) =>
        FuseToolOperations.FuseReviewAsync(
            indexer, reductionPipeline, changeSource, sessionStore, path, changedSince, maxTokens, includeTests,
            format, sessionId, handoff, checkSession, maxChangedFiles, cancellationToken);
}

[McpServerToolType]
internal sealed class FuseReduceMcpHandler
{
    [McpServerTool(Name = "fuse_reduce", ReadOnly = true)]
    [Description("Reduce named files or raw content without using the workspace index.")]
    public static Task<string> ExecuteAsync(
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
}
