using Fuse.Fusion.Extensions;
using Fuse.Indexing;
using Fuse.Plugins.Formats.Web.Extensions;
using Fuse.Plugins.Languages.CSharp.Extensions;
using Fuse.Plugins.Languages.CSharp.Roslyn.Extensions;
using Fuse.Cli.Services;
using Fuse.Cli.Mcp;
using Fuse.Context;
using Fuse.Retrieval;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Fuse.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Extensions;

/// <summary>
///     Host composition root for registering the full Fuse stack used by the CLI and MCP server.
/// </summary>
public static class FuseServiceCollectionExtensions
{
    /// <summary>
    ///     Registers core fusion services, C# language and Roslyn structural plugins, and format reducers.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <paramref name="services" /> instance, to allow chaining.</returns>
    /// <remarks>
    ///     This is the single composition root for production hosts. Callers should not register
    ///     <c>AddFuseCore</c> or <c>AddCSharpRoslyn</c> separately.
    /// </remarks>
    public static IServiceCollection AddFuse(this IServiceCollection services)
    {
        services.AddFuseCore();
        services.AddCSharpLanguage();
        services.AddCSharpRoslyn();
        services.AddFormatReducers();
        services.AddSemanticIndexing();
        return services;
    }

    // Registers the V3 semantic indexing components. The workspace index store is created per command with a
    // resolved database path (it is not a singleton), so only the stateless extractors and the file scanner
    // are registered here.
    private static IServiceCollection AddSemanticIndexing(this IServiceCollection services)
    {
        services.AddSingleton<FileHashService>();
        services.AddSingleton<SyntaxSymbolExtractor>();
        services.AddSingleton<SyntaxRouteExtractor>();
        services.AddSingleton<SemanticSymbolExtractor>();
        services.AddSingleton<DotNetWorkspaceDiscoverer>();
        // Constructed via a factory because the optional ILogger constructor parameter is not registered.
        services.AddSingleton(_ => new RoslynWorkspaceLoader());
        services.AddSingleton(_ => SemanticAnalysisRunner.CreateDefault());
        services.AddSingleton<WorkspaceFileScanner>();
        services.AddSingleton<IProcessRunner, OwnedProcessRunner>();
        services.AddSingleton<BuildCaptureClient>();
        // The indexer is retained by the singleton job executor. Register it with the same host lifetime so
        // its compiler client and held-solution cache cannot be captured from a shorter-lived service.
        services.AddSingleton<SemanticIndexer>();
        services.AddSingleton<IndexCoordinator>();
        services.AddSingleton<IWorkspaceIndexJobExecutor, SemanticIndexJobExecutor>();
        services.AddSingleton<IWorkspaceIndexJobManager, WorkspaceIndexJobManager>();
        services.AddSingleton<EagerIndex>();
        services.AddSingleton<LocalIndexAccessProvider>();
        services.AddSingleton<IIndexAccessProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<LocalIndexAccessProvider>());
        services.AddSingleton<ResidentWorkspaceRegistry>();
        services.AddSingleton<IResidentWorkspaceProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<ResidentWorkspaceRegistry>());
        services.AddSingleton<WarmSolutionCache>();
        services.AddSingleton<PooledCheckWorker>();
        services.AddSingleton<FuseMcpRuntime>();
        services.AddSingleton<IndexJobClient>();
        services.AddSingleton<IChangeSource, GitChangeSource>();
        services.AddSingleton<ContextSessionStore>();
        return services;
    }
}
