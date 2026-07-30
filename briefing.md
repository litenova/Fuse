# Fuse: project briefing

## Purpose

This file is the **single briefing document** for understanding Fuse end to end without opening
the repository. It lives at the repo root (`briefing.md`) so planners and LLM sessions can attach
one path. Use it when you need to orient before planning, prioritizing, or reviewing the
roadmap: what the product is, how it is built, what the benchmarks actually show, where the
honest gaps are, and how the v3 through v4 plans fit together.

**Primary audiences:**

- **LLM or human planners** drafting the next roadmap wave (pass this file instead of the source
  tree; section 9 holds every canonical benchmark with methodology and findings).
- **New contributors** who want the full shape of the engine before diving into code.
- **Adoption decisions** where a reader needs measured results and caveats, not marketing claims.

**What this file is not:**

- Not the day-to-day contributor guide (that is [AGENTS.md](AGENTS.md): build, test, invariants,
  MCP tool list, measured headlines).
- Not the executable forward plan (that is [roadmap/v4-plan.md](roadmap/v4-plan.md) and its checklist).
- Not the public docs site (user-facing prose lives under `site/content/docs/`).

**How to read it:** top to bottom for first orientation; or jump to section 9 for evidence-only
planning, section 8 for open issues, section 11 for plan history, section 12 for the 4.0.0 thesis
and what remains.

## About this document

This is a single self-contained overview of Fuse: what it is, how it is built, the algorithms
with their real constants, the measured results, the honest gaps, and the roadmap history. It
was originally assembled to brief a radical-roadmap design session, and it doubles as a general
orientation: a new contributor, a user deciding whether to adopt Fuse, or anyone picking up the
current plan can read it top to bottom and know the whole shape of the project. Section 9 is the
full benchmark record (methodology, every canonical result file, and cross-suite findings) so a
planner can reason about roadmap tradeoffs from this file alone without the source tree.

Everything here was assembled from the live codebase rather than from memory, so the file and
line references are real and current as of Fuse version 4.1.0 (last verified 2026-07-14, through the
v4.1 release and [roadmap/v4.2-plan.md](roadmap/v4.2-plan.md) hardening). Where a described behavior
is still in flight, the section says so and points at the item in [roadmap/v4-plan.md](roadmap/v4-plan.md)
or [roadmap/v4.2-plan.md](roadmap/v4.2-plan.md).

Two conventions this project enforces, visible throughout: plain ASCII prose only (no em
dashes, no smart quotes, no emoji outside code fences), and every performance number sourced to
`tests/benchmarks/results` (never fabricated or rounded). The rationale for both is in the
measured-results section; the honesty convention is itself a product value.

---

## 1. What Fuse is, in one paragraph and then in full

Fuse is a .NET-native semantic context engine for AI coding agents. It keeps a warm, persistent
Roslyn-backed index of a workspace and resolves what the code actually runs (which
implementation is injected for an interface, which handler processes a request, which endpoint
serves a route, what a git diff impacts), then hands the agent the exact context a task needs,
reduced to fit the context window. It ships two ways: a .NET global tool (`fuse`) for the
terminal and CI, and a Model Context Protocol (MCP) server (`fuse mcp serve`) with nine tools
an agent calls in a loop. A long-lived `fuse host` process on a named pipe serves the
ambient-verification hooks and can share one resident workspace across MCP sessions (the VS Code
extension was removed in v4, Decision D15). On the buildable corpus-v2 benchmark, open-ended
task localization reaches 37.7 percent changed-file recall (95 percent CI 30 to 46) at 21.1
percent precision; precise wiring lookup and graph-backed review remain the deterministic
strengths.

The problem it exists to solve: an AI agent working on an unfamiliar .NET codebase burns
enormous token budget and many tool round-trips just exploring. It greps, opens files, greps
again, follows a using directive, opens more files. Most of what it reads is noise for the task
at hand, and it still misses the file where the real wiring lives because that file shares no
lexical tokens with the task description. Fuse attacks this on three axes at once:

1. **Token efficiency** - it reduces source (skeletons, public-API views, format minification)
   so the same information costs far fewer tokens, while keeping the public contract intact and
   verifiable.
2. **Precision scoping** - it returns only the files a task needs, ranked, with provenance,
   instead of a dump.
3. **Semantic resolution** - it answers "what actually runs here" from a typed graph built with
   Roslyn, which is the part lexical and embedding tools cannot do. This is the moat.

The deliberate strategic stance (from the V3.1 plan): Fuse is an MCP server in a loop with a
model, so it does not have to win the vague one-shot query. When a request lacks a usable anchor
it refuses and routes (hands back a navigation map and asks for a symbol, route, service,
config, or git base) rather than returning a low-precision guess. The calling model, which is
exploring anyway, then issues a better-anchored query.

Since 4.0.0, Fuse also ships compiler-oracle tools (`fuse_check`, `fuse_impact`,
`fuse_refactor`, `fuse_test`, and the `fuse_find` signatures kind) that answer edit-time questions
from a build-captured compilation when tier-1 is configured, and abstain with "cannot verify" when
it is not. Section 12 summarizes what shipped, what remains, and the load-success ceiling that gates
realized value.

The v4 program then hardened the tier-1 substrate so the oracle is the default, not an opt-in. Each
capability names its mechanism: `fuse up` diagnoses a per-project load failure against a knowledge
base and applies an install-free NuGet-source-mapping overlay or a consent-gated SDK-band install to
reach tier-1 (C1); `fuse capture --out` packages a build's portable compiler log, extracted graph, and
versioned manifest, and `fuse index --from-capture` rehydrates the semantic graph and answers
`fuse_check` at oracle grade on a machine that cannot restore or build, never shipping the binlog and
failing closed on a planted secret (C2); tier-1 build capture is default-on with the worker bundled in
the tool and a `--no-incremental` capture so an already-built repo still yields the compiler calls,
and `mcp serve` cold-starts syntax-first with a supervised background upgrade (C3); `fuse capture
--merge` assembles a bundle from per-project build-target fragments, equal in graph to a direct
capture, via a backward-compatible format-2 bundle (G4); a shared `fuse host` daemon holds one
resident workspace per repository under a single-instance lock, with `mcp serve` delegating to it over
the pipe, so multiple sessions share one warm compilation (G5); and the corpus-health suite plus a
model-suite refusal gate enforce that a model-driven benchmark runs only on a corpus proven buildable
with verified test oracles (C4). The corpus-bound benchmark numbers under these shipping defaults are
being re-derived on the buildable corpus-v2 (C4); the roadmap progress log carries the interim
measurements and their ceilings.

---

## 2. Repository layout

Solution file: `Fuse.slnx`. Target framework: `net10.0` across all assemblies. SDK pinned to
`10.0.100` in `global.json`. Single version source of truth: `Directory.Build.props`
`<Version>` (currently 4.1.0), mirrored into the MCP registry manifest and the docs site, all bumped
together by `build/set-version.ps1` (the VS Code extension was removed in v4, Decision D15, so there is
no extension package to sync). License: Apache-2.0 (migrated from MIT in v4 item L1).

- `src/Core` - the pipeline libraries:
  - `Fuse.Collection` - file discovery, filtering, gitignore, security guards.
  - `Fuse.Reduction` - reduction tier sequencing, caching, token counting, secret redaction.
  - `Fuse.Emission` - payload/manifest emission, budgeting, provenance rendering.
  - `Fuse.Fusion` - the orchestrator that wires Collection to Reduction to Emission, plus
    scoping (BM25F, dependency graph, change detection, context planning, packing).
  - `Fuse.Indexing` - the SQLite index store, schema, text-processing primitives.
  - `Fuse.Semantics` - syntax-tier extraction, the language-provider seam, the Roslyn typed
    graph analyzers, git co-change mining.
  - `Fuse.Retrieval` - candidate generation, scoring, priors, graph expansion, the
    signal-sufficiency contract, changeset session store.
  - `Fuse.Context` - semantic context emission, manifest building, provenance rendering,
    session deduplication for repeated context calls.
- `src/Host` - user-facing surfaces:
  - `Fuse.Cli` - the CLI commands, the MCP stdio server, and the JSON-RPC host (`fuse host`).
  - `Fuse.BuildCaptureWorker` - out-of-process tier-1 build capture worker (binlog rehydration,
    semantic extraction, graph serialization back to the parent process).
- `src/Plugins` - capability plugins resolved by file extension:
  - `Fuse.Plugins.Abstractions` - the capability registry and options records.
  - `Fuse.Plugins.Languages.CSharp` - the lexer-based C# reducer.
  - `Fuse.Plugins.Languages.CSharp.Roslyn` - the Roslyn skeleton extractor and analyzers.
  - `Fuse.Plugins.Formats.Web` - JSON/XML/CSS/SCSS/JS/HTML/Razor/YAML/Markdown/SQL minifiers.
