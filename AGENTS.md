# Fuse - Agent and Contributor Guide

Read this before editing Fuse source or docs. It holds the durable context about the project so a session does not need it repeated in a prompt.

## What Fuse Is

Fuse is a .NET-native codebase context optimizer for AI-assisted development. It collects source files, reduces them for token efficiency, and emits one structured payload an agent or developer can read in a single call instead of opening thousands of files. It ships as a .NET global tool (`fuse`) and as a Model Context Protocol server (`fuse mcp serve`). It cuts tokens while keeping the public API intact, scopes to the files a task needs, and trims the round-trips an agent makes during its explore phase.

## Repository Layout

- `src/Core`: pipeline libraries - `Fuse.Collection`, `Fuse.Reduction`, `Fuse.Emission`, `Fuse.Fusion` (the orchestrator).
- `src/Host`: user-facing surfaces - `Fuse.Cli` (CLI commands and the MCP server).
- `src/Plugins`: `Fuse.Plugins.Abstractions`, `Fuse.Plugins.Languages.CSharp`, `Fuse.Plugins.Languages.CSharp.Roslyn`, `Fuse.Plugins.Formats.Web`.
- `tests/`: unit, golden-output, and integration tests. `tests/benchmarks/`: the harness, corpus manifest, and recorded results.
- `site/`: the documentation website (Next.js + Fumadocs), published at fuse.codes. All prose documentation lives here as MDX under `site/content/docs`.
- `assets/`: the benchmark figure (`fuse-benchmarks.png` and `.svg`) and the chart-generating script. `mcp-registry/`: the MCP Registry server manifest.
- Solution file: `Fuse.slnx`.

## Build, Test, Format

```bash
dotnet build Fuse.slnx -c Release
dotnet test Fuse.slnx -c Release --no-build
dotnet format Fuse.slnx --verify-no-changes
```

Build first, then test with `--no-build`. CI verifies build, test, format, and a self-contained publish smoke test for win-x64 and linux-x64.

## Design Invariants

- C# structural analysis (skeleton, outline, dependency, type-name, route map, semantic markers, symbol slice) uses Roslyn syntax parsing. Regex remains for lexer-based reduction, project-graph parsing, format reducers, pattern detectors, and secret redaction.
- A small local embedding model backs the dense retrieval channel (on by default); it is fetched once and cached on the first index, and all query-time work is offline. When the model is absent the path falls back to the deterministic lexical (BM25F) floor, so retrieval never requires a model at query time.
- The syntax-tier indexer is provider-driven: a `ILanguageSyntaxProvider` (in `Fuse.Semantics`) claims file extensions and extracts symbols and chunks with no compiler, and `SemanticIndexer` selects providers by extension rather than hardwiring one language. C# is the `CSharpSyntaxProvider` (the existing Roslyn-syntax extractor behind the seam, unchanged); a second language registers a provider without changing the shared indexer. Each indexed file carries a `language` tag (the `files.language` column, schema version 14) set from the selecting provider, so retrieval can filter or blend by language over the language-agnostic tables; symbols and nodes inherit their language through their `file_id`. The semantic (typed-graph) tier remains C#/Roslyn-only for now (the semantic-tier provider seam and per-language wiring analyzers are not yet built).
- Persistent cache and index data live in a single SQLite file at `.fuse/fuse.db` (WAL mode).
- JSON uses source-generated `JsonSerializerContext` only (no reflection serialization).

## Change-safety invariants

- Host RPC contract: any change to a `Fuse.Cli.Rpc` DTO or a `[JsonRpcMethod]` signature must bump both `FuseHostService.ProtocolVersion` and `ext/vscode/src/host/protocol.ts` `PROTOCOL_VERSION` in the same change, and update the extension client. The version constant exists to surface a stale-extension or new-host mismatch cleanly; leaving it stale turns a mismatch into an opaque error.
- External-process arguments are bounded. Commands built from a variable-length path or id list (for example the git invocations in `GitStatsProvider` and `GitChangeDetector`) must chunk the list or pass it via stdin. A single concatenated command line overflows the OS limit on large repos and silently degrades to empty results.
- Tests must actually run. When adding a test, confirm the test command discovers and executes it (the count goes up). The extension contract suite is wired through `ext/vscode/package.json` `test:contract`; a test placed where that script does not reach is dead. A green gate that ran zero new tests is not coverage.
- Behavior changes are not silent. A change that alters throw or propagation behavior (for example SQLite flush exhaustion swallowing instead of rethrowing) is a behavior change; call it out, do not fold it into an "add logging" change.

