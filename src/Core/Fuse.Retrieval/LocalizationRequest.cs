namespace Fuse.Retrieval;

/// <summary>
///     A request to localize the files and symbols relevant to a task. Any combination of signals may be
///     supplied; candidate generators run for the fields that are set.
/// </summary>
/// <param name="RootDirectory">The workspace root.</param>
/// <param name="Query">A free-text task or query, used for full-text and path search.</param>
/// <param name="ChangedSince">A git base ref whose changed files seed the candidates (wired in review).</param>
/// <param name="Focus">A symbol name to localize around.</param>
/// <param name="Route">A route ("METHOD /pattern") to resolve.</param>
/// <param name="Service">A service type name to resolve to its implementation.</param>
/// <param name="Request">A request or command type name to resolve to its handler.</param>
/// <param name="ConfigSection">A configuration section name to resolve to its options type.</param>
/// <param name="StackTrace">A stack trace to parse for frames (parsed in a later phase).</param>
/// <param name="SelectedPaths">Explicit paths to treat as must-keep changed/selected seeds.</param>
/// <param name="MaxCandidates">The maximum number of candidates to return per source.</param>
/// <param name="Depth">The graph expansion depth (used by the retrieval engine).</param>
/// <param name="MaxTokens">An optional token budget for packing.</param>
/// <param name="IncludeTests">Whether test files are eligible.</param>
/// <param name="IncludeConfig">Whether config files are eligible.</param>
/// <param name="Strict">
///     When true, the signal-sufficiency contract hard-requires an anchor: an insufficient request is refused
///     (no file list, only the navigation map). The default (false) is best-effort-plus-flag, so a client that
///     cannot refine still receives the partial set with a low-confidence flag.
/// </param>
/// <param name="ExpandGraph">
///     When true, the selected candidates are enriched with their typed-graph neighbors (implementers, callers,
///     configuration) at a decayed score, for discovery. Off by default: expansion adds the structural blast
///     radius, which widens recall but pressures precision, so it is an explicit operating point rather than the
///     default. A no-op in syntax mode where the graph has no edges.
/// </param>
/// <param name="EnableCentralityPrior">
///     Whether to apply the dependency-centrality prior. On by default; the ranking suite (N1) sets it false to
///     score the ranking channels in isolation and to re-adjudicate the priors as default-on features.
/// </param>
public sealed record LocalizationRequest(
    string RootDirectory,
    string? Query = null,
    string? ChangedSince = null,
    string? Focus = null,
    string? Route = null,
    string? Service = null,
    string? Request = null,
    string? ConfigSection = null,
    string? StackTrace = null,
    IReadOnlyList<string>? SelectedPaths = null,
    int MaxCandidates = 50,
    int Depth = 2,
    int? MaxTokens = null,
    bool IncludeTests = true,
    bool IncludeConfig = true,
    bool Strict = false,
    bool ExpandGraph = false,
    bool EnableCentralityPrior = true);

/// <summary>
///     A candidate file or symbol produced by a candidate generator, before scoring and graph expansion.
/// </summary>
/// <param name="NodeId">The graph node id when the candidate is a node (symbol, route, config); empty for file-only candidates.</param>
/// <param name="FilePath">The candidate's normalized file path.</param>
/// <param name="Kind">The candidate kind (node kind or "file").</param>
/// <param name="BaseScore">The base relevance score before normalization.</param>
/// <param name="Source">Which generator produced the candidate.</param>
/// <param name="Reasons">Human-readable reasons the candidate was included.</param>
/// <param name="TokenEstimate">An estimated token cost, when known (0 until resolved during packing).</param>
public sealed record CandidateNode(
    string NodeId,
    string FilePath,
    string Kind,
    double BaseScore,
    CandidateSource Source,
    IReadOnlyList<string> Reasons,
    int TokenEstimate);

/// <summary>
///     The provenance of a candidate, which determines its base weight.
/// </summary>
public enum CandidateSource
{
    /// <summary>A file changed in the diff.</summary>
    DiffChangedFile,

    /// <summary>An exact route match.</summary>
    RouteExact,

    /// <summary>An exact symbol match.</summary>
    SymbolExact,

    /// <summary>A frame parsed from a stack trace.</summary>
    StackTrace,

    /// <summary>An exact service match.</summary>
    ServiceExact,

    /// <summary>An exact request/command match.</summary>
    RequestExact,

    /// <summary>An exact config section match.</summary>
    ConfigExact,

    /// <summary>A full-text match on a symbol/name/signature field.</summary>
    FtsSymbol,

    /// <summary>A full-text or LIKE match on a path.</summary>
    FtsPath,

    /// <summary>A full-text match on a body or comment field.</summary>
    FtsBody,

    /// <summary>A neighbor pulled in by expanding a seed through the typed semantic graph.</summary>
    GraphNeighbor,
}

/// <summary>
///     The base weight for each candidate source.
/// </summary>
public static class CandidateSourceWeights
{
    /// <summary>
    ///     Returns the base weight for a candidate source.
    /// </summary>
    /// <param name="source">The candidate source.</param>
    /// <returns>The base weight in the range 0 to 1.</returns>
    public static double Weight(CandidateSource source) => source switch
    {
        CandidateSource.DiffChangedFile => 1.00,
        CandidateSource.RouteExact => 1.00,
        CandidateSource.SymbolExact => 0.95,
        CandidateSource.StackTrace => 0.95,
        CandidateSource.ServiceExact => 0.95,
        CandidateSource.RequestExact => 0.95,
        CandidateSource.ConfigExact => 0.90,
        CandidateSource.FtsSymbol => 0.75,
        CandidateSource.FtsPath => 0.70,
        CandidateSource.FtsBody => 0.55,
        CandidateSource.GraphNeighbor => 0.40,
        _ => 0.50,
    };
}
