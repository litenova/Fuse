<!-- mcp-name: io.github.Litenova-Solutions/fuse -->

<p align="center">
  <img src="assets/fuse-icon.svg" alt="Fuse" width="72" height="72">
</p>

<p align="center">
  <a href="https://github.com/Litenova-Solutions/Fuse/actions/workflows/ci.yml"><img src="https://github.com/Litenova-Solutions/Fuse/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI"></a>
  <a href="https://www.nuget.org/packages/Fuse"><img src="https://img.shields.io/nuget/v/Fuse.svg?label=NuGet" alt="NuGet version"></a>
  <a href="https://www.nuget.org/packages/Fuse"><img src="https://img.shields.io/nuget/dt/Fuse.svg?label=downloads" alt="NuGet downloads"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Litenova-Solutions/Fuse" alt="License"></a>
</p>

# Fuse

Fuse is a local .NET tool with a persistent syntax index, typed-graph wiring
resolution on demand, reduced task-scoped source, and pre-write compiler verification
for coding agents. It stores repository facts in `.fuse/fuse.db` and reuses them across
agent turns instead of rediscovering the same structure through repeated file reads and
text searches.

From a .NET project inside a Git repository:

```bash
dotnet tool install -g Fuse
fuse mcp install
```

The installer supports Claude Code, Cursor, GitHub Copilot, OpenCode, Kilo Code, Codex,
and Grok Build. Use `--client <name>` to configure one client; the default `all` configures
all seven for the selected scope. `fuse mcp install` writes MCP registration and a short,
versioned managed block in the client's documented instruction file, such as `AGENTS.md` or
`CLAUDE.md`. Pass `--no-rules` to register MCP without the managed block. `--with-hooks`
separately writes project-scoped Claude Code hooks. Run `fuse mcp doctor` after installation
to inspect registration, managed guidance, daemon reachability, and index state. See [Connect
your coding agent](https://fuse.codes/docs/start/connect-your-ai)
for the exact file and scope matrix.

Reload your MCP client, then ask:

```text
Resolve IOrderService to its implementation, then check the proposed OrderService.cs edit
with fuse_check before writing it.
```

When the MCP server starts, its shared local daemon owns one index job for the repository.
A cold read starts a syntax index and reports its job state. Run `fuse index` to render
progress in a terminal, `fuse index --semantic` for explicit compiler analysis, and
`fuse index status` or `fuse index cancel` to control the job. The installer adds `.fuse/`
to `.gitignore` at project scope.

Every MCP operation except `fuse_reduce` requires a Git repository identity. Fuse walks upward to the nearest
`.git` directory or file, so a call from a nested source or output folder uses the same
repository-root daemon, lock, and index. A non-Git folder returns
`workspace_identity_unresolved:` without starting a daemon or writing an index;
`fuse_reduce` remains available. Each warm index records its repository root and complete
file inventory. A missing or incomplete manifest rebuilds automatically even when the
database already contains file rows.

<p align="center">
  <img src="assets/demo/fuse-check-demo.gif" alt="An agent proposes an edit with an invalid OrderOptions member. fuse_check returns CS1061 and a repair packet, then verifies the corrected proposal." width="820">
</p>

Analysis runs locally and can work offline. Fuse walks a typed graph of DI registrations,
handlers, routes, and callers, emits reduced source for a selected scope, and lets a coding
agent typecheck proposed single-file content before writing it. No model is required. The
optional update check can contact NuGet, and build-grade operations can use the package
feeds configured for the repository.

## Persistent Discovery and Reduced Context

Coding agents can inspect a repository through file reads, grep, and regex. On a large
solution, those operations can rediscover the same symbols, references, registrations, and
project structure across several turns. Fuse performs that discovery through MSBuild and
Roslyn, persists the result, and incrementally re-indexes files as they change.

When a project loads semantically, the graph records DI registrations, request handlers,
routes, options bindings, and call edges. When it does not, Fuse falls back to syntax-level
indexing for that project and reports the mode.

- **Resolve wiring.** `fuse_find` traces a service, request, route, or configuration
  section to the code that handles it. Text search finds `IOrderService`; Fuse follows the
  registration to the implementation that runs.
- **Pack branch context.** `fuse_review` seeds on the git diff and returns related callers,
  handlers, and tests with provenance. On 69 recorded pull requests the median response was
  1,026 tokens at 93.4 percent precision (`review.json`).
- **Return less source.** `fuse_context` reduces the selected files under a token budget
  and records why each file was included. Across four recorded repositories, skeleton
  reduction removed 38 to 44 percent of tokens while retaining every measured public and
  protected type name (`reduce.json`).
- **Read warm.** On the recorded NodaTime run (semantic tier, 14,760 symbols), exact
  symbol lookup took 1.8 ms at the median, task localization 15.7 ms, and review planning
  106.3 ms (`performance.json`; timings are environment-dependent).

<p align="center">
  <img src="assets/fuse-typed-wiring.svg" alt="Fuse resolves an interface through dependency injection registration to its concrete implementation and related callers." width="820">
</p>

## Compiler Checks and Change Verification

- `fuse_check` checks the proposed content of one file and returns compiler diagnostics
  without changing the working tree. Oracle grade reuses compiler state captured from the
  real build. Build grade runs a scoped `dotnet build` for the owning project when captured
  state is unavailable. Supported API-shape errors can include a repair packet; in the
  recorded run, the top suggestion repaired 20 of 20 near-miss member and type errors
  (`diagbench.json`).
- Before changing a public method, `fuse_impact` finds callers, implementations, and
  referencing types. Given a package id and two versions, it returns the break set for
  that NuGet upgrade.
- When a signature must change, `fuse_refactor` stages the refactor as a diff and
  returns it only when the compiler reports no new diagnostic.
- After an edit, `fuse_test` selects and runs the test types that reach the changed
  symbol, grouped by owning project, instead of starting with the whole suite.

Every answer names how it was produced. Fuse calls this the **verification grade**:
oracle grade checks against the compilation captured from the real build, build grade
runs a scoped `dotnet build`, and when neither compiler path can answer, Fuse abstains
and names the missing prerequisite instead of guessing.

## What the Recorded Results Cover

Every result below comes from `tests/benchmarks/results` and has a reproduction command
on the [benchmarks page](https://fuse.codes/docs/project/benchmarks).

- Across 1,000 compiler-labeled edits in the recorded OrderingApp test app (500 breaking,
  500 neutral), `fuse_check` reported zero broken edits as clean and rejected zero valid
  edits (`checkgate.json`).
- In the same app, Fuse matched all 24 expected .NET wiring links with no extra matches
  (`semantics.json`).
- Across 69 real pull requests, branch review retained every git-changed file by
  construction at 93.4 percent precision with a median size of 1,026 tokens (`review.json`).
- In the reduced-scope agent-loop run, the Fuse arm's edits passed the project's own
  tests on the first attempt in 89 percent of scored rollouts versus 82 percent for
  native tools, with overlapping confidence intervals. The Fuse arm declared success on a
  failing edit 8 times versus 9 for native. Build and test calls were essentially equal at
  3.1 versus 3.2 (`loop.json`).
- Across four recorded repositories, skeleton reduction removed 38 to 44 percent of
  tokens while keeping every public and protected type name and 96.3 to 99.4 percent of
  method names (`reduce.json`).

The opt-in resident workspace answered repeated `fuse_check` calls in 31.2 ms at the
median on the recorded NodaTime run (`resident-latency.json`). That path is faster than a
full build for speculative checks; it does not replace normal builds or tests before merge.

These measurements have defined limits. Read the
[full methods and results](https://fuse.codes/docs/project/benchmarks) before comparing
tools or applying the figures to another repository.

## Local and Write-Safe

Fuse reads source, compiler state, git metadata, and its local `.fuse/fuse.db` index.
Read, check, impact, refactor, and review operations do not write the working tree.
`fuse_workspace` with `action=apply` is the one explicit tree-write path, and it is a dry
run unless `write=true`.

Compiler-backed wiring analysis is .NET-only. Other languages receive syntax-level search
and reduction.

## Related Code-Intelligence Tools

Repository indexes, code graphs, and language-server tools already exist.
[CodeGraphContext](https://github.com/CodeGraphContext/CodeGraphContext) provides a local
multi-language graph, [Serena](https://github.com/oraios/serena) exposes
language-server-backed symbol tools, and [Sourcegraph](https://sourcegraph.com/code-search)
covers multi-repository search and code intelligence. Fuse concentrates on local .NET work
through MSBuild and Roslyn: framework wiring, reduced scoped context, compiler-backed
proposed-file checks, change impact, and covering-test selection. It can run alongside the
index or search built into a coding client.

The [peer comparison](https://fuse.codes/docs/project/benchmarks#peer-comparison-fuse-versus-codegraph-coa-codesearch-and-serena)
records a bounded, dated experiment and its sampling limits. It is not a general ranking of
code-intelligence tools.

## Start Here

- [Quickstart](https://fuse.codes/docs/start/quickstart)
- [Connect your coding agent](https://fuse.codes/docs/start/connect-your-ai)
- [How Fuse works](https://fuse.codes/docs/concepts/how-fuse-works)
- [Tool reference](https://fuse.codes/docs/reference/mcp-tools)
- [Install options](https://fuse.codes/docs/start/install)
- [Contributing](https://fuse.codes/docs/project/contributing)

Apache 2.0. Copyright (c) 2026 Litenova Solutions. See [LICENSE](LICENSE) and
[NOTICE](NOTICE).