- `tests/` - unit, golden-output, integration tests. `tests/benchmarks/` - the eval harness,
  corpus manifest, recorded results, peer harness.
- `site/` - the documentation website (Next.js + Fumadocs), published at fuse.codes. All prose
  docs are MDX under `site/content/docs`.
- `briefing.md` (repo root) - this file: project briefing for planners and contributors (architecture,
  benchmarks, gaps, plan history).
- `roadmap/` - the internal engineering plans (v3, v3.1, v3.2, v4); start at
  [roadmap/README.md](roadmap/README.md).
- `assets/`, `mcp-registry/` - the benchmark figure and the MCP Registry manifest.

---

## 3. The pipeline (Collection to Reduction to Emission)

The classic Fuse pipeline is a four-stage flow orchestrated by `FusionOrchestrator`
(`src/Core/Fuse.Fusion/FusionOrchestrator.cs`). Everything is run-scoped: per call it opens
fresh collaborators (content provider, SQLite reduction cache, analysis index) so concurrent
runs never share mutable state.

### 3.1 Collection (`Fuse.Collection`)

`FileCollectionPipeline.CollectAsync` has three input modes: explicit-file mode (bypasses
enumeration and filters, used by `reduce`), candidate-set mode (caller supplies the listing,
filters still run), and directory walk (default). Filtering runs in parallel; a file is kept
only if every `IFileFilter.Include` returns true, then results are re-sorted deterministically
by enumeration index then normalized path.

Security and scope guards during enumeration: rejects paths escaping the root via `..` or a
different drive, skips symlink/junction files and directories reached through them (stat cached
per directory), and prunes files belonging to nested VCS roots (worktrees, submodules, embedded
clones). Filters include gitignore, binary, auto-generated (reads the first 5 lines for
`<auto-generated`), empty, excluded-dir, excluded-filename, extension, size, glob, and
test-project. Gitignore walks up from each source directory to the repo root, compiling each
pattern into a `DotNet.Globbing.Glob`. About 25 per-language project templates supply default
extensions and exclusions (DotNet, TypeScript, Python, Go, Rust, and more).

### 3.2 Reduction (`Fuse.Reduction` plus the language/format plugins)

One `ReductionLevel` dial expands to per-transform booleans:

- **None** - whitespace normalization plus orthogonal flags only.
- **Standard** - removes C# comments, using directives, namespace wrappers, and #region.
- **Aggressive** - Standard plus whitespace/syntax compression (output not guaranteed to
  compile; it maximizes token savings).
- **Skeleton** - type and member signatures, bodies removed.
- **PublicApi** - Skeleton, but only public and protected members.

`ContentReductionPipeline.ReduceAsync` processes files in parallel with a fixed per-file stage
order: normalize whitespace, apply the extension-specific reducer, apply skeleton, prepend
semantic markers, and (always, after caching) redaction. An optional per-file level selector
lets one run mix tiers (seeds full, neighbors skeletonized). Caching keys on XXHash64 of raw
content plus a hash of the reduction options that folds in a schema version but deliberately
excludes emission-only flags so toggling them does not fragment the cache. On a cache hit the
reducer stages are skipped but redaction still re-runs.

How token savings are achieved while the public API survives:

- **Roslyn skeleton reducer** (`RoslynSkeletonExtractor`): parses the syntax tree and runs a
  `BodyStrippingRewriter` that replaces method/constructor/operator bodies with a semicolon,
  strips accessor bodies, and collapses expression-bodied properties to auto-getters. In
  public-API-only mode it drops non-public/protected members (never interface members, which
  are implicitly public). Working from the parsed tree, not regex, means conditional
  compilation, partial classes, and braces-in-strings cannot desync a depth counter.
  Signatures survive, bodies vanish: a large token cut with the contract intact.
- **Lexer-based C# reducer** (`CSharpReducer`): masks every string/char literal (regular,
  verbatim, interpolated, raw) with opaque placeholders before any transform, so comment
  stripping can never reach into literal contents; then removes comments/directives/usings/
  namespaces via source-generated regex, optionally applies aggressive optimization, and
  restores the literals.
- **Generated-code collapse**: detects EF Core migrations and model snapshots and replaces the
  bodies of Up/Down/BuildModel/BuildTargetModel with a marker while keeping the signatures.
- **Format reducers** (`Fuse.Plugins.Formats.Web`): whitespace minifiers for JSON, XML, CSS,
  SCSS, JS, HTML, Razor, YAML, Markdown, SQL.

Secret redaction (`DefaultSecretRedactor`) is regex plus entropy: named patterns (AWS keys,
JWT, PEM private-key blocks, GitHub/Google/Slack/Stripe tokens, generic api_key/token
assignments), connection strings (only a quoted literal with two or more semicolon-delimited
pairs and a connection keyword), and a high-entropy heuristic (quoted literal 32 or more chars,
Shannon entropy 4.5 or higher, mixed case, digit, and symbol). It can classify which redactions
fall inside C# string literals as a diagnostic.

### 3.3 Emission (`Fuse.Emission`)

`EmissionPipeline.EmitAsync` builds a token budget, orders entries (most-relevant-first when any
entry has a relevance score, else descending token count), writes an optional manifest prefix,
then loops entries charging token count plus a 30-token marker overhead against the budget. The
budget has two limits, `MaxTokens` (hard) and `SplitTokens` (per part), and checks before
committing so an over-budget entry is never charged or emitted. If nothing fits, it force-emits
the single most-relevant entry so a scoped run never returns empty.

Token counting: exact offline counts via `Microsoft.ML.Tokenizers` Tiktoken (default
`o200k_base`), or a deterministic approximate counter for providers without a local tokenizer.
The manifest carries per-file token counts and, when git stats are available, commit count and
last date. Provenance (the inclusion chain: why each file is here, and which members were
selected) is rendered by the entry formatters. Disk output streams to temp files then renames to
token-tagged names; a table-of-contents mode degrades detail (Full to PathsOnly to Directories)
until it fits.

### 3.4 Scoping and packing (in `Fuse.Fusion`)

Three mutually exclusive scoping modes (`Stages/FusionScopingStage.cs`): Focus (resolve seed
paths or a Type.Member symbol slice, build a dependency graph, compute centrality, expand
outward with provenance), Changes (git changed files, optional dependents expansion, and a
diff-plus-callers preamble in Review mode), and Query (delegate to the BM25F pipeline over the
dependency graph).

BM25F relevance (`Scoping/Bm25RelevanceIndex.cs`) is a fielded inverted index with four fields
(Body, Symbols, Path, Comments), each with its own length-normalization b and boost. A term in
a declared symbol name counts 5x, in the path 3x, in comments 1.5x, versus the body at 1x.
K1 = 1.2. This is the in-memory BM25F used by the classic fusion path (the host RPC `fuse/scope`
path); the persistent index uses FTS5 bm25 with a corrected weight table (section 5,
N1 shipped). N2 part 1 archived stale results and fixed citations; deletion of the in-memory
ranker from the classic fusion path is deferred (non-shipping, heavy test coupling).

Context planning (`Scoping/ContextPlanBuilder.cs`) labels each file Seed / Dependency / Changed
and assigns a reduction tier once: tiered emission downgrades non-seed dependencies to Skeleton
while seeds keep the requested level. Strict total-token accounting does a two-pass pack: pass
one packs bodies into the whole budget, then it measures the framing (manifest, maps, redaction
report, preambles) for that set, and pass two re-packs into MaxTokens minus that reserve, so the
complete payload the client receives fits the cap. The packer (`Scoping/ReductionAwarePacker.cs`)
is a greedy 0/1-knapsack over real reduced token cost by descending relevance-per-token density,
admitting the single most-relevant entry unconditionally.

---

## 4. The persistent semantic index (`Fuse.Indexing`)

All cache and index data live in a single SQLite file at `.fuse/fuse.db` in WAL mode. This is
the warm store the MCP tools and the host read from.

Schema version `WorkspaceIndexSchema.TargetVersion = 15` (v15 adds type-level `references`
edges from `ReferenceEdgeAnalyzer`, R5). Pragmas: WAL, synchronous NORMAL.
Relational tables: `files` (with `normalized_path` unique, `content_hash`, `language`,
`is_generated`, `is_test`, `project_id`), `projects`, `nodes` (typed-graph node: kind,
display_name, symbol_id, stable_key, span), `symbols` (assembly-qualified id,
fully_qualified_name, is_public_api), `chunks` (token_estimate, reduced_token_estimate,
outline), `edges` (from/to node, edge_type, weight, confidence, evidence file plus span, unique
on from/to/type/evidence), `routes`, `di_registrations`, `options_bindings`, `git_cochange`
(path pair, count, pmi, jaccard, last_seen), `chunk_embeddings` (chunk_id, dim, vector blob).