## MCP Tools

Nine tools over the persistent semantic index: `fuse_index` (build or refresh the index), `fuse_map` (workspace map of symbols, routes, and counts), `fuse_localize` (rank candidate files for a task, with the graded signal-sufficiency contract), `fuse_resolve` (resolve wiring: service to implementation, request to handler, route to action, config to options), `fuse_context` (emit scoped, reduced source with provenance for selected seeds), `fuse_review` (diff-first change impact and packed context), `fuse_find` (exact symbol, path, or text lookup), `fuse_neighbors` (the graph neighborhood of a file, the callers and implementers of a symbol, or the central files of an area), and `fuse_reduce` (compact a known set of files or raw content). Read tools build the index on first use. MCP resources cover the map, localize, context, and review workflows.

## Measured Results (source of truth)

All numbers come from `tests/benchmarks/results` (the recorded data) and the benchmarks page at `site/content/docs/project/benchmarks.mdx`, counted with `o200k_base` over a commit-pinned corpus (Scrutor, Ardalis.Specification, NodaTime, the eShopOnWeb application, plus the in-repo SampleShop and OrderingApp fixtures). The suites are reproduced with `fuse eval semantics|review|localize|agent|reduce|performance`; the C# eval driver lives in `tests/benchmarks/Fuse.Benchmarks`. Never fabricate or weaken a number; verify against the results before quoting. This is a snapshot of the current figures; the per-wave evolution lives in the roadmap progress logs under `roadmap/`.

- Semantic resolution (Suite A, `semantics.json`): the extracted graph matches the hand-built edge ground truth exactly on the OrderingApp wiring fixture, 22 of 22 edges, recall and precision 1.0. It covers DI registration and constructor injection, MediatR request-to-handler, ASP.NET route-to-action, options binding, EF Core, Scrutor decoration, factory registration, hosted services, pipeline behaviors, minimal-API and gRPC and SignalR, plus the precision edge cases (an explicit open generic, a TryAdd, and a multiple-implementation ambiguity where only the registered implementation resolves). This is the deterministic moat. A `--corpus-sample` mode samples predicted corpus edges for adjudication.
- Change impact (Suite B, `review.json`, 53 PRs, 25,000-token budget, `--restore`): changed-file recall 100 percent, precision 79.8 percent, F1 0.89, median 958 and mean 1,095 returned tokens. Index modes partial 27, semantic 1, syntax 25 (review restores each PR worktree, so a majority load semantically). A grep baseline reaches 53 percent recall at 14 percent precision. Changed files are seeded as must-keep, so the signal is precision; the semantic blast radius is exercised on the application.
- Open-ended localization (Suite C, `localize.json`, 53 PRs, title only, `--restore`, dense on by default): overall changed-file recall about 15 percent at 8.1 percent precision, median 1,033 returned tokens; by bucket identifier-rich 21 percent, natural-language domain 17 percent. Contract metrics: low-signal detection F1 1.0, false-rejection on answerable queries 0.0 percent, precision when confident 9.3 percent. The dense channel (default on, offline, fetched once and cached) lifts recall over the lexical fallback (13.3 to 15.1 percent overall, 14 to 17 percent on natural-language) and removes false rejections; with no model present the path is byte-identical to the lexical fallback. The weakest mode, bounded by a mostly-syntax corpus (localize main-checkout modes partial 2, syntax 2). The retrieval levers (subword indexing, stemming, a comment field, dependency-centrality and git co-change priors, opt-in graph expansion) operate over the language-agnostic tables.
- Agent context sufficiency (Suite D, `agent.json`, model-dependent, not byte-reproducible): one Claude Code CLI driver (`claude-sonnet-4-6`) over 12 PRs in two arms (native file tools, the Fuse MCP tools), one rollout each. Fuse 30 percent mean file recall at a median 211,502 cumulative tokens; native 26 percent at 209,182; precision 44 versus 43 percent. A small, wide-CI sample, read recall together with tokens.
- Peer comparison (`layer6-peers.json`, 50,000-token budget): fuse 19 percent recall at 19 percent precision (12 PRs), codegraph 9 percent at 11 percent (12 PRs), coa-codesearch 9 percent at 1 percent (4 PRs), serena 34 percent at 27 percent (4 PRs). The deterministic arms (fuse, codegraph) ran all 12 PRs; the model-driven arms (coa, serena) ran a bounded 4-PR sample. Fuse beats CodeGraph on the 12-PR comparison; serena's higher aggregate is dominated by a tiny-repo outlier on its 4-PR sample, and on the substantive PRs Fuse leads or ties it. Token columns are not directly comparable (fuse and codegraph return source; coa and serena return path or snippet lists). No head-to-head ranking is claimed beyond this caveated reading.
- Token reduction and fidelity (Suite E, `reduction.json`, offline): the Roslyn skeleton keeps every public and protected type and 99 to 100 percent of public methods while removing 37 to 55 percent of tokens at skeleton level, and 47 to 60 percent at public-API level. Fidelity is counted by parsing the raw source with Roslyn as independent ground truth, so the measure is not circular.
- Warm latency and cold start (`performance.json`): warm operations are well inside target (NodaTime warm localize 61 ms P50 / 71 ms P95, resolve sub-millisecond, review plan 110 ms P50, single-file incremental re-index 23 ms P50). Cold start serves the syntax tier in about 20 seconds (a syntax-first pass with no MSBuild and no embedding) versus about 70 seconds for the full semantic pass; the syntax-first serve with a background semantic upgrade is opt-in (`FUSE_BG_UPGRADE=1`), and the explicit `fuse index` is always a synchronous full pass. Timings are environment-dependent.
- Honest gaps: the model-driven peer comparison at full scale (50 to 100 PRs) and full task resolution (apply a patch, run a test oracle, score pass@1 across arms over many tasks) are compute-bounded and not run at scale; treat any such claim as illustrative until recorded. Most corpus repositories load syntax or partial in this environment, so the corpus suites sit below the Suite A semantic ceiling.

