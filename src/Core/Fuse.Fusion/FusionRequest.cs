using Fuse.Fusion.Scoping;
using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Fusion;

/// <summary>
///     Represents a complete fusion request spanning collection, reduction, and emission.
/// </summary>
public sealed class FusionRequest
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionRequest" /> class.
    /// </summary>
    public FusionRequest(
        CollectionOptions collection,
        ReductionOptions reduction,
        EmissionOptions emission,
        bool inMemory = false,
        FocusOptions? focus = null,
        ChangeOptions? changes = null,
        int parallelism = 0,
        bool useReductionCache = true,
        bool clearReductionCache = false,
        bool useAnalysisCache = false,
        ExperimentalOptions? experimental = null)
    {
        Collection = collection;
        Reduction = reduction;
        Emission = emission;
        InMemory = inMemory;
        Focus = focus;
        Changes = changes;
        Parallelism = parallelism;
        UseReductionCache = useReductionCache;
        ClearReductionCache = clearReductionCache;
        UseAnalysisCache = useAnalysisCache;
        Experimental = experimental ?? new ExperimentalOptions();
    }

    /// <summary>
    ///     Gets the collection options for file discovery and filtering.
    /// </summary>
    public CollectionOptions Collection { get; }

    /// <summary>
    ///     Gets the reduction options for content normalization and minification.
    /// </summary>
    public ReductionOptions Reduction { get; }

    /// <summary>
    ///     Gets the emission options for output generation and token budgeting.
    /// </summary>
    public EmissionOptions Emission { get; }

    /// <summary>
    ///     Gets a value indicating whether output is captured in memory instead of written to disk.
    /// </summary>
    public bool InMemory { get; }

    /// <summary>
    ///     Gets focus scoping options, or <c>null</c> when not scoped.
    /// </summary>
    public FocusOptions? Focus { get; }

    /// <summary>
    ///     Gets change scoping options, or <c>null</c> when not scoped by git changes.
    /// </summary>
    public ChangeOptions? Changes { get; }

    /// <summary>
    ///     Gets the maximum degree of parallelism for pipeline stages.
    /// </summary>
    public int Parallelism { get; }

    /// <summary>
    ///     Gets a value indicating whether per-file reduction results are cached in the host memory cache.
    /// </summary>
    /// <remarks>
    ///     The cache is bounded and exists only for the host or command process lifetime. It does not create
    ///     a database file under the repository.
    /// </remarks>
    public bool UseReductionCache { get; }

    /// <summary>
    ///     Gets a value indicating whether cached reduction entries for this repository are cleared before fusion
    ///     runs.
    /// </summary>
    public bool ClearReductionCache { get; }

    /// <summary>
    ///     Gets a value indicating whether per-file dependency and symbol analysis is cached in host memory, so
    ///     repeated scoping calls reuse it. Off by default.
    /// </summary>
    /// <remarks>
    ///     Entries share the bounded repository-scoped cache with reduction output and leave memory when the host
    ///     exits or its cache reaches capacity.
    /// </remarks>
    public bool UseAnalysisCache { get; }

    /// <summary>
    ///     Gets the experimental scoring knobs (graph-centrality weight, query expansion) for this run.
    ///     Defaults to <see cref="ExperimentalOptions" /> defaults; environment variables override the
    ///     configured values when the orchestrator resolves them, and the resolved values are recorded in the
    ///     run report so a measurement is reproducible.
    /// </summary>
    public ExperimentalOptions Experimental { get; }
}