An FTS5 virtual table `chunk_fts` is created separately so a runtime lacking FTS5 still builds
the relational schema. Its columns, in this order (order is load-bearing for the bm25 weights):
`chunk_id UNINDEXED, path, name, symbols, signature, comments, body, subtokens, stems`,
tokenizer `unicode61`.

Two independent freshness mechanisms:

1. **Schema version** - `WorkspaceIndexMigrator` reads `schema_version`; if below target it
   drops every Fuse-owned object (so stale tables from old schemas go too) and recreates. There
   is no incremental migration in V3; it rebuilds from scratch.
2. **Build compatibility** - `FuseBuildInfo.Current` reads the assembly informational version;
   `IsCompatible` compares by major.minor only (a patch keeps the extraction contract, a
   minor/major may not) and treats null as compatible so a pre-stamp index is not wiped. The
   store stamps `fuse_version` into `index_meta` on every pass; on init, if incompatible, it
   rebuilds. `EnsureTablesAsync` runs every init (all IF NOT EXISTS) to self-heal additive
   changes within a version.

A third mechanism, the **N6 freshness contract**, applies on warm MCP read paths:
`ReconcileDirtyFilesAsync` compares stored content hashes against on-disk files before answering.
If the dirty count exceeds 300 (storm threshold), it stamps `stale_dirty_count` in `index_meta`
and skips reconcile rather than blocking the agent; otherwise it re-indexes each dirty known file
(syntax rows only; cross-file edges still need a full pass, issue 5). The host RPC path opens the
same store but does not yet call reconcile on warm reads; it relies on `fuse/invalidated` to
trigger a refresh instead.

**Oracle availability header**: store-backed MCP read tools prepend a one-line grade from
`OracleAvailabilityHeaderAsync`: index mode (semantic/partial/syntax), tier-1 build capture
configured or not, and stale-as-of stamp when present. This is the oracle-side generalization of
the signal-sufficiency contract: when a compiler-backed answer cannot be produced, the tool says
"cannot verify" rather than guessing.

Edge ids are a stable xxHash64 of (from, to, type, evidence file) so re-index replaces rather
than accumulates. Embeddings are stored as little-endian float blobs. Store status is Cold when
file count is 0, else Warm.

---

## 5. Retrieval and ranking (`Fuse.Retrieval`)

Orchestrated by `SemanticRetrievalEngine`. `LocalizeAsync` flow: low-signal gate (refuse and
navigate), then generate candidates, score, apply the centrality prior, apply the co-change
prior, grade the signal, select by state, optionally expand through the graph, dedupe, truncate.

### 5.1 Candidate generation

Default generators: Exact, Lexical (FTS), Path, Diff, plus Dense when an embedder is available.

- **Lexical / BM25F over FTS5**: `WorkspaceIndexStore.SearchAsync` runs FTS5 `bm25()` with
  positional per-column weights on chunk_fts. Query-time expansion (`BuildMatchExpression`)
  tokenizes the query then adds each term's subword splits and their Porter stems, joined with
  OR, with the raw term kept first so exact-name matches rank highest. The weight vector (N1,
  shipped) ranks name/symbols/signature above path: a term hitting a declared symbol name must
  outrank the same term appearing only in a folder path. The ranking regression suite
  (the ranking suite, `results/ranking.json`) guards this table.
- **Subword and stem index-time fields**: `subtokens` splits camelCase/snake_case/acronyms;
  `stems` is Porter 1980; the `comments` field is the only source for the comments column and
  also feeds the stems bridge. This is the deterministic fix for the vocabulary gap: a prose
  word like "rounding" can now match a code identifier like `ApplyRoundingMode`.
- **Dense**: `DenseCandidateGenerator` embeds the query, cosine against persisted chunk vectors,
  best per file, top 20, normalized. The model is all-MiniLM-L6-v2 (384-dim, quantized ONNX,
  BERT WordPiece, max 256 tokens, mean-pooled and L2-normalized). It is fetched once on the
  indexing path only (never at query time) from HuggingFace `Xenova/all-MiniLM-L6-v2` (about
  23 MB), SHA-256 pinned. On by default (opt out via `FUSE_DENSE`); when absent the path is
  byte-identical to the lexical fallback.

### 5.2 Scoring and priors

`CandidateScorer.Score` groups candidates per node (or file) and combines source scores with
noisy-or `1 - product(1 - score)`, so corroboration raises toward but never past 1. Base source
weights: DiffChangedFile and RouteExact 1.00; Symbol/Service/Request/StackTrace 0.95; ConfigExact
0.90; FtsSymbol 0.75; Dense 0.72; FtsPath 0.70; FtsBody 0.55; Cochange 0.45; GraphNeighbor 0.40.

- **Dependency-centrality prior** (`GraphCentralityPrior`): centrality is normalized node degree
  (in plus out) over the semantic graph; boost is score x (1 + 0.10 x centrality), capped at 1,
  top 30 only. A no-op in syntax mode (no edges).
- **Git co-change prior** (`GitCoChangePrior`): for the top 8 seed files, a candidate that
  co-changes with a strictly-higher-scored seed gets score x (1 + 0.15 x jaccard), capped at 1,
  top 30 only. Mining (`GitCoChangeCollector`) runs one bounded `git log --no-merges --name-only`
  (max 1000 commits), skips commits touching more than 40 source files, keeps pairs seen twice or
  more, computes PMI and Jaccard, has a 20 s hard timeout, and degrades to no rows.

### 5.3 Graph expansion and navigation

`ExpandSeedsThroughGraphAsync` (opt-in) expands the top 2 seeds one hop and admits up to 5
neighbor files at decayed scores. `EdgeWeightProvider` gives per-edge traversal weights with
HopDecay 0.65: route_handles 1.00, mediatr_handles 0.95, di_resolves_to 0.95, implements 0.90,
di_depends_on_impl 0.85, options_binds 0.85, config_impacts 0.80, inherits 0.75, di_injects 0.75,
options_consumes 0.75, sends_request 0.70, tests 0.65 (from `TestEdgeExtractor`, DI-resolved,
post-merge in `SemanticIndexer`), cochanges 0.45, project_references 0.30, path_proximity 0.20,
references 0.15 (from `ReferenceEdgeAnalyzer`; the former `calls` weight was removed as dead,
finding 7). `GraphNeighborhoodExplorer`
provides standalone navigation primitives (neighborhood, callers/implementers, central files),
with a same-folder cohesion fallback in syntax mode.

### 5.4 The signal-sufficiency contract (the strategic centerpiece)

Two stages, both model-free and reproducible:

1. **Low-signal gate** (`QuerySignalClassifier`): before candidate generation. Any structured
   signal (route/focus/service/request/config/changed-since/selected-paths) is immediately
   HighSignal, never rejected. Only free-text-only queries are judged, and only against three
   precise noise regexes (merge, dependency-bump, ci/chore) plus empty. Deliberately conservative
   to keep the false-rejection rate near zero: an answerable prose title is not downgraded.
2. **Score grading** (`SignalGrader.Grade`): from the score distribution alone, fixed thresholds.
   Confident needs top at or above 0.55 and a leading cluster (within a 0.15 gap) of at most 3
   that stands clear of the tail; top below 0.30 is Insufficient; otherwise Partial. Confident
   returns only the leading cluster (the precision win); Partial returns a small best-effort set;
   Insufficient returns nothing under strict mode, else best-effort, plus a `NavigationMap`
   (candidate areas, entry points, nearest symbols, and an ask). `NavigationMapBuilder` builds a
   real map even for an insufficient result so the refusal is a navigation step, not a wall.

This contract is the load-bearing honesty mechanism of the whole design. The oracle availability
header (section 4) extends the same principle to compiler-backed tools: when tier-1 is not
configured or the owning project did not load clean, the tool says "cannot verify" rather than
guessing.

---

## 6. The semantic (typed-graph) tier: Roslyn edge extraction

This is the deterministic moat and the part no lexical or embedding tool reproduces.

Roslyn via MSBuild. `RoslynWorkspaceLoader` registers MSBuildLocator once per process, opens the
solution/projects via MSBuildWorkspace, and produces loaded projects carrying the Compilation.
Any failure (no SDK, unrestored packages) sets `SemanticLoadSucceeded = false` and the caller
falls back to syntax indexing. This soft fallback is why most cloned repos load partial or
syntax rather than full semantic today. N4 shipped tier-1 build capture as an opt-in oracle path
(default off; see section 6.2); the in-process MSBuildWorkspace loader remains the default
index path and is still fragile on arbitrary clones. The N4 bake-off (`results/n4-bakeoff.json`)
records about 65 percent build-success as the oracle coverage ceiling on the corpus.