## Working Conventions

- Branch off `main`; open a PR via `gh` when verified. Do not merge, self-approve, or enable auto-merge; leave merges to reviewers.
- Keep build, test, and format green. New public API without XML docs is incomplete.

## Writing Style (docs and prose)

- Plain ASCII only. No em dashes, no emoji.
- User-facing pages (Start, Scenarios, Concepts) are outcome-first: lead with the result and a runnable example, then explanation. Reference pages stay dense and precise.
- Define a coined term (fusion, skeleton, seed, scoping, manifest, round-trip) in one plain sentence on first use.
- Avoid filler jargon: seamless, robust, ensure, leverage.
- Measured numbers are exact and sourced. Label any illustrative or theory-grounded claim as illustrative; never present it as a benchmark.

## Code Documentation Standard

### Public API: XML (`///`)

Apply to every `public` and `protected` type and member in `src/**/Fuse.*`.

1. Document on the interface or abstract base first; implementations use `<inheritdoc />` unless they add behavior worth noting.
2. Required tags: `<summary>`, `<param>` (every parameter, including `CancellationToken`), `<returns>` (non-void), `<exception cref="...">` (intentionally thrown).
3. Use `<remarks>` for ordering guarantees, side effects, performance, null semantics, or algorithm constraints.
4. Use `<see cref="..."/>` to link related types instead of repeating docs.
5. Style: four-space indent after `///`; property summaries as noun phrases (never "Gets or sets"); `<c>` for literals.

Do not add XML to `private` members.

### Internal complexity: `//` comments

Use regular comments for non-obvious `private` or `internal` logic: heuristics, state machines, regex pipelines, invariants, edge cases. Explain why, not what; skip obvious code. Comment when a reader must hold mental state (depth counters, accumulation, thresholds) to change the code safely.

### Where docs go

| Area | Public XML | Private `//` |
|------|------------|--------------|
| Orchestration (`FusionOrchestrator`, `*Pipeline`) | `<remarks>` on stage ordering and delegation | Stage logic inside private helpers |
| Language plugins | Full docs on capability interfaces; thin impls use `<inheritdoc />` | Regex and scan heuristics |
| Detectors and reducers | Summary plus remarks on false-positive tradeoffs | Non-obvious matching rules |
| Options and DTO records | Summary when the name alone is ambiguous | Rarely needed |

Full contribution workflow: [fuse.codes/docs/project/contributing](https://fuse.codes/docs/project/contributing). Pipeline context: [fuse.codes/docs/internals/pipeline](https://fuse.codes/docs/internals/pipeline). Documentation source lives in `site/content/docs`.
