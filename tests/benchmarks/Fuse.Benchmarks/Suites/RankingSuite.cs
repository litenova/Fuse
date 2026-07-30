using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;

namespace Fuse.Benchmarks;

/// <summary>
///     The ranking regression suite (N1): scores the retrieval ranking in isolation with MRR, recall@k, and
///     nDCG@k against changed-file ground truth, so a field-weight or prior change that reorders results is
///     caught by a recorded metric rather than shipping unmeasured (findings 4 and 9).
/// </summary>
/// <remarks>
///     Runs two configurations over the same index, so the gate covers the lexical channel and the shipping
///     dependency-centrality prior:
///     <list type="bullet">
///         <item><description><c>lexical</c>: the base lexical channel with dependency centrality off.</description></item>
///         <item><description><c>default</c>: the shipping configuration with dependency centrality on.</description></item>
///     </list>
///     Ranking metrics come from <see cref="Metrics" /> (MRR, recall@k, nDCG@k). This suite is the required gate
///     on any change to field weights, tokenization, query expansion, or priors.
/// </remarks>
public sealed class RankingSuite : IEvalSuite
{
    private const int CandidateK = 20;
    private static readonly int[] RecallKs = [1, 5, 10];
    private readonly SemanticIndexer _indexer;
    private readonly IChangeSource _changeSource;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RankingSuite" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="changeSource">The git change source (passed for parity; unused for title-only ranking).</param>
    public RankingSuite(SemanticIndexer indexer, IChangeSource changeSource)
    {
        _indexer = indexer;
        _changeSource = changeSource;
    }

    /// <inheritdoc />
    public string Name => "ranking";

    /// <inheritdoc />
    public string Description => "Ranking regression: MRR, recall@k, nDCG@k for the lexical channel and the shipping default.";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var manager = new CorpusManager(options.BenchRoot, options.ResolvedCorpusRoot, options.Log);
        var dataset = manager.LoadDataset("dotnet-prs-v1", options.DatasetFile, options.ManifestPath);
        var notes = new List<string> { $"candidates@{CandidateK}, title-only input, changed-file ground truth" };

        var present = dataset.Repos
            .Where(r => r.Path is not null && (options.RepoFilter is null || r.Name.Equals(options.RepoFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (present.Count == 0)
        {
            notes.Add("No corpus repositories present; skipped. Run setup-corpus to populate the corpus.");
            return Skipped(notes);
        }

        // The dense channel and git co-change prior were retired. The remaining configurations isolate the
        // lexical channel and the dependency-centrality prior.
        var configs = new (string Label, bool Centrality)[]
        {
            ("lexical", false),
            ("default", true),
        };

        // config label -> per-task ranked metrics
        var perConfig = configs.ToDictionary(c => c.Label, _ => new List<RankRow>(), StringComparer.Ordinal);
        var modes = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var repo in present)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Restore)
            {
                var restore = await manager.RestoreAsync(repo.Path!, cancellationToken);
                notes.Add($"restore {repo.Name}: {restore.Summary}");
            }

            options.Report($"ranking: indexing {repo.Name}");
            var databasePath = Path.Combine(Path.GetTempPath(), "fuse-eval-rank", Guid.NewGuid().ToString("N"), "fuse.db");
            try
            {
                await using var store = new WorkspaceIndexStore(databasePath);
                await store.InitializeAsync(cancellationToken);
                var mode = (await _indexer.IndexAsync(repo.Path!, store, cancellationToken)).Mode;
                modes[mode] = modes.GetValueOrDefault(mode) + 1;

                var repoTasks = options.Limit > 0 ? repo.Tasks.Take(options.Limit).ToList() : repo.Tasks;
                foreach (var config in configs)
                {
                    var engine = new SemanticRetrievalEngine(store, _changeSource);
                    foreach (var task in repoTasks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var groundTruth = ChangedFiles(task);
                        if (groundTruth.Count == 0)
                            continue; // No changed-file truth to rank against; skip (does not penalize ranking).

                        var localization = await engine.LocalizeAsync(new LocalizationRequest(
                            repo.Path!, Query: task.Title, MaxCandidates: CandidateK,
                            EnableCentralityPrior: config.Centrality), cancellationToken);

                        // Ranked list in score order, deduped by path preserving the first (highest) occurrence.
                        var ranked = new List<string>();
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var c in localization.Candidates)
                        {
                            var p = Normalize(c.Path);
                            if (p.Length > 0 && seen.Add(p))
                                ranked.Add(p);
                        }

                        var gtSet = groundTruth.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        perConfig[config.Label].Add(new RankRow(
                            Metrics.ReciprocalRank(ranked, gtSet),
                            RecallKs.ToDictionary(k => k, k => Metrics.RecallAtK(ranked, groundTruth, k)),
                            Metrics.NdcgAtK(ranked, gtSet, 10)));
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                options.Report($"ranking: {repo.Name} indexing failed: {e.Message}");
            }
            finally
            {
                TryDeleteStore(databasePath);
            }
        }

        var defaultRows = perConfig["default"];
        if (defaultRows.Count == 0)
        {
            notes.Add("No tasks could be scored.");
            return Skipped(notes);
        }

        notes.Add("index modes: " + string.Join(", ", modes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        foreach (var config in configs)
        {
            var rows = perConfig[config.Label];
            if (rows.Count == 0)
                continue;
            var mrr = Metrics.Mean(rows.Select(r => r.ReciprocalRank).ToList());
            var ndcg = Metrics.Mean(rows.Select(r => r.Ndcg10).ToList());
            var recallParts = RecallKs.Select(k => $"recall@{k} {Metrics.Mean(rows.Select(r => r.RecallAtK[k]).ToList()):P1}");
            notes.Add($"config {config.Label} (n {rows.Count}): MRR {mrr:F3}, {string.Join(", ", recallParts)}, nDCG@10 {ndcg:F3}");
        }

        // Headline scorecard: the shipping-default recall@10 and MRR (as F1 slot is unused, we surface it in notes).
        var defRecall10 = Metrics.Mean(defaultRows.Select(r => r.RecallAtK[10]).ToList());
        var defMrr = Metrics.Mean(defaultRows.Select(r => r.ReciprocalRank).ToList());
        var (ciLow, ciHigh) = Metrics.BootstrapCi(defaultRows.Select(r => r.RecallAtK[10]).ToList());
        var scorecard = new Scorecard(defaultRows.Count, defRecall10, ciLow, ciHigh, defMrr, defMrr, 0, 0);

        return new SuiteResult(Name, Description, null, scorecard, [], notes);
    }

    // The changed files a task touched, normalized. This is the ranking ground truth (finding: recall is bounded
    // by index mode, but ranking is measured against what the PR actually changed).
    private static IReadOnlyList<string> ChangedFiles(PrTask task)
    {
        var changed = task.GroundTruth.Files
            .Where(f => string.Equals(f.Role, "changed", StringComparison.OrdinalIgnoreCase))
            .Select(f => Normalize(f.Path))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (changed.Count > 0)
            return changed;
        // Fall back to all ground-truth files if roles were not adjudicated on this task.
        return task.GroundTruth.Files
            .Select(f => Normalize(f.Path))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // One task's ranking outcome under a configuration.
    private readonly record struct RankRow(double ReciprocalRank, IReadOnlyDictionary<int, double> RecallAtK, double Ndcg10);

    private SuiteResult Skipped(IReadOnlyList<string> notes)
        => new(Name, Description, null, new Scorecard(0, 0, 0, 0, 0, 0, 0, 0), [], notes);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void TryDeleteStore(string databasePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