`SemanticAnalysisRunner.CreateDefault` wires the analyzers in order, each implementing
`ISemanticAnalyzer.Analyze`:

- **DiRegistrationAnalyzer** - AddScoped/Singleton/Transient and TryAdd variants, generic
  `<TService,TImpl>`, self-registration, typeof pairs, factory-lambda implementation recovery.
  Emits `di_resolves_to` and Scrutor `di_decorates`.
- **ConstructorInjectionAnalyzer** - `di_injects`, `di_depends_on_impl`.
- **MediatRAnalyzer** - matches handler/request interfaces by simple name (real MediatR or a
  local equivalent both work); emits `mediatr_handles` and `sends_request`.
- **AspNetRouteAnalyzer** and **EndpointAnalyzer** - `route_handles`.
- **OptionsBindingAnalyzer** - `options_binds`, `options_consumes`.
- **InterfaceImplementationAnalyzer** - `implements`, `inherits`.
- **HostedServiceAnalyzer** - `hosted_service`.
- **PipelineBehaviorAnalyzer** - `pipeline_behavior`.
- **EfCoreAnalyzer** - `ef_entity`, `ef_configures`.
- **ReferenceEdgeAnalyzer** - type-level `references` edges (R5, weight 0.15 in graph traversal).

These eleven analyzers in the runner target first-party Microsoft and common-library patterns.
After the runner merges, `TestEdgeExtractor` adds DI-resolved `tests` edges (post-merge in
`SemanticIndexer`, not in the runner list). Third-party
containers and frameworks (Autofac, Lamar, Wolverine, FastEndpoints, Carter, source-generated
DI) are not yet covered; widening that coverage is the community on-ramp in the v4 plan (G2).

The seam story: extraction is provider-driven at the syntax tier (`ILanguageSyntaxProvider`
claims extensions and extracts symbols/chunks with no compiler; C# is `CSharpSyntaxProvider`,
plus Python and JS/TS regex providers), and each file carries a `language` tag. The semantic
(typed-graph) tier is still C#/Roslyn-only: the semantic-tier provider seam is not yet
load-bearing. So today a second language reaches the syntax floor but not the wiring graph.

### 6.1 The tiers: cold-start syntax versus full semantic

Three indexer entry points:

- `IndexAsync` - full synchronous pass (tier-1 build capture when configured, else MSBuild
  semantic, else syntax fallback, plus co-change mining).
- `IndexSyntaxFirstAsync` - syntax tier only, no MSBuild load, no embedding, serves in a few
  seconds; sets index_mode syntax and semantic_pending 1.
- `UpgradeToSemanticAsync` - calls IndexAsync, which clears the pending flag when the full graph
  and embeddings land.

Host orchestration reads `FUSE_BG_UPGRADE` (off by default). When enabled in the `mcp serve`
host, a cold read serves syntax-first then schedules a background upgrade supervised by
`SemanticUpgradeSupervisor` (N3 part 1): deduped per root, failures logged to stderr, cancelled
and drained on host shutdown so no task is orphaned. It remains off by default because the CLI
`fuse index` is always a synchronous full pass and short-lived callers must not leave background
work running. Documented cold timings (NodaTime, `performance.json`): syntax tier about 17.8 s;
full semantic pass about 24.6 s; syntax-first plus background upgrade to semantic-ready about
48 s additional. N3 remainder (resident workspace plus dependency-scoped semantic re-index) is not yet
shipped; incremental reconcile still updates syntax rows only (issue 5).

### 6.2 Tier-1 build capture (oracle-grade, opt-in)

Because MSBuildWorkspace and binlog-rehydration libraries cannot coexist in one process (B1),
tier-1 runs out-of-process via `Fuse.BuildCaptureWorker`:

- Enable with `FUSE_BUILD_CAPTURE=1` and `FUSE_BUILD_CAPTURE_WORKER` pointing at
  `fuse-build-capture.dll`.
- The worker runs `dotnet build` with a binary log, rehydrates compilations, runs the semantic
  analyzers, and serializes the graph back; the parent ingests nodes, edges, symbols, routes, DI,
  and options, then runs the syntax pass for chunks and embeddings.
- Index mode is `semantic` when every project has zero errors, else `partial`.
- Powers `fuse_check` (speculative single-file typecheck), `fuse_refactor` (Roslyn rename as a
  staged diff), and changeset diagnose. All abstain with "cannot verify" when tier-1 is not
  configured or the owning project did not load clean.
- `fuse doctor` (CLI) actively loads the workspace and reports the achieved tier plus a
  per-project downgrade reason (unrestored, SDK mismatch, compile errors), so oracle coverage
  gaps are visible before an agent hits abstention in a loop.

---

## 7. The surfaces: CLI, MCP server, host RPC

One executable (`Fuse.Cli`) hosts three surfaces over one shared engine (`SemanticRetrievalEngine`
plus `WorkspaceIndexStore`), so agent, ambient hooks, and CLI see identical data. The command
framework is DotMake.CommandLine.

### 7.1 CLI commands

`index`, `map`, `localize`, `resolve`, `context`, `review`, `find`, `diagnostics`, `doctor`,
`reduce`, `init`, `models`, `eval`, `update`, `host`, and `mcp` (with `mcp install` and
`mcp serve`). `doctor` loads the workspace and reports semantic tier plus per-project downgrade
reasons (see section 6.2). `update` solves the self-update file-lock problem for a global tool:
a running tool locks its own
files on Windows, so it stops other running Fuse hosts and launches a detached platform-native
updater script that waits for the process to exit before replacing files.

### 7.2 The MCP tools (nine)

All defined on the partial class `FuseTools`, each `[McpServerTool]`, returning descriptive error
strings rather than throwing, and building the index on first use. MCP warm reads run N6 reconcile
(section 4) before answering. Store-backed reads prepend the oracle availability header (index
mode, tier-1 configured or not, stale-as-of when present).

**The loop surface (eight tools plus `fuse_reduce`):**

- `fuse_workspace` (not read-only for `index`/`apply`) - status and lifecycle: `status` (index
  mode, verify grade, freshness), `index` (build or refresh), `map` (symbols, routes, counts),
  `doctor` (per-project load diagnosis), and `apply` (the one explicit tree-write path, a dry run
  unless `write=true`, refusing a path that escapes the root).
- `fuse_find` - the find union keyed by `kind`: `symbol|path|text|all` (exact lookup),
  `service|request|route|config` (deterministic wiring resolution), `signatures` (a symbol's exact
  signature, reporting syntax-tier gaps rather than inventing them; resident-metadata resolution
  when a resident workspace serves the root), `neighbors` (callers and implementers), and `task`
  (rank candidate files with the graded signal-sufficiency contract). No bodies.
- `fuse_context` - plan and emit scoped, reduced source (bodies, mixed tiers, manifest,
  provenance) for selected seeds; a `sessionId` elides files already sent.
- `fuse_impact` - blast radius for a symbol (callers, implementers, referencers from the
  persisted graph) plus covering tests (lower bound); a package-upgrade break set via
  `package:{id,fromVersion,toVersion}`; abstains on the tier-1 signature-change break set.
- `fuse_check` - speculative single-file typecheck of proposed content; the grade ladder answers
  oracle-grade against the build-captured compilation, else build-grade via `dotnet build`, else
  abstains; R6 repair packets on API-shape diagnostics; a delta mode over persisted sessions.
- `fuse_test` - run the covering tests for a symbol (the tests that reach it through the persisted
  `tests` edges), scoped by filter so the whole suite never runs (build grade).
- `fuse_refactor` - compiler-executed, verify-gated refactors staged as a unified diff (rename,
  the change-signature family, extract-interface, move-type, apply-codefix); never writes disk;
  abstains on partial load or a recompile that introduces a diagnostic.
- `fuse_review` - diff-first semantic impact since a git ref: changed files plus blast radius
  (callers, DI consumers, route/request handlers, options consumers, tests) plus packed context,
  with the public API delta and a paste-ready PR handoff packet.
- `fuse_reduce` - compact a known file set or raw content at a chosen level (the one out-of-loop
  utility).

MCP resources cover the map, localize, context, and review workflow reads plus the status, diff,
diagnostics, and session-ledger reads in a fixed-default addressable form. v4 is the first public
release and ships exactly these nine tools with no deprecation shims and no legacy names (D14).

### 7.3 The host RPC (`fuse host`)

`fuse host` is a long-lived process, one per repo root, sharing the same engine and
`.fuse/fuse.db` store. Transport is a named pipe on Windows, a Unix domain socket elsewhere;
the endpoint address is a stable SHA-256 of the normalized repo-root path (8 bytes hex). Methods
(`[JsonRpcMethod]`, `fuse/` namespace): handshake, stats, index, graph, scope, explain,
diagnostics, shutdown. Every method except handshake is gated by a per-session random token. A
debounced file watcher broadcasts `fuse/invalidated` to connected clients. Unlike MCP read tools,
the host does not yet run N6 reconcile on warm opens (section 4); it cold-builds when the store
is empty and relies on invalidation to trigger refresh after edits.

