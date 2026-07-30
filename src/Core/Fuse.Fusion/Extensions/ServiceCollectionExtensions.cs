using Fuse.Fusion.Scoping;
using Fuse.Fusion.Enrichment;
using Fuse.Fusion.PostReduction;
using Fuse.Fusion.Stages;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Collection.Templates;
using Fuse.Collection.Templates.Definitions;
using Fuse.Emission;
using Fuse.Emission.Tokenization;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Abstractions.Markers;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Plugins.Abstractions.Patterns;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Skeleton;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Reduction.Security;
using Fuse.Reduction.Tokenization;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Extensions;

/// <summary>
///     Extension methods for registering Fuse fusion services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Registers stage pipelines, capability registries, and the bounded memory cache factory.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <paramref name="services" /> instance, to allow chaining.</returns>
    /// <remarks>
    ///     Does not register language or format plugins. Hosts call language-specific extension methods
    ///     (for example <c>AddCSharpLanguage</c> and <c>AddFormatReducers</c>) after this when needed.
    ///     File filter registration order equals evaluation order.
    /// </remarks>
    public static IServiceCollection AddFuseCore(this IServiceCollection services)
    {
        services.AddSingleton<TokenizerFactory>();
        services.AddSingleton<ITokenCounter>(sp => sp.GetRequiredService<TokenizerFactory>().GetCounter());
        services.AddSingleton<FusionValidator>();
        services.AddSingleton<FusionOrchestrator>();
        services.AddSingleton<IExplainService, ExplainService>();
        services.AddSingleton<FusionCollectionStage>();
        services.AddSingleton<FusionScopingStage>();
        services.AddSingleton<FusionReductionStage>();
        services.AddSingleton<ISecretRedactor, DefaultSecretRedactor>();
        services.AddTransient<FileCollectionPipeline>();
        services.AddTransient<ContentReductionPipeline>();
        services.AddTransient<EmissionPipeline>();
        services.AddSingleton<PostReductionEnrichmentPipeline>();
        // Fresh pattern detectors per run: detectors accumulate mutable state during a detection pass, so the
        // singleton post-reduction pipeline resolves a new transient batch for each run rather than sharing
        // instances across concurrent runs.
        services.AddSingleton<Func<IReadOnlyList<PatternDetectorBase>>>(
            sp => () => sp.GetServices<PatternDetectorBase>().ToArray());

        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        // Per-run content cache: a fresh instance per run keeps the read-once-per-run invariant intact under
        // concurrent runs (a shared cache would be cleared mid-flight by another run).
        services.AddSingleton<Func<ISourceContentProvider>>(sp =>
            () => new SourceContentProvider(sp.GetRequiredService<IFileSystem>()));
        services.AddSingleton<GitIgnoreParser>();
        services.AddSingleton<ProjectTemplateRegistry>();
        services.AddSingleton<OutputNamingService>();

        services.AddSingleton(sp => new CapabilityRegistry<IContentReducer>(sp.GetServices<IContentReducer>()));
        services.AddSingleton(sp => new CapabilityRegistry<ISkeletonExtractor>(sp.GetServices<ISkeletonExtractor>()));
        services.AddSingleton(sp => new CapabilityRegistry<ISymbolOutlineExtractor>(sp.GetServices<ISymbolOutlineExtractor>()));
        services.AddSingleton(sp => new CapabilityRegistry<ISymbolSliceExtractor>(sp.GetServices<ISymbolSliceExtractor>()));
        services.AddSingleton(sp => new CapabilityRegistry<ISymbolChunkExtractor>(sp.GetServices<ISymbolChunkExtractor>()));
        services.AddSingleton(sp => new CapabilityRegistry<ISemanticMarkerGenerator>(sp.GetServices<ISemanticMarkerGenerator>()));
        services.AddSingleton(sp => new CapabilityRegistry<IDependencyExtractor>(sp.GetServices<IDependencyExtractor>()));
        services.AddSingleton(sp => new CapabilityRegistry<ITypeNameLocator>(sp.GetServices<ITypeNameLocator>()));

        services.AddSingleton<ITokenCostModel, DefaultTokenCostModel>();
        services.AddSingleton<DependencyGraphBuilder>();
        services.AddSingleton<FocusSeedResolver>();
        services.AddSingleton<Enrichment.BoilerplateDeduplicator>();
        services.AddSingleton<Enrichment.BodyDeduplicator>();

        services.AddSingleton<Session.ISessionTracker, Session.InMemorySessionTracker>();
        services.AddSingleton<IChangeDetector, GitChangeDetector>();
        services.AddSingleton<IGitStatsProvider, GitStatsProvider>();
        services.AddSingleton<IWorkspaceMemoryStoreFactory, MemoryStoreFactory>();

        RegisterFileFilters(services);
        RegisterProjectTemplates(services);

        return services;
    }

    private static void RegisterFileFilters(IServiceCollection services)
    {
        services.AddTransient<IFileFilter, GitIgnoreFilter>();
        services.AddTransient<IFileFilter, ExtensionFilter>();
        services.AddTransient<IFileFilter, ExcludedDirectoryFilter>();
        services.AddTransient<IFileFilter, TestProjectFilter>();
        services.AddTransient<IFileFilter, UnitTestProjectFilter>();
        services.AddTransient<IFileFilter, FileSizeFilter>();
        services.AddTransient<IFileFilter, BinaryFileFilter>();
        services.AddTransient<IFileFilter, EmptyFileFilter>();
        services.AddTransient<IFileFilter, AutoGeneratedFileFilter>();
        services.AddTransient<IFileFilter, ExcludedFileNameFilter>();
        services.AddTransient<IFileFilter, GlobPatternFilter>();
    }

    private static void RegisterProjectTemplates(IServiceCollection services)
    {
        services.AddSingleton<IProjectTemplate, AzureDevOpsWikiTemplate>();
        services.AddSingleton<IProjectTemplate, ClojureTemplate>();
        services.AddSingleton<IProjectTemplate, CppCSharpTemplate>();
        services.AddSingleton<IProjectTemplate, DartTemplate>();
        services.AddSingleton<IProjectTemplate, DotNetTemplate>();
        services.AddSingleton<IProjectTemplate, ElixirTemplate>();
        services.AddSingleton<IProjectTemplate, ErlangTemplate>();
        services.AddSingleton<IProjectTemplate, FsharpTemplate>();
        services.AddSingleton<IProjectTemplate, GenericTemplate>();
        services.AddSingleton<IProjectTemplate, GoTemplate>();
        services.AddSingleton<IProjectTemplate, HaskellTemplate>();
        services.AddSingleton<IProjectTemplate, InfrastructureTemplate>();
        services.AddSingleton<IProjectTemplate, JavaScriptTemplate>();
        services.AddSingleton<IProjectTemplate, JavaTemplate>();
        services.AddSingleton<IProjectTemplate, KotlinTemplate>();
        services.AddSingleton<IProjectTemplate, LuaTemplate>();
        services.AddSingleton<IProjectTemplate, PerlTemplate>();
        services.AddSingleton<IProjectTemplate, PhpTemplate>();
        services.AddSingleton<IProjectTemplate, PythonTemplate>();
        services.AddSingleton<IProjectTemplate, RTemplate>();
        services.AddSingleton<IProjectTemplate, RubyTemplate>();
        services.AddSingleton<IProjectTemplate, RustTemplate>();
        services.AddSingleton<IProjectTemplate, ScalaTemplate>();
        services.AddSingleton<IProjectTemplate, SwiftTemplate>();
        services.AddSingleton<IProjectTemplate, TypeScriptTemplate>();
        services.AddSingleton<IProjectTemplate, VbNetTemplate>();
    }
}