Change-safety invariant: any change to a `Fuse.Cli.Rpc` DTO or `[JsonRpcMethod]` signature must
bump `FuseHostService.ProtocolVersion` in the same change. The version constant surfaces a
stale-hook-client or new-host mismatch cleanly. The VS Code extension and its TypeScript protocol
mirror were removed in v4 (Decision D15); ambient verification hooks are the in-repo client.

---

## 8. Known issues and open questions

These were found while assembling this briefing. Each is tagged with its current status and, if
open, the v4 item that addresses it.

1. **Host protocol version drift.** RESOLVED in 3.2.0; the VS Code extension and TypeScript
   protocol mirror were removed in v4 (Decision D15). `FuseHostService.ProtocolVersion` is the
   sole contract stamp; ambient hooks must match the running host version.
2. **BM25 weight vector versus intent comment.** RESOLVED in v4 N1. The FTS5 bm25 column weights
   now rank name/symbols/signature above path (`WorkspaceIndexStore.SearchAsync`). The ranking
   regression suite (the ranking suite, `results/ranking.json`) guards the table.
3. **Two disagreeing lexical rankers.** PARTIAL, v4 N2. N2 part 1 archived stale results and
   fixed citations. The in-memory `Bm25RelevanceIndex` still exists for the classic fusion path
   (host RPC scoping) but is non-shipping; deletion is deferred due to test coupling. The
   persistent FTS5 path is the shipping ranker and matches N1's weight table.
4. **Corpus generational gap in recorded results.** MOSTLY RESOLVED. The current corpus
   (`corpus.json`) no longer references the prior 5-library set (AutoMapper, FluentValidation,
   MediatR, Newtonsoft, Serilog). Main suite results were regenerated or archived under N2/N5.
   **Caveat:** `agent.json` notes that 10 of its 12 PRs still come from the retired corpus; read
   Suite D as a directional pre-R4 record, not a current-corpus headline.
5. **Incremental re-index does not refresh semantic edges.** OPEN, v4 N3 remainder.
   `ReindexFileAsync` and N6 reconcile re-extract a file's syntax rows only; cross-file graph
   edges (DI, route, MediatR, EF, references) are recomputed only by a full `IndexAsync`. N3
   adds a dependency-scoped semantic re-index over a resident compilation.
6. **Host N6 reconcile gap.** OPEN. MCP read tools reconcile dirty files on warm opens; the host
   RPC path does not yet call `ReconcileDirtyFilesAsync` before answering.

---

## 9. Measured results (source of truth)

This section is written so a planner can read it without the source tree: every number below
comes from a file under `tests/benchmarks/results/`, counted with `o200k_base`, and never
fabricated or rounded. The harness is the C# library `tests/benchmarks/Fuse.Benchmarks`, invoked
in the separate `Fuse.Benchmarks.slnx` solution. Fuse 4.4 does not package an evaluation command,
so a suite is driven from that solution rather than from the installed tool. Each suite writes a
scorecard JSON; the canonical files are listed in
9.0. Never quote numbers from `results/archive/` (superseded runs).

### 9.0 Shared methodology

**Corpus** (`tests/benchmarks/corpus.json`, corpus v2 with 69 PRs in `prs.json`; the retired
mixed corpus with 53 PRs is preserved as `corpus-retired.json` / `localize-retired.json` for
provenance): Scrutor, Ardalis Specification, NodaTime, eShopOnWeb (the application), plus
in-repo fixtures OrderingApp (Suite A) and SampleShop. Repos are cloned `--no-checkout` then
detached at a pinned commit. PR ground truth is reconstructed from merge commits (parent 1 = base,
parent 2 = head), keeping diffs of 2 to 25 changed C# files and dropping maintenance titles that
cannot locate their own diff.

**Signal buckets** (title classification in `prs.json`): no-signal, dependency-bump, config-ci,
formatting, route-api, test-only, identifier-rich, nl-domain. Only no-signal is treated as
low-signal for the abstention contract.

**Index mode** is recorded per run and caps what the numbers mean. Semantic mode needs a restored
workspace (`dotnet restore`, `--restore` on corpus suites). Syntax mode is a thin graph; partial
mode has compile errors. Most corpus-suite numbers sit below the Suite A semantic ceiling because
most checkouts load syntax or partial in this environment (see 9.13).

**Metrics (file-set tasks):**

- **Changed-file recall** = |returned intersect truth| / |truth|. Truth is the PR's changed C#
  files unless noted.
- **Precision** = |returned intersect truth| / |returned|. Empty return: precision undefined,
  often reported as 0 in aggregates.
- **F1** = harmonic mean of recall and precision.
- **Median/mean returned tokens** = `o200k_base` count of the scoped payload the tool returned.
- **Bootstrap CI** on recall: deterministic percentile bootstrap, seed 1469, 2000 iterations
  (small-N confidence bands in scorecard).

**Ranking metrics** (Suite ranking, no token budget): MRR, recall@1/5/10, nDCG@10 against
changed-file ground truth, title-only input, top 20 candidates.

**Contract metrics** (Suite C): low-signal detection F1; false-rejection rate on answerable
queries; precision when confident (confident grade only); graded states (confident / partial /
insufficient).

**Reproduce arguments** (driven from `Fuse.Benchmarks.slnx`; the packaged CLI has no eval command):

```text
semantics                    # Suite A (in-repo fixture, no corpus)
review --restore             # Suite B
localize --restore           # Suite C (lexical channel, shipping default)
ranking --restore            # ranking gate
checkgate                    # Suite F (in-process plus mutation arm)
checkgate --mutations 500    # Suite F mutation gate (1000 verified cases)
loop --restore --limit 1     # Suite R4 (model + claude CLI; needs FUSE_LOOP_RUN=1)
agent --restore              # Suite D (model + claude CLI)
reduce                       # Suite E
performance                  # latency (writes performance.json)
```

The peer comparison stays a script:

```bash
pwsh -File tests/benchmarks/harness/layer6-peers.ps1
```

**Canonical result files** (current headline numbers):

| File | Suite | Deterministic? |
|------|-------|----------------|
| `semantics.json` | A, wiring fixture | yes |
| `review.json` | B, change impact | yes |
| `localize.json` | C, shipping default (lexical) | yes |
| `localize-retired.json` | C, retired mixed corpus | yes (provenance) |
| `localize.tier1.json` | C, tier-1 build capture on retired corpus | yes |
| `ranking.json` | N1 ranking gate | yes |
| `checkgate.json` | F, check honesty | yes |
| `loop.json` | R4, loop metric | no (model) |
| `agent.json` | D, agent sufficiency | no (model; retired-corpus caveat) |
| `reduce.json` | E, reduction fidelity | yes |
| `performance.json` | warm/cold latency | yes (env-dependent) |
| `n4-bakeoff.json` | N4 mechanism spike | yes |
| `layer6-peers.json` | peer comparison | partial (coa/serena model-driven) |
| `semantics-corpus.json` + `semantics-corpus-sample.json` | corpus edge sample for adjudication | yes |

---

### 9.1 Suite A: semantic resolution (`semantics.json`)

**Question:** Does the extracted semantic graph match hand-built edge ground truth?

**How measured:** Index the in-repo OrderingApp wiring fixture (no corpus clone). Compare every
predicted edge to a curated ground-truth set. One task. No token budget, no model. Covers DI
registration and constructor injection, MediatR, ASP.NET routes, options binding, EF Core,
Scrutor decoration, factory registration, hosted services, pipeline behaviors, minimal-API/gRPC/
SignalR, plus precision cases (explicit open generic, TryAdd, multiple-implementation ambiguity
where only the registered impl resolves).

**Result:**

| Metric | Value |
|--------|------:|
| Edges matched | 24 / 24 |
| Recall | 1.0 |
| Precision | 1.0 |
| False positives | 0 |

**Finding:** The deterministic moat is proven in kind on a hand-built fixture. Corpus-wide
wiring precision is not fully adjudicated; the semantics suite with `--corpus-sample N` samples
predicted corpus edges into `semantics-corpus-sample.json` for human adjudication
(`semantics-corpus.json`: 261 predicted edges, index modes partial 2 / syntax 4, 24 sampled).

---

### 9.2 Suite B: change impact and review (`review.json`)

**Question:** Does `fuse review` return the changed files plus relevant semantic blast radius?

**How measured:** 69 PRs on corpus v2 (retired mixed corpus: 53 PRs), 25,000-token budget,
`--restore` (each PR worktree restored before index). Changed files are seeded must-keep, so
changed-file recall is 100 percent by construction; the discriminating metric is **precision**.
Grep baseline: rank C# files by title-token matches, admit to the same budget.

**Result:**

| Metric | Value (corpus v2) | Retired mixed corpus |
|--------|------:|------:|
| Tasks | 69 | 53 |
| Changed-file recall | 100% | 100% |
| Precision | 93.4% | 79.8% |
| F1 | 0.966 | 0.89 |
| Median returned tokens | 1,026 | 958 |
| Index modes | semantic 33, partial 18, syntax 18 | partial 27, semantic 1, syntax 25 |
| Grep baseline recall / precision | 67% / 8% | 53% / 14% |

**Finding:** Review is the practical strength: high-precision blast-radius scoping in about a
thousand tokens, decisively beating grep. On the buildable corpus v2 a majority of PRs load
semantically (33 of 69, versus 1 of 53 on the retired corpus), so precision rises to 93.4 percent.
The corpus-v2 ground truth is the reconstructed changed-file set (no adjudicated reading set).
Application external validity is covered by a supplementary run (`review-app.json`): eShopOnWeb now
loads its main checkout at semantic tier (was partial/syntax) and review scores 100 percent recall at
84.2 percent precision over its 8 PR worktrees, median 366 tokens, grep 73/10 - precision just below
the library 93.4 percent because an application's blast radius legitimately pulls in wiring. The
retired mixed corpus (`review-retired.json`) is preserved for provenance.

---

### 9.3 Suite C: open-ended localization (`localize.json` and variants)

**Question:** Given only a PR title (no git base), does `fuse_find kind=task` (or the CLI
`localize` command) find the changed files?

> Note (v4.1 K1, 2026-07-08): the dense embedding channel and the ONNX plugin were retired.
> Retrieval is the lexical channel with offline subword, stem, comment, dependency-centrality, and
> an opt-in git co-change prior (off in the shipping default since D6). `FUSE_DENSE` and
> `FUSE_RERANK` are no longer read. Pre-K1 dense A/B artifacts (`localize.a1-lexical.json`) are
> archived for provenance only.

**How measured:** Corpus v2, 69 PRs, title-only query, top 20 candidates, `--restore`. The
signal-sufficiency contract is graded on every query. The retired mixed corpus (53 PRs,
`localize-retired.json`) preserves the low-signal abstention measurements.

**Result (shipping default, `localize.json`, corpus v2):**

| Metric | Value (corpus v2) | Retired mixed corpus |
|--------|------:|------:|
| Changed-file recall | 37.7% (CI 30% to 46%) | 15.0% |
| Precision | 21.1% | 8.1% |
| Median returned tokens | 1,348 | 1,033 |
| Index modes | semantic 14, partial 4, syntax 5 | partial 2, syntax 2 |
| Bucket identifier-rich | 52% (n=28) | 21% |
| Bucket nl-domain | 24% (n=31) | 17% |
| Precision when confident | 46.7% (10 tasks) | 5.6% (9 tasks) |

The buildable corpus more than doubles open-ended recall (37.7 percent versus 15.0 percent on the
retired mixed corpus) because more of it loads semantically. The reconstructed set drops
maintenance titles, so it carries no no-signal tasks; the low-signal refuse-and-route contract
(F1 1.0, false rejection 0.0 percent) is measured on the retired corpus, which had them.

**Tier-1 re-run (`localize.tier1.json`, retired mixed corpus):** recall 15.0% versus 15.0%
baseline; index modes unchanged (partial 2, syntax 2). No measurable lift from a richer graph on
the same repos; build capture is a verification asset, not an open-ended-recall lever (N4 Phase 4
gate).

**Finding:** Weakest deliberate mode on title-only queries, but corpus v2 recall is 37.7 percent
when more repos load semantically. Recall remains bounded by index mode more than retrieval
cleverness. The contract works: refuses low-signal rather than returning junk. With a git base,
use review (Suite B) instead.

---

### 9.4 Ranking regression (`ranking.json`)

**Question:** Do lexical field weights rank symbol-name matches above folder-path matches?

**How measured:** Corpus v2, 69 PRs, title-only, changed-file ground truth, top 20 candidates,
three configs: lexical only; shipping default (lexical plus centrality, co-change off); diagnostic
default-plus-cochange (re-adds the git co-change prior). Recorded 2026-07-12. Required gate on any
weight, tokenization, expansion, or prior change.

**Result (corpus v2, 69 PRs, index modes semantic 14 / partial 4 / syntax 5):**

| Config | MRR | recall@10 | nDCG@10 |
|--------|----:|----------:|--------:|
| lexical | 0.489 | 38.2% | 0.361 |
| default (lexical + centrality, co-change off) | 0.489 | 38.2% | 0.361 |
| default-plus-cochange (diagnostic) | 0.434 | 36.3% | 0.320 |

Co-change prior delta (re-adding it, on minus off): MRR -0.055, recall@10 -1.9 percent. (Retired
mixed corpus, `ranking-retired.json`: old default MRR 0.197, recall@10 15.0 percent, delta -0.011.)

**Finding:** N1 weight fix is guarded. The buildable corpus lifts ranking materially (default MRR
0.489 versus 0.197). D6 DISCHARGED on a semantic-mode corpus: the git co-change prior is net-negative
(re-adding it costs MRR -0.055 and recall@10 -1.9 percent), the evidence D6 held for, so the prior is
dropped from the shipping default and kept measured behind the `default-plus-cochange` diagnostic
config.

---

### 9.5 Suite D: agent context sufficiency (`agent.json`)

**Question:** Under a real model, does an agent gather the task's files with Fuse MCP tools?

**How measured:** Claude Code CLI, model `claude-sonnet-4-6`, max 25 turns, 12 PRs (two per
repo), two arms (native Read/Grep/Glob vs Fuse MCP), one rollout each, 24 rollouts total,
`--restore`. Measures mean file recall and precision of gathered files plus median cumulative
session tokens.

**Result:**

| Arm | Mean recall | Mean precision | Median cumulative tokens |
|-----|------------:|---------------:|-------------------------:|
| fuse | 30% | 44% | 211,502 |
| native | 26% | 43% | 209,182 |

**Provenance caveat (critical):** 10 of 12 PRs are from the **retired** pre-V3.1 corpus
(AutoMapper, FluentValidation, MediatR, Newtonsoft.Json, Serilog); only 2 eShopOnWeb PRs are
current-corpus. The file is an explicit **directional pre-R4 record**, superseded for headlines by
Suite R4 loop metrics. Do not quote as a current-corpus benchmark.

**Finding:** Fuse arm slightly higher recall at comparable precision and token cost on this
small sample. Session-level tokens were flat: per-payload reduction does not shrink the explore-
and-build loop (motivates Suite R4).

---

### 9.6 Suite F: fuse_check honesty gate (`checkgate.json`)

**Question:** Does speculative typecheck ever lie (false green or false red)?

**How measured:** Eight single-file edits on a small raw-Roslyn compilation (no MSBuild): three
known-good (equivalent rewrite, valid overload, comment-only) and five known-bad (missing member
CS1061, wrong return CS0029, undefined type CS0246, syntax error CS1513, init-only assignment
CS8852). Each edit replaces one document; classified with the shipped `CheckResult.IsClean`
rule. Abstention counts as neither false green nor false red. A mutation arm adds 1,000
compiler-verified cases (500 breaking, 500 neutral) from Roslyn rewriters over OrderingApp,
labeled by the compiler. A verify-agreement arm compares oracle-grade and build-grade paths on
24 OrderingApp mutants when the build-capture worker is provisioned.

**Result:**

| Metric | Value |
|--------|------:|
| Cases | 8 (+ 1,000 mutation-verified) |
| Correct | 8 / 8; mutations 1,000 / 1,000 |
| False green | 0 (0.0%) |
| False red | 0 (0.0%) over 1,000 verified mutations |
| Abstained | 0 |
| Verify-agreement (24 mutants) | diagnostic-id 24/24; verdict 24/24 |
| Gate | PASS |

**Finding:** In-process and mutation-derived classification contracts are honest. Verify-agreement
meets the 99 percent gate at 100 percent on the recorded 24-mutant sample. SampleShop mutants are
skipped in-process (recorded, not fabricated).

---

### 9.7 Suite R4: loop metric (`loop.json`)

**Question:** Does the fuse arm reach a green build in fewer build-gated turns than native?

**How measured (B1 reduced-scope referendum, D22a-fixed harness):** driver the `claude` CLI 2.1.181
pinned to `claude-sonnet-4-6`, max 40 turns, opt-in via `FUSE_LOOP_RUN=1`. Two arms: native
(filesystem + `dotnet build`/`test`) vs fuse (MCP tools, verify via `fuse_check` where applicable).
Task set: the 59 verified fail-to-pass oracle tasks in `corpus-tasks-v2.json` (`--restore`), both
arms, two rollouts per arm. Metrics: TRUE pass@1 from a gold-test oracle post-check (the task's
changed tests checked out onto the agent's finished edit and run), false-done (proxy green but
oracle red), median iterations-to-green, and agent-visible `dotnet build`/`test` round-trips counted
apart from `fuse_check` turns. `LoopTranscriptClassifier` + `LoopMetrics` + `OraclePostCheck` are
deterministic and unit-tested; model arms are not byte-reproducible. Reduced-scope by
pre-registration (D21): the corpus meets the 40-task floor but not the full minimums (15/20 tier-1
repos, 59/60 verified tasks), so it is reported with confidence intervals and no headline; it
supersedes the single-rollout pilot (`loop-pilot.json`).

**Result (236 planned rollouts; two wedged and omitted, 234 scored: 118 fuse, 116 native):**

| Arm | n | True pass@1 (95% CI) | Proxy green (CI) | False-done | Median iters | Mean build+test | Mean fuse_check |
|-----|--:|---------------------:|-----------------:|-----------:|-------------:|----------------:|----------------:|
| fuse | 118 | 89% (75/84), 82-95 | 89%, 83-94 | 8 | 1.0 | 3.1 | 0.7 |
| native | 116 | 82% (66/80), 74-90 | 85%, 78-91 | 9 | 1.0 | 3.2 | 0.0 |

**Finding:** On the arena built for it (corpus v2 builds; every task is a verified fail-to-pass
oracle), the harness now measures the true metrics. Fuse resolves more tasks at true pass@1 (89 vs
82 percent, a 7-point edge) and has a lower silent-wrong-answer rate (false-done 8 vs 9). Pre-
registered gates, now measurable: pass@1-within-5-points HOLDS on the true oracle pass@1 (fuse
higher); the build-invocations-at-most-half gate MISSES and is now cleanly measured with `fuse_check`
separated (fuse 3.1 vs native 3.2 agent-visible build+test, essentially equal, not half); the
false-done gate HOLDS (8 vs 9). Two of three gates hold on the true metric; the build-collapse gate
misses but is no longer confounded. The honest reading: Fuse's edge is higher true success and lower
false-done, not halving build round-trips. The earlier 4-PR environment-null and the single-rollout
pilot are both superseded by this arena.

---

### 9.8 Suite E: token reduction and fidelity (`reduce.json`)

**Question:** How much do reduction levels cut tokens while preserving the public API?

**How measured:** Offline over four corpus repos. Count raw `o200k_base` tokens, reduce at each
level through the shipped path, recount. Skeleton fidelity: parse raw source with Roslyn,
count public/protected types and methods surviving (independent ground truth, not circular).

**Result (per repo):**

| Repository | Raw tokens | Skeleton reduction | PublicApi reduction | Skeleton fidelity (types / methods) |
|------------|----------:|-------------------:|--------------------:|-----------------------------------|
| NodaTime | 823,655 | 38% | 60% | 336/336, 1696/1708 |
| Scrutor | 40,868 | 42% | 53% | 107/107, 101/105 |
| Specification | 133,415 | 44% | 49% | 188/188, 336/349 |
| eShopOnWeb | 65,569 | 42% | 47% | 226/226, 186/189 |

Also: standard reduction 11 to 41%; aggressive 16 to 46% by repo.

**Finding:** Skeleton keeps every public/protected type and 99 to 100% of public methods while
removing 38 to 44% of tokens (47 to 60% at public-API level). Token efficiency is a property of
scoped output (review median 1,026 tokens on corpus v2), not session totals (Suite D).

---

### 9.9 Performance and cold start (`performance.json`)

**Question:** How fast are warm reads and cold indexing?

**How measured:** NodaTime checkout, 512 files, 14,760 symbols, semantic index mode on the
product solution `src/NodaTime.slnx` (re-measured 2026-07-15 after the workspace-discovery fix;
the earlier snapshot indexed at syntax tier because discovery had bound the 2-project
`build/Tools.slnx`). 25 repetitions for warm paths; single cold pass. Environment-dependent; not
cross-machine published.

**Result:**

| Operation | P50 | P95 |
|-----------|----:|----:|
| Warm localize | 15.7 ms | 22.3 ms |
| Warm find symbol | 1.8 ms | 2.3 ms |
| Warm resolve | 0.0 ms | 0.1 ms |
| Warm review plan | 106.3 ms | 131.1 ms |
| Incremental re-index (one file) | 22.0 ms | 24.2 ms |

Cold: syntax tier served in 17,768 ms; semantic-ready after further 71,052 ms (background
upgrade path); full semantic pass (tier-1 build capture, graph-grade) 45,859 ms. Incremental
re-index updates syntax rows only, not cross-file semantic edges.

**Resident verify path (`resident-latency.json`, NodaTime, opt-in with `FUSE_RESIDENT=1`):**
resident warm (build plus rehydrate) 14,092 ms; RSS 162 MB; overlay check (speculative
typecheck, analyzers off) P50 31.2 ms / P95 42.4 ms (S1 gate PASS); delta mode (whole-state
diff) P50 203.9 ms / P95 699.3 ms (S2 gate PASS).

---

### 9.10 N4 bake-off: build-capture vs MSBuildWorkspace (`n4-bakeoff.json`)

**Question:** Which loader reaches oracle-grade tier on real repos?

**How measured:** Spike 2026-07-03, Windows 11, SDK 10.0.109. 17 evaluable repos (4 corpus + 13
OSS); 3 clone failures excluded. Mechanism (a): current MSBuildWorkspace loader index mode.
Mechanism (b): tier-1 achievable iff `dotnet build -c Release -bl` succeeds (binlog rehydration).

**Result:**

| Mechanism | Oracle-grade rate |
|-----------|------------------:|
| MSBuildWorkspace (semantic) | 12% overall (2/17); 18% on buildable (2/11) |
| Build capture (build succeeds) | 65% (11/17 repos build); tier-1 on 100% of buildable (11/11) |

**Decision:** Adopt build-capture ladder. **65% build-success is the published oracle coverage
ceiling** in this environment; failures include NU1507 (Scrutor CPM), CS0104 (eShopOnWeb), SDK
skew, MSBuild task load. Non-buildable repos get graph-grade retrieval plus abstention.

---

### 9.11 Peer comparison (`layer6-peers.json`, `layer6-peers.md`)

**Question:** How does Fuse localize compare to CodeGraph, coa-codesearch, Serena on title-only
file retrieval?

**How measured:** 50,000-token budget. Each tool gets PR title; score returned file set vs
changed files. Fuse and CodeGraph: deterministic, 12 PRs. coa (Lucene MCP) and Serena (LSP MCP):
one `claude-haiku-4-5` rollout per PR, bounded to 4 PRs (one per repo). Peers omitted, never
stubbed, when absent. Token columns not directly comparable (fuse/codegraph return source; coa/
serena return path/snippet lists).

**Result:**

| Arm | PRs | Mean recall | Mean precision | Mean tokens |
|-----|----:|------------:|---------------:|------------:|
| fuse | 12 | 19% | 19% | 10,717 |
| codegraph | 12 | 9% | 11% | 3,582 |
| coa | 4 | 9% | 1% | 3,382 |
| serena | 4 | 34% | 27% | 1,538 |

Per-repo fuse recall: eShopOnWeb 12%, NodaTime 36%, Scrutor 7%, Specification 20%. Serena's
aggregate dominated by Scrutor outlier (100% recall on 2-file change in 45-file repo).

**Finding:** Fuse leads CodeGraph on 12-PR deterministic comparison. Serena higher on 4-PR
model sample is not apples-to-apples; on substantive PRs Fuse leads or ties. No broad ranking
claimed at this sample size.

---

### 9.12 Cross-suite synthesis (for roadmap planning)

Read these together when prioritizing work:

1. **Semantic ceiling (A) vs corpus reality (B/C/ranking):** Wiring resolution is exact on the
   fixture (24/24). Corpus suites run mostly syntax/partial, so they understate the moat.
2. **Retrieval is bounded (C, ranking, peers):** 37.7 percent open-ended recall on corpus v2;
   tier-1 did not lift it on the same repos; ranking tweaks are guarded but bounded by index mode.
   Fuse beats CodeGraph on peers but open-ended recall remains moderate, not oracle-grade.
3. **Scoped change work is strong (B):** on corpus v2, 93.4% precision, 1,026 median tokens, beats grep 67/8.
4. **Oracle honesty works (F):** 8/8 check gate plus 1,000 mutation-verified cases; abstention model is load-bearing.
5. **Oracle loop measured (R4):** Fuse leads true pass@1 on the corpus-v2 arena; build round-trips are not halved.
6. **Session tokens flat (D):** Context in without loop collapse; motivates oracle tools and R4,
   not payload reduction alone.
7. **Coverage ceiling (N4 bake-off):** Realized oracle value = theoretical gain x ~65% build-
   success in this environment. Lifting that rate is the dominant product uncertainty.
8. **Reduction is real but secondary (E):** 38 to 44% skeleton cut at 99 to 100% method
   fidelity; does not move session totals.

**Not measured at scale (honest gaps):** full 50 to 100 PR peer run with model-driven arms;
pass@1 task resolution with test oracle across arms; cross-machine latency SLA; corpus-wide
semantic wiring adjudication beyond the 24-edge sample.

---

## 10. The corpus and eval methodology (detail)

Corpus pinning, PR reconstruction, and harness architecture are summarized in section 9.0. This
section adds operational detail for reproducing or extending runs.

**Corpus restore caveats** (recorded in result notes):

- Scrutor: central package management with nested NuGet config triggers NU1507; main checkout
  loads syntax.
- NodaTime and Specification: restore clean on .NET 10 SDK.
- eShopOnWeb: per-PR worktrees restore for review (Suite B); main checkout loads syntax for
  localize (Suite C).

**PR filter:** merge-commit reconstruction, 2 to 25 changed C# files, misleading maintenance
titles dropped. Corpus v2 keeps 69 PRs in `prs.json`; the retired mixed corpus kept 53.

**Harness architecture:** one C# `Fuse.Benchmarks` driver in its own solution (v4 N5 retired
legacy PowerShell layer scripts). Exception: peer comparison orchestration in
`harness/layer6-peers.ps1` with `harness/common.ps1`. All MCP peer calls bounded with wall-
clock backstop and process-tree kill.

**Optional flags:**

- `--restore`: `dotnet restore` before index (required for semantic mode on corpus).
- `--require-semantic`: skip checkouts that fail to reach semantic mode (do not score at syntax
  fallback silently).
- `FUSE_DENSE=0`: retired; lexical-only was the pre-K1 A/B flag (archived results only).
- `FUSE_BUILD_CAPTURE=1` + `FUSE_BUILD_CAPTURE_WORKER`: tier-1 indexing and check/refactor
  oracle paths.
- `FUSE_LOOP_RUN=1`: enable model rollouts in loop suite (never fires silently).

**Archive policy:** superseded runs live in `results/archive/` (old layer scripts, prior
localize experiments). Cite only canonical files in section 9.0 for headline numbers.

**Confidence intervals:** recall CI fields in each scorecard use bootstrap seed 1469; treat
small-N suites (agent 12 PRs, loop 4 PRs) as wide-CI directional signals unless resampled.

---

## 11. The roadmap so far

The internal plans live under `roadmap/`, newest last. Convention: one item equals engine plus
tests plus website docs plus a benchmark in a single change, three gates green (build, test,
format), every number sourced, weaknesses published.

- **v3-plan.md** (the big wave, R0 through R9): built the .NET semantic moat (wiring resolution),
  hybrid retrieval, the abstention contract, the wider analyzer set, warm millisecond latency, and
  the peer and agent runs. Carries the archived V3 overhaul record.
- **v3.1-plan.md**: sharpened the open-ended floor. Strategic pivots: refuse-and-route on low
  signal (graded confident/partial/insufficient), and retire the no-model floor (bundle a local
  dense embedder, on by default, offline at query time, lexical as fallback). Landed the
  signal-sufficiency contract, subword indexing, stemming plus a comment bridge, graph-aware
  discovery, exploration primitives, dense-by-default, the peer harness at scale, the corpus
  rebuild, and the git co-change signal. Deferred the resident workspace, dependency-scoped
  freshness, the semantic-tier seam, tree-sitter, learning-to-rank, and a thesaurus.
- **v3.2-plan.md**: the warm-server finishing wave. Shipped the host-on-semantic-index migration
  (protocol 3) and the rich index panel. Left two of its highest-value items only partly landed:
  the resident Roslyn workspace with dependency-scoped freshness (W1) and semantic-mode corpus
  coverage (W4). Those are picked up in v4.
- [v4.2-plan.md](v4.2-plan.md): the post-4.1 hardening wave (2026-07-14). Closes deferred
  architecture-review findings: briefing drift guards, store split, semantic provider seam,
  scoping unification, host IPC, and operator ergonomics. Twelve checklist items (R1-R12).
- **`roadmap/v4-plan.md`** (4.0.0 release, oracle wave): repositioned Fuse from a retrieval tool into the
  .NET agent's ground-truth oracle. **Shipped in 4.0.0:** L1 (Apache-2.0), L2 (DCO), N1 (ranking
  fix plus suite), N2 part 1 (archive plus citations), N5 (harness retirement), N6 (freshness
  contract), N4 tier-1 build capture (out-of-process worker, default off), N4 `fuse doctor`, N4
  tier-1 localize re-run (no recall lift), N3 part 1 (supervised background upgrade), R5
  (`references` and `tests` edges, schema 15), R6 (the signatures lookup, repair packets on
  `fuse_check`), R2 (`fuse_impact`), R1 (`fuse_check` plus Suite F), R7 part 1 (`fuse_refactor`
  rename), M1 (the changeset lifecycle plus covering-test selection), R4 loop suite (numbers
  recorded), G2 docs (analyzer coverage table). **Partial or deferred:** N2 in-memory ranker
  deletion, N3 resident workspace plus dependency-scoped semantic re-index, R3 tool-surface
  collapse (availability header only), R7 part 2 (change-signature), M2 (stretch, 4.1), G1
  (launch publish). Version bumped to 4.0.0; rationale in section 12 and each item's "Why" in
  the plan.

The stance the plans keep returning to: open-ended recall is bounded by index mode (whether the
checkout loads semantically), not by ranking cleverness; the moat is the deterministic wiring
graph; and the product does not have to win the vague one-shot because it is in a loop with a
model. The v4 plan adds one more: token efficiency per payload is not the product, fewer and
shorter agent iterations is, and the way to get there is to answer the questions the agent would
otherwise run a build to answer.

---

## 12. Where Fuse is going (the thesis and what 4.0.0 shipped)

The v4 thesis, carried in [roadmap/v4-plan.md](roadmap/v4-plan.md): Fuse is the .NET agent's ground-truth
oracle, the tool that answers the questions only a compiler can answer, at edit speed. The
one-line pitch: Fuse gives your agent the compiler.

**What 4.0.0 shipped.** The compiler-oracle MCP tools (`fuse_check`, `fuse_impact`,
`fuse_refactor`, `fuse_test`, and the `fuse_find` signatures kind) plus tier-1 build capture
(opt-in, out-of-process), `fuse doctor`, the availability header, R5 graph edges, Suite F (check
gate), and Suite R4 (loop metric with recorded numbers). Wiring resolution and review were already
oracle-shaped retrieval queries; the oracle tools close the edit-verify loop: typecheck a proposed
edit, list blast radius before changing a signature, rename across the solution as a staged diff,
and diagnose a proposed edit before writing it.

**What the evidence still says.** See section 9.12 for the cross-suite synthesis. In short:
open-ended retrieval recall is 37.7 percent on corpus v2 (Suite C); the tier-1 re-run did not lift
it on the same repos. Suite D's flat session tokens and the B1 loop referendum (on the corpus-v2
arena with the D22a-fixed harness, fuse resolves more tasks at true pass@1, 89 versus 82 percent,
with lower false-done, 8 versus 9, but at essentially equal agent-visible build-plus-test
round-trips, 3.1 versus 3.2, once `fuse_check` is counted separately) show that oracle tools
raise the agent's true success and honesty on the loop, though not by halving build round-trips
on this arena; index mode (whether the checkout loads semantically) still caps realized value on
the wider corpus. Every oracle answer is gated on tier-1 actually loading: realized value is
theoretical gain times load-success rate. The N4 bake-off records about 65 percent build-success
as the coverage ceiling (section 9.10).

**What remains.** N3 resident workspace and dependency-scoped semantic re-index (issue 5); host
N6 reconcile (issue 6); R3 tool-surface collapse; R7 change-signature; a larger buildable loop
run; making tier-1 the default path agents actually hit. The stance the plans keep returning to:
open-ended recall is bounded by index mode, not ranking cleverness; the moat is the deterministic
wiring graph; the product is in a loop with a model and does not have to win the vague one-shot;
and the session-level win is fewer build-gated iterations, not fewer tokens per payload.

For item-level status, blockers (B1 resolved via worker, B2 resident workspace), and risks, read
[roadmap/v4-plan.md](roadmap/v4-plan.md). Every item there traces its rationale back to the analysis in this
overview.
