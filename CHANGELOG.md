# Changelog

All notable changes to Fuse are documented here. The format is based on Keep a Changelog. Fuse 3.0 is a product overhaul; backward compatibility with 2.x output, commands, and the MCP tool surface is not a goal.

## [Unreleased] - 4.0.0

The compiler-oracle release. See `roadmap/v4-plan.md` for the full item breakdown.

### Changed

- **License migrated from MIT to Apache 2.0 (L1).** The project now ships under the Apache License, Version 2.0. Apache 2.0 adds an explicit patent grant and a termination-on-litigation clause, the standard pairing for compiler-adjacent tooling that third parties embed. This is a license-header change only, with no API break. Downstream packagers should update the SPDX expression they record for Fuse from `MIT` to `Apache-2.0`. A `NOTICE` file is added per Apache convention; the runtime-fetched all-MiniLM-L6-v2 embedding model is itself Apache-2.0 and is downloaded on demand, not redistributed.

- **Fixed the lexical field-weight inversion and landed a ranking regression suite (N1).** The FTS5 `bm25` weight vector on the localize hot path weighted the `path` column highest, so a folder-name match outranked a symbol-name match, contradicting the documented intent. The vector is corrected (name and signature and symbols above path; path above comments and body; subtokens below the exact name but above the body; stems lowest). A new `fuse eval ranking` suite scores the ranking with MRR, recall@k, and nDCG@k against changed-file ground truth in three configurations (lexical isolation, shipping default, and default-without-co-change), writes `results/ranking.json`, and is the required gate on any change to field weights, tokenization, query expansion, or priors. `LocalizationRequest` gains `EnableCentralityPrior` and `EnableCoChangePrior` toggles (default true) so the suite can score channels in isolation and re-adjudicate the A6 co-change prior. Behavior change: the classic FTS ranking order shifts because the field weights changed; this is the intended fix.

- **Purged stale-corpus benchmark results and fixed superseded citations (N2, part 1).** Moved the legacy PowerShell-harness result files (`layer1`..`layer5`, `layer-latency`, `layer-ranking`, `baseline.layer1`, and the superseded `localize`/`review` dev-iteration snapshots) plus the legacy `reduction.json` to `tests/benchmarks/results/archive/`. Regenerated `reduce.json` and `performance.json` on the current corpus (Scrutor, Specification, NodaTime, eShopOnWeb). Rewrote the Suite E reduction table in the docs from the retired 5-library corpus to the current corpus, corrected precision-when-confident from the superseded 9.3 percent to the current 5.6 percent (`localize.json`, 9 tasks), corrected the dense-lift pairing to 13.3 to 14.9 percent, and annotated `agent.json` as a directional pre-R4 record (10 of its 12 PRs are retired-corpus). The physical deletion of the in-memory `Bm25RelevanceIndex` (N2, part 2) is deferred: no CLI or MCP surface constructs a classic query-mode `FusionRequest`, so it is a non-shipping code path, and removing it mid-release would destabilize the fusion test suite for no product benefit (the shipping ranker is already unified on the FTS5 index guarded by the N1 suite).

- **Retired the legacy PowerShell benchmark harness and fixed doc/reality drifts (N5).** Deleted the superseded `harness/layer1`..`layer5`, `layer-latency`, `layer-ranking`, `run-all`, `setup-corpus`, `gen-prs`, `check-regressions`, `smoke`, `calibrate-tokenizers`, and `bootstrap-ci` scripts; their metrics now live in the C# `Fuse.Benchmarks` suites and `CorpusManager`. The one documented exception is `harness/layer6-peers.ps1` (external-MCP-server orchestration) with its `common.ps1` helpers. Fixed the `FuseTools` XML summary (said "eight tools", omitting `fuse_neighbors`; now nine), added the `OrderingApp` wiring fixture to `corpus.json` (recorded as fixture-only, the Suite A ground truth), and rewrote the benchmark readme and docs to describe one harness and one command surface (`fuse eval`).

- **The freshness contract: no read tool serves silently stale data (N6).** Before this change the MCP read path indexed only when the store was cold, so after the first edit every read tool answered from an index frozen at first call. Now a warm store is reconciled on open: `SemanticIndexer.ReconcileDirtyFilesAsync` hashes each known file's current on-disk content against the stored hash, re-indexes edited files, and removes deleted files, so a symbol an edit added is found and one an edit removed is gone. A bulk change (many files dirty at once) degrades to a stale-as-of stamp (`stale_dirty_count` in the index metadata) instead of a per-file reconcile storm. The reconcile refreshes a file's own rows; cross-file semantic edges are still recomputed by a full `fuse index` (the resident-workspace semantic increment lands with N3). This also wires the single-file incremental re-index to a real product path (previously it had no product caller).

### Added

- **Loop-metric computation for the task-resolution suite (R4, part 1).** Added `LoopMetrics`, the deterministic, model-free core of the loop-measuring benchmark: from a task-resolution transcript it computes iterations-to-green, build-invocations-per-session (the loop-collapse metric), wall-clock, and whether green was reached. It is unit-tested against scripted transcripts, so it carries the claim between the expensive model-driven runs. The full suite (10-to-15 curated PRs across the native, LSP-armed, and Fuse arms, three rollouts) needs provisioned models and is the remaining R4 work; this lands the measurement definitions the runs will use.

- **DI-resolved test edges complete R5.** A post-merge `TestEdgeExtractor` emits `tests` edges from a test type to the source types it references, resolving injected interfaces to their registered implementations through the `di_resolves_to` edges, so a test injecting `IOrderService` is linked to `OrderService`. It runs after the per-project analyzers merge (so it links across projects) and is foreign-key safe (it links only to node ids that already exist). This is the covering-test substrate M1 selects over, and it gives the `tests` traversal weight its producer, fully resolving finding 7 (both `references` and `tests` now have producers; the dead `calls` weight was removed). Selection is sound-but-best-effort: edges reached only through reflection or a source generator are not seen, and the docs say so rather than claiming completeness.

- **`fuse_impact`: blast radius before an edit (R2, part 1).** A new MCP tool enumerates the callers, implementers, consumers, and referencing types a change to a symbol would touch, served from the persisted semantic graph (R5's reference edges plus the wiring edges), deduped by source. No source bodies. Per the availability contract, the exact signature-change break set (which call sites would no longer bind) is an oracle-grade answer requiring a tier-1 load; until one exists the tool reports it as unavailable rather than guessing, and returns the graph-grade blast radius. The MCP surface is now eleven tools. The tier-1 bind-check is the remaining R2 work.

- **Persisted reference edges (R5, part 1).** The semantic analyzers now emit type-level `references` edges via a new `ReferenceEdgeAnalyzer`: a source type that uses a member or the type of another source type gets a deduped edge to it, resolved through the semantic model so only real bound references count. This is the persisted substrate `fuse_impact` (R2) will read to answer "what references this symbol" without a live `SymbolFinder` pass. Granularity is the declaring type (one edge per referencing/referenced pair) to bound row volume. The index schema version bumps to 15 (a stale index has no reference edges, so it rebuilds). Finding 7 cleanup: `references` now has a producer, and the dead `calls` traversal weight was removed (no analyzer emitted it; R5 uses `references` as the edge name for use edges). The `tests` traversal weight is retained for M1's test-impact selection; its producer is R5 part 2 (test-to-symbol edges with DI resolution), not yet implemented.

- **Build-capture worker runs Fuse's semantic extraction (N4, tier-1, part 3).** The `fuse-build-capture` worker now runs Fuse's semantic extraction (`SemanticSymbolExtractor` plus the wiring analyzers via `SemanticAnalysisRunner`) over each rehydrated compilation, in-process in the worker, and reports the symbol, node, and edge counts. This validates the crux of the tier-1 wiring: Fuse's analyzers run over a build-capture compilation with no MSBuildWorkspace conflict (the worker references Fuse.Semantics but never invokes the MSBuild loader). The remaining tier-1 work is serializing the extracted graph bundle and having the indexer spawn the worker and write that bundle to the store as the tier-1 path.

- **Out-of-process build-capture worker foundation (N4, tier-1, part 2).** A standalone `fuse-build-capture` executable (`Fuse.BuildCaptureWorker`) runs a repository's own build with a binary log and rehydrates exact Roslyn compilations via Basic.CompilerLog.Util, emitting the result as source-generated JSON on stdout. It resolves the tier-1 integration blocker: the library's Roslyn closure conflicts with MSBuildWorkspace inside one process, so the worker is a separate process that never invokes MSBuildWorkspace, and the parent process (which keeps MSBuildWorkspace) never references the library. Validated: the worker rehydrates the SampleShop compilations out of process while the parent's MSBuildWorkspace tests stay green. This is the foundation; the remaining tier-1 work is for the worker to run the wiring analyzers over the rehydrated compilation and serialize the graph, and for the indexer to spawn the worker and write that graph to the store.

- **Supervised background semantic upgrade (N3, part 1).** The `mcp serve` host's syntax-first background upgrade (opt-in via `FUSE_BG_UPGRADE`) is no longer a fire-and-forget `Task.Run` that swallows exceptions and can outlive the host (finding 5). A new `SemanticUpgradeSupervisor` owns the jobs: deduped per root, each run under a cancellation token tied to host shutdown, failures logged to stderr rather than dropped, and shutdown cancels and drains in-flight jobs so none is orphaned. This is the lifecycle prerequisite for the resident workspace; the resident compilation itself and the dependency-scoped incremental semantic reindex are the remaining N3 work.

- **`fuse_signatures`: batch exact-signature lookup (R6, part 1).** A new MCP tool returns the declared signature, kind, accessibility, containing type, and location for a batch of named symbols (simple or fully qualified) from the persistent symbol store in one call, replacing many grep-and-read round-trips. A signature is recorded at the semantic tier; in syntax mode a symbol may have no recorded signature and the tool says so per symbol rather than inventing one, and a requested name with no match is reported. Additive (no shim, no protocol change); the MCP tool surface is now ten tools. The repair-packet half of R6 (fix context attached to `fuse_check` diagnostics) rides on R1 and is not yet implemented.

- **`fuse doctor`: per-project load-tier diagnosis (N4, part 1).** A new command actively loads the workspace through MSBuild and Roslyn and reports the achieved tier (oracle-grade when every project loads clean, graph-grade when some load with compile errors or fail to load, syntax when none load) and the concrete per-project reason for any downgrade (unrestored, SDK mismatch, build error). The loader now records a per-project load report, and a project that loads with compile errors is marked graph-grade rather than counted as oracle-grade. This is the availability-reporting half of N4; the tier-1 build-capture (binlog rehydration) and tier-2 auto-restore mechanisms are not yet implemented, so oracle-grade is reached today only where MSBuildWorkspace already loads every project clean.

- **Developer Certificate of Origin (DCO) sign-off requirement (L2).** Contributions now require a `Signed-off-by:` trailer on every commit (`git commit -s`), certifying the DCO 1.1 statement in the new `DCO.txt`. A DCO CI check (`.github/workflows/dco.yml`) fails any pull request with a commit missing a matching trailer; commits merged before adoption are grandfathered. A pull-request template reminds contributors, and the contributing docs describe the flow. Fuse uses the DCO in place of a Contributor License Agreement.

## [3.2.0] - 2026-07-02

### Changed

- **The VS Code host runs on the V3 semantic index, not the legacy fusion engine.** The `fuse host` RPC surface (`fuse/index`, `fuse/graph`, `fuse/scope`, `fuse/explain`, `fuse/diagnostics`) is rewritten to read the same `WorkspaceIndexStore` and `SemanticRetrievalEngine` the MCP tools use, so the editor and the agent see identical analysis. The legacy `FusionOrchestrator`, `DependencyGraphBuilder`, and explain service are gone from the host. The wire protocol is bumped to version 3 (`FuseHostService.ProtocolVersion` and the extension's `PROTOCOL_VERSION`); an older extension and a newer host surface the mismatch at handshake.
- **The index panel shows the full semantic picture.** `fuse/index` now returns the index tier (semantic, partial, or syntax), symbol and route counts, a per-language file breakdown, full-text-search availability, the schema version, and the Fuse build that wrote the index. The extension's Index panel renders all of it.

### Added

- **Host index summary queries.** New store queries back the panel and the graph without per-file round-trips: route count, per-language counts, file-level dependency edges, and a bulk file token-estimate map.

## [3.1.2] - 2026-07-02

### Added

- **Update awareness for the CLI and MCP server.** A cache-first, throttled (once a day), offline-safe check tells you when a newer Fuse is on NuGet. `fuse mcp serve` prints the notice to stderr and `fuse index` to the console; both read a local cache instantly and refresh it in the background, so nothing waits on the network. Disable with `FUSE_UPDATE_CHECK=0`.
- **Opt-in auto-update between sessions.** Set `FUSE_AUTO_UPDATE=1` and the MCP server, on finding a newer version, launches the detached updater to apply after the session exits, so the next spawn is current. It never stops sibling sessions and never updates mid-session.
- **Scheduled updates** are documented as a one-line Task Scheduler (Windows) or cron (POSIX) entry that runs `fuse update` on a cadence, for fully hands-off upgrades.

## [3.1.1] - 2026-07-01

### Added

- **`fuse update` command.** Updates the global tool with the process-lock choreography a bare `dotnet tool update` cannot do on its own: it stops the running Fuse hosts that hold the file locks (for example the one an editor extension spawns), then hands the update to a small detached script that waits for the current process to exit before replacing the tool files. This works around the fact that a running .NET global tool locks its own files on Windows. Reload the editor window afterward so its MCP client re-handshakes with the new tool surface.
- **Deprecation shims for the retired V2 MCP tool names.** `fuse_toc`, `fuse_skeleton`, `fuse_search`, `fuse_focus`, `fuse_changes`, `fuse_ask`, `fuse_dotnet`, and `fuse_generic` are registered again as thin tools that return an actionable message naming the V3 replacement. A client that cached the old tool surface and was upgraded underneath now gets clear guidance instead of an opaque `Unknown tool` error. They will be removed in the next major.

### Changed

- **The index self-heals on an incompatible upgrade.** The index records the Fuse build that wrote it; when a later run's `major.minor` differs, the index rebuilds automatically before it is read, so a minor upgrade never serves stale extraction. A patch release keeps the contract and does not force a rebuild. `fuse diagnostics` reports both the running version and the version that built the index.

### Changed

- **The product version is owned by the codebase, not the git tag.** `Directory.Build.props` `<Version>` is the single source of truth for every .NET assembly, and the VS Code extension, the MCP registry manifest, and the docs site carry the same number. `build/set-version.ps1` bumps all of them at once and `build/verify-version.ps1` fails CI on any drift, or on a release tag that does not match. This also fixes the index version stamp: `FuseBuildInfo` now reports the real product version (previously the non-CLI assemblies were left at 1.0.0), so the version-drift self-heal and `fuse diagnostics` report correctly.

### Fixed

- **Indexing is defensive against duplicate and foreign content.** The trigger was Claude Code writing full duplicate checkouts under `.claude/worktrees/`, so `fuse_localize` returned ten candidates that were all worktree copies. The fix is layered rather than a single name: (1) discovery prunes any nested version-control root (git worktree, submodule, or embedded clone) anywhere under the workspace, not just `.claude`; (2) for a git repository the file set now comes from git itself (tracked plus untracked-but-not-ignored), so other worktrees and ignored trees are excluded by construction, with the directory walk as the fallback; (3) the excluded-directory set is shared by the scanner and the .NET discoverer, widened to the common tooling, build, and dependency directories, and extensible per repository through a `.fuseignore` file; (4) byte-identical solution copies no longer flip discovery into projects mode; and (5) retrieval collapses byte-identical files, so a query never returns several copies of one source file even if duplication reaches the index.
- **VS Code host connection gave up too early and orphaned the host.** The extension waited only about three seconds for a freshly spawned host to create its named pipe, so a cold .NET host under load (a debugger attached, other extensions busy) surfaced as `connect ENOENT`, and the supervisor left the still-starting host orphaned on the pipe. The connect now waits against a generous deadline, bails out early if the host process exits before it serves, kills the host if it does give up so no orphan squats the pipe, and reports a spawn error. Activation no longer blocks on the connect.
- **VS Code host handshake serialized PascalCase, breaking the extension.** The `fuse host` JSON-RPC endpoint applied the source-generated context as a resolver but not its camelCase naming policy, so the wire emitted `ProtocolVersion` where the extension's `vscode-jsonrpc` client reads `protocolVersion`. Every field deserialized as its default, surfacing as an opaque "Fuse host protocol mismatch: host undefined". The host now sets the camelCase policy on the formatter, and a cross-formatter regression test (a camelCase client, matching the extension) guards it; the prior same-formatter test could not see the casing.

## [3.1.0] - 2026-06-30

Fuse 3.1 sharpens the .NET semantic engine on the modes where 3.0 was weakest: open-ended discovery, language breadth, and first-call latency. Dense retrieval is now on by default and fully offline, the open-ended path refuses and routes instead of guessing on a vague request, and more of the codebase's own vocabulary is searchable without a model. The numbers behind every claim are on the [benchmarks page](https://fuse.codes/docs/project/benchmarks).

### Added

- **Dense retrieval, on by default and offline.** A small local embedding model (all-MiniLM-L6-v2, about 23 MB) is fetched once and cached on the first index; every later run and all query-time work is offline, and the query is embedded as written, never paraphrased. It blends with the lexical and graph signals so a prose task finds a file by meaning. When the model is genuinely absent the path falls back to the deterministic lexical floor. Opt out with `FUSE_DENSE` set to a falsy value.
- **Signal-sufficiency contract on the open-ended path.** `fuse_localize` now grades every request from the candidate score distribution into confident (return the tight set), partial (a small flagged set plus a navigation map), or insufficient (refuse and return only a navigation map and an explicit ask for a symbol, route, service, request, config section, or git base). On a no-signal title Fuse hands back a map instead of guessing. A `strict` option hard-requires an anchor; the default stays best-effort so a one-shot client is never stranded.
- **Offline vocabulary bridges.** Identifier subword indexing (so the prose word `rounding` matches `ApplyRoundingMode`), Porter stemming, and a weighted comment field make the codebase's own vocabulary searchable without a model.
- **Structural ranking signals.** A dependency-centrality prior and a git co-change prior (mined from a bounded git-history window at index time) nudge ambiguous candidates by where a file sits in the graph and which files it historically changes with. Both are capped so they tune rather than dominate.
- **Iterative exploration tool.** `fuse_neighbors` returns the graph neighborhood of a file, the callers and implementers of a symbol, or the structurally central files of an area, ranked, bounded, and body-free.
- **Multi-language breadth.** A provider seam drives the syntax tier; Python and JavaScript/TypeScript are supported at the syntax tier (token-efficient context and search) alongside C#. Each indexed file carries a `language` tag so retrieval can filter or blend by language. The deep typed graph remains .NET-only.
- **Opt-in fast cold start.** In the `mcp serve` host, `FUSE_BG_UPGRADE=1` serves the syntax tier on the first read (no MSBuild wait) and upgrades to the full semantic graph in the background; the first answer drops from about 70 seconds to about 20 seconds on a mid-size repository.

### Changed

- **Open-ended localization is a graded contract, not a fixed candidate list.** Precision when the engine answers is up and false rejections on answerable queries are down; bare-title recall is no longer the headline for this path (the contract metrics are). With a git base, the routed mode remains change-impact review, which is where recall and precision are strongest.
- **Persistent index schema bumped to version 14.** The index rebuilds on the first run after upgrade (it adds the subword, stem, and comment full-text fields and the file language tag). No action is needed; the rebuild is automatic.
- **Benchmark corpus rebuilt and peer comparison expanded.** The corpus now uses repositories that restore and index in semantic or partial mode on the .NET 10 SDK, and the peer comparison runs Fuse against CodeGraph, coa-codesearch, and Serena head-to-head. The benchmarks page and the docs are rewritten as current-state snapshots.

### Fixed

- **Directory walk no longer escapes the collection root through a symlinked or junctioned directory.** A file reached by descending through a reparse-point directory that points outside the root is now excluded, matching the existing handling for file symlinks.

## [3.0.0] - 2026-06-26

Fuse 3.0 turns Fuse from a token-reduction context optimizer into a Roslyn-backed .NET semantic context engine for AI agents. It maintains a warm, persistent semantic index of a workspace and serves precise, provenance-backed context from it: which implementation is injected, which endpoint handles a route, which handler processes a request, which options type binds a config section, and what a git diff semantically impacts. Token reduction is now a rendering and transport feature, not the product.

### Added

- **Persistent semantic index.** A single SQLite database at `.fuse/fuse.db` (WAL, schema version 10) holds files, projects, symbols, chunks, a typed semantic graph (DI registrations and injection, MediatR request-to-handler, ASP.NET route-to-action, options binding and consumption, interface implementation), routes, and an FTS5 full-text index. The workspace loads through MSBuild and Roslyn with a syntax-only fallback; re-indexing is incremental per changed file.
- **New CLI commands.** `fuse index`, `map`, `localize`, `resolve`, `context`, `review`, `find`, `diagnostics`, plus `reduce`, `models`, `init`, `mcp` (install/serve), and `host`.
- **New MCP surface.** Eight tools (`fuse_index`, `fuse_map`, `fuse_localize`, `fuse_resolve`, `fuse_context`, `fuse_review`, `fuse_find`, `fuse_reduce`) and four resources (`fuse://map`, `fuse://localize`, `fuse://context`, `fuse://review`) over the persistent index, with session-delta elision on `context`/`review`.
- **C# benchmark suite.** `tests/benchmarks/Fuse.Benchmarks`, run with `fuse eval semantics|review|localize|agent`, replaces the per-suite PowerShell harness. Recorded results in `tests/benchmarks/results`: semantic resolution 100 percent edge recall and precision on the wiring fixture; `fuse review` 100 percent changed-file recall at 89.6 percent precision and a median 874 tokens over 48 PRs; open-ended localization 27 percent recall from a title alone (the weak floor); and a model-dependent agent suite (sonnet-4-6) showing the Fuse MCP arm gathering a change's files at a median 135K tokens versus 212K for bare tools.

### Changed

- **Retrieval is graph-based, not lexical.** Candidate generation (exact resolution, FTS, path, changed files) feeds typed graph expansion with per-edge weights and pruning, replacing the in-memory BM25F query-scoping engine.
- **Reduction is a render tier.** The context renderer reduces each planned file to full source, reduced, skeleton, or public-API per the plan; reduction is no longer the product claim.
- **Docs rewritten.** The documentation site is rewritten to the warm-semantic-index model; the benchmark figure and quoted numbers are regenerated from the four suites.

### Removed

- The query-scoping and focus engine and its `FUSE_*` tuning levers, the eleven-tool MCP surface (`fuse_toc`, `fuse_skeleton`, `fuse_focus`, `fuse_search`, `fuse_changes`, `fuse_ask`, `fuse_dotnet`, `fuse_generic`, `fuse_explain`), and the `fuse dotnet`, `fuse wiki`, `fuse ask`, `fuse explain`, and `fuse verify` commands. The `spike-*.ps1` benchmark scripts tied to the removed engine are deleted.

The entries below this point predate 3.0 and describe development of the now-removed 2.x query-scoping engine; they are retained as history.

## [Unreleased]

### Changed

- **Scalar admission tuning swept and defaults kept (item 5).** With B2 (the 90-PR corpus) and B5 (the
  dev/test split) both in place, the four ranking scalars the plan names are now sweepable without a rebuild:
  `HopDecay` and the PRF `ExpansionWeight` join `CentralityWeight` and `--query-top` with `FUSE_HOP_DECAY` and
  `FUSE_EXPANSION_WEIGHT` environment overrides, threaded through the query pipeline and recorded in the run
  report so a measurement names its configuration. `spike-scalar-tuning.ps1` sweeps each scalar one-at-a-time
  (plus an admit-more combo), tunes on the dev fold, and reads the held-out test fold with a per-repo
  no-regression check. Result over the 90-PR corpus: no arm clears the baseline on test (the dev-best arm,
  `cw=0.0`, drops test 45 to 44 percent and regresses AutoMapper 28 to 23), so the shipped defaults are kept,
  confirming the plan's overfit-risk prediction. The full table is in `tests/benchmarks/results/opt-in-levers.md`.
  The knobs remain available so a deployment with a different corpus can retune and validate the same way.
- **Reading-set ground-truth support in Layer 2A.** The scoping benchmark now scores recall against an optional
  per-PR `reading_set` (the files a task must read but not edit, such as interfaces, callers, and tests) unioned
  with the editing set, instead of only the files the PR changed, when a PR is labeled. Absent a label the
  truth is the editing set, so this support does not by itself move any published number. The labels
  themselves are deliberate curation that would shift the numbers, so they are deferred to a dedicated curation
  pass rather than fabricated alongside the B2 larger-corpus rebaseline; this lands the harness support that
  honors them once they exist.
- **Parallel query-path document indexing (Q1).** The query scoping pipeline now reads each file, derives its
  declared symbols, and extracts its comment field in parallel rather than in a sequential loop, since the files
  are independent and the analysis index and content provider already tolerate the concurrent access the
  dependency-graph build performs. The per-file results are folded into the index and its process-lifetime cache
  signature in the original file order, so the built index and its cache key are byte-identical to the
  sequential build: a latency optimization, not a behavior change. All scoping unit tests are unchanged and the
  B9 per-repo recall gate passes (no per-repo regression at the 50k budget).

### Added

- **Opt-in rules-based query rewrite (item 12, the no-model half).** A query-path rewrite that emphasizes the
  compound PascalCase identifiers in a query (the strongest signal that the query names a type) via weighted-term
  ranking, fully lexical with no model. Enable with `FUSE_QUERY_REWRITE=1`. A measured A/B
  (`spike-query-rewrite.ps1`) was neutral at the 50,000 token headline (61 percent on and off, a 2-point
  FluentValidation dip at 25,000), so it is off by default; see the research note. The LLM-backed rewrite stays
  a separate opt-in and never touches the no-model path. Covered by `HeuristicQueryRewriterTests`.
- **Platform VSIX release pipeline with a bundled host.** `ext/vscode/scripts/package-platform.mjs` publishes
  the self-contained host for a runtime identifier, stages it under `host/<rid>/` (which the extension prefers
  over the PATH fallback), and runs `vsce package --target` to produce a platform-specific VSIX; the
  `ext-release.yml` workflow runs it across all six OS/arch pairs and uploads the VSIXes. Verified locally for
  win32-x64: a 62.65 MB VSIX with the host bundled (the ~141 MB host compresses well). The base no-host VSIX
  (about 230 KB, host resolved from PATH) remains for a lightweight install. This completes the extension's
  packaging phase.
- **Per-RID host publish profiles and CI matrix.** Publish profiles now cover all six runtime identifiers
  (win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64); the CI runtime job publishes each on its
  matching OS, smoke-testing the native-arch builds and publish-only for the cross-arch ones. This produces the
  per-platform self-contained hosts the VS Code extension will bundle. (win-arm64 cross-publish verified.)
- **Host engine-data RPC methods and a read-only secret-span API (extension Phase 1, continued).** The host now
  serves `fuse/index` (warms the shared engine, returns state and file count), `fuse/scope` (runs a
  focus/search/changes fusion through the shared orchestrator, returns the emitted file plan with token costs,
  and writes the payload to a temp file the extension opens read-only), `fuse/graph` (projects the dependency
  graph with PageRank centrality and an estimated token cost, at file or directory level of detail with
  directory supernodes), and `fuse/diagnostics` (secret findings with precise zero-based editor ranges). The
  last adds `ISecretRedactor.FindSecretSpans`, a read-only method that locates detected secrets and their
  character spans in the original content without redacting; it mirrors the redaction match logic on the
  original content (the same approach the existing code-literal classification uses), so it is purely additive
  and the redaction output and counts are byte-identical (all redaction tests unchanged). Each host method is
  integration-tested over the real engine; the wire DTOs are mirrored in `protocol.ts` and pinned by contract
  tests.
- **Live workspace-change push (`fuse/invalidated`) and the warm-index watcher lifecycle.** The host now runs a
  debounced file watcher on the served root and pushes a `fuse/invalidated` notification to every connected
  editor when files change (server-to-client push through a `HostNotifier` connection registry that registers
  on attach and drops on disconnect). The extension subscribes through its supervisor and re-projects the index,
  diagnostics, hotspots, and graph on receipt; a host restart re-establishes the subscription. Covered by a host
  broadcast test.
- **Scoped role overlay on `fuse/graph`.** The graph method takes an optional scope (mode, seed, query, since)
  and tags each node with the role the context plan assigned that file (Seed, Changed, Dependency), so the
  extension's webview recolors by role to show exactly what a fusion would include. The scope routing is now a
  shared `ApplyScopeMode` helper used by scope, explain, and the graph overlay. Covered by a host test.
- **Token-hotspot and graph-gap diagnostics on `fuse/diagnostics`, and a scope-payload concurrency fix.**
  `fuse/diagnostics` now also returns the most token-expensive files (hotspots) and the files the dependency
  graph leaves unconnected (no inbound or outbound type reference, often dead or reflection-only code), which
  the extension surfaces as informational and hint diagnostics alongside the secret warnings. The host
  concurrency test surfaced a real bug: `fuse/scope` wrote its payload to a file named only by root and mode,
  so two concurrent same-mode scopes collided on one file; payload file names are now unique per call. Both
  fixes are covered by host tests. `fuse/diagnostics` also flags generated code (EF Core migrations and model
  snapshots, via the engine's `GeneratedCodeCollapser.IsGenerated`) as editor hints.
- **`fuse/explain` and a read-only `FusionResult.Plan` projection.** The host now serves `fuse/explain`, which
  returns the scoped result's context plan (each planned file's role, reduction tier, and relevance score)
  without writing a payload, so the extension can show why a file is included and at what fidelity before
  fetching. This surfaces the previously `internal` context plan as a public, additive `FusionResult.Plan`
  (a list of `PlannedFileInfo`), projected once by the orchestrator at the result return; it defaults to empty
  for unscoped runs, so it changes no existing output, recall, or token number (the full suite and golden
  tests are unchanged). All eight Phase 1 host RPC methods now work end to end with 16 host tests.
- **`fuse host`: a JSON-RPC endpoint for the VS Code extension (extension Phase 1).** A new long-lived host
  command serves the warm engine to the editor over a named pipe (Windows) or Unix domain socket (elsewhere),
  sharing the same `AddFuse` dependency graph as `fuse mcp serve`, so the agent (over MCP) and the developer
  (over this transport) read one warm engine. The endpoint address is derived from the repository root, so a
  second editor window reuses the running host and two repositories stay isolated; the accept loop serves
  multiple connections until a client calls `fuse/shutdown`. Wire JSON goes through a source-generated
  `FuseHostJsonContext` (no reflection), mirrored by `ext/vscode/src/host/protocol.ts` and pinned by a contract
  test; the lifecycle methods (`fuse/handshake` version match, `fuse/stats` process health, `fuse/shutdown`) are
  verified end to end over an in-memory duplex pipe. The engine-data methods (`fuse/index`, `fuse/graph`,
  `fuse/scope`, `fuse/explain`, `fuse/diagnostics`) have DTOs and protocol entries in place and are the next
  increment. The extension build is tracked under `ext/vscode` (DECISIONS, PROGRESS). Transitive of this work:
  the VS Threading analyzers (suppressed repo-wide; they flagged unrelated synchronous console writes) and a
  transitive MessagePack advisory (suppressed; the MessagePack formatter is never used, only System.Text.Json).

## [3.0.0] - 2026-06-25

Fuse 3.0 is a retrieval, packing, and benchmarking release built on the IMPROVEMENT-OPPORTUNITIES playbook: a first-class `ContextPlan`, budget-aware expansion and tiered emission, downgrade-before-drop packing, auto-mode MCP routing with session-delta diff overlays, a measured opt-in rerank stack (bi-encoder, cross-encoder, project graph, churn, member-level), and a far more honest benchmark suite (per-repo CI gate, bootstrap intervals, ranking metrics, latency layer, cost-adjusted recall). Default-path scoping stays lexical and offline by construction; every opt-in lever degrades gracefully to that floor. Breaking changes are intentional and listed below.

### Deferred (blocked on external runtime, a full rebaseline, or unmet plan gates)

These plan items are not implemented this release. Every item that was implementable here was built and
measured rather than predicted: Q5 (member-level retrieval, found a real win, shipped opt-in), B12 (title
versus title-plus-body, measured via the GitHub API), and item 23 (rerank embedding cache) all moved out of
this list once attempted. The items below remain because each is blocked on a runtime absent from this
environment, is a full benchmark rebaseline that the never-weaken-a-number invariant says to do deliberately,
or has an unmet plan gate. They are recorded so the accounting is complete and auditable.

- **B2 (larger, cleaner corpus):** explored end to end and found to be a full rebaseline, not an additive
  append. The data work is done and staged: `tests/benchmarks/corpus-candidates/serilog.json` holds a fifth
  repository (serilog/serilog, pinned) with six git-verified PRs and changed-file ground truth, ready to wire
  in. Running layer 2A over the resulting 30-PR corpus moved every headline mean (query 61 to 64, focus 92 to
  90, changes 87 to 89, grep 38 to 37 percent), as expected. The blocker is not the data but the benchmarks
  page: its Findings section states roughly fifteen per-feature A/B deltas (for example "query 57 to 61",
  "focus 71 to 77") each measured over the pinned 24-PR corpus at the time that feature landed, so a 30-PR
  corpus invalidates all of them at once. Publishing the new headline tables without re-measuring those deltas
  would leave the page's prose contradicting its tables, which weakens published numbers and violates the
  "never fabricate or weaken a benchmark number" invariant. The honest rebaseline therefore re-runs every layer
  (1, 2A, 2B, 4) and re-measures every per-feature spike over the new corpus, then rewrites the Findings prose,
  AGENTS.md headline, and the B9 baseline atomically. That is dedicated work; the staged file removes the
  curation cost so it can start from verified ground truth. Several items below wait on it.
- **Item 5 (scalar admission tuning):** the plan gates it on B2 plus the held-out split (B5), to tune on dev
  and publish on test. Tuning the 24-PR corpus now would overfit; blocked on B2.
- **B1 (task-success eval with round-trips):** needs a programmatic agent harness driving the arms and scoring
  patches, which is not built. Blocked on that runner.
- **Item 10 (learned-sparse SPLADE):** an XL opt-in retrieval rewrite the plan marks "as warranted by data";
  warranted only after dense rerank (item 9) pays off, which it did not here. (Item 11, its sibling, was built
  and measured this release; see Added and the research note.)
- **Item 12 (LLM query rewrite / HyDE):** an LLM at query time is opt-in only, since the default path must run
  with no model and no network; deferred with the other opt-in model levers.
- **F1 (SQLite FTS5 BM25 backend):** an architectural swap the plan says to do "only if profiling demands it";
  the latency layer shows the current postings path is not the bottleneck, so it is not warranted.

### Changed

- **First-class `ContextPlan` for scoped results (architecture enabler A1).** A scoped result now produces an
  explicit `ContextPlan` of `PlannedFile` records, each carrying the file's `Role` (Seed, Dependency, or
  Changed), its reduction `Tier`, relevance score, provenance, query-selected members, and a `MustKeep` flag.
  This replaces the former implicit scheme where seed-versus-neighbour was inferred downstream from the length
  of a file's provenance chain and the per-file reduction tier was chosen by a separate ad-hoc resolver. The
  plan is built once after scoping by `ContextPlanBuilder` and drives the per-file reduction tier (a non-seed
  file reduces to a signature skeleton when tiered emission is active; seeds keep the request's level), making
  role and tier a single explicit source of truth that packing and emission can read instead of re-deriving.
  The refactor is behavior-preserving: the plan's tier mapping reproduces the previous resolver exactly, so the
  full test suite and a fresh layer 2A run reproduce the published recall and token numbers with no change. This
  completes the Phase 1 architecture enablers (A1 through A4).
- **Extract `QueryScopingPipeline` from the orchestrator (architecture enabler A2).** The query scoping path
  (index build, candidate ranking, pseudo-relevance feedback, multi-query fusion, the distributional thesaurus,
  member-level retrieval, the git churn prior, dense rerank, seed promotion, member selection, and graph
  expansion) moved out of `FusionOrchestrator` into a dedicated `QueryScopingPipeline`, so the query path is a
  testable unit rather than inline orchestrator code. The orchestrator now builds the shared dependency graph
  and proximity-edge adjacency (also used by the focus and changes paths) and hands them to the pipeline; both
  are deterministic and independent of ranking, so building them before the pipeline is behavior-identical. The
  refactor is behavior-preserving: the full test suite and a fresh layer 2A run reproduce the published query
  numbers exactly (no change to selection, recall, or token cost). The nested `FilteredFileSet` and
  `SymbolSliceRequest` records are now internal top-level types so the pipeline can return them. This completes
  enabler A2 alongside A1 (`ContextPlan`, above), A3 (`TokenCostModel`), and A4 (candidate/seed/emit split).
- **Cache rerank document embeddings by content hash (item 23).** The dense reranker (item 9, opt-in) embedded
  every candidate's text on every query; it now caches each document embedding by a content hash, so a warm
  rerank over an unchanged file reuses the vector instead of re-running the model. The cache is process-lifetime
  and concurrency-safe, bounded by clearing past a cap (about a few tens of MB), and behavior-preserving (the
  same text embeds to the same vector). This is the in-memory form of the plan's persistent vector cache; it
  benefits the opt-in rerank path only (the query exact text is not cached, since it changes per call). It does
  not change the default path, which runs no model.
- **Reuse the built relevance index across queries on an unchanged tree (item 24).** The BM25 index rebuilt its
  document-frequency and length statistics on every query; this is the dominant warm-call cost once body
  tokenization is cached on disk. A process-lifetime `RelevanceIndexCache` now keeps one built index keyed by a
  content signature of the indexed files (path plus content of every file), so a repeated scoped query against
  an unchanged tree reuses the index instead of rebuilding it. A built index is read-only (ranking only reads
  its tables), so the cached instance is shared safely across the MCP server's concurrent queries; the build
  runs outside the lock, so a first-time build never serializes unrelated queries. Behavior-preserving: the
  index is a pure function of the document content the signature covers, so scoping results are identical (the
  full suite and the per-repo gate confirm it). The payoff is in the long-lived MCP server, where one process
  serves many queries; the per-process latency layer spawns a fresh process per sample and so does not capture
  this in-process reuse. Covered by cache unit tests (same signature builds once and returns the same instance,
  a different signature rebuilds, the single entry evicts).
- **Downgrade-before-drop packing (P1), on by default.** When the reduced set exceeds the token budget, the
  lower-relevance tail the packer would otherwise drop is now replaced with a compact structural sketch (type
  and member names, no bodies, from the Roslyn outline) instead of being cut, so a would-be-dropped file stays
  present as a navigable outline. It runs as a redaction-correct post-reduction rewrite (C1) on the query and
  focus paths under a token budget, ordering by relevance and sketching only the tail beyond the budget; it
  only adds sketched files and never displaces a fuller one, so it cannot regress recall. Measured (Layer 2A,
  regenerated): focus rose from 77 to 92 percent at the headline budget (Newtonsoft.Json focus 28 to 74,
  FluentValidation 88 to 100, the focus medium and large change-set strata 62 to 94 and 24 to 60), and query
  rose at the tight budgets (10,000 token 39 to 46, 25,000 50 to 54) with the 50,000 headline unchanged at 61
  percent. No per-repository regression at any budget; change mode is excluded (its recall rests on full
  bodies). It also lifted Layer 2B single-turn localization for query from 42 to 75 percent (9 of 12),
  overtaking the grep baseline (58 percent), because an answer file the 20,000 token budget used to drop now
  surfaces as a sketch. This is the largest focus lift of the release, targeting the multi-file-truncation
  failure mode directly: the wide focus neighbourhood the budget used to truncate now survives as sketches. Set
  `FUSE_DOWNGRADE_DROP=0` to reproduce the drop-only packer. Covered by orchestrator tests (a would-be-dropped
  tail file is emitted as a sketch when on, dropped when off) and the env-override test.

- **Budget-aware query-path dependency expansion (item 4).** When a token ceiling is set, query-path
  expansion now admits neighbours highest-score first only while their estimated reduced cost fits the budget,
  instead of admitting the whole neighbourhood and leaving the packer to cut it. The per-file estimate comes
  from the `TokenCostModel` at the level each file will be emitted at (a skeleton for a neighbour when tiered
  emission is on, the request level otherwise), so a cheap skeletonized neighbour is not rejected as a full
  body; seeds are always admitted. Stopping the graph from over-admitting a large neighbourhood keeps the
  budget on the seeds and their closest neighbours, so more truth files survive the cut: Layer 2A query recall
  rose at every budget (50,000 tokens 57 to 61 percent, 25,000 45 to 49, 10,000 33 to 39), with precision up
  (50k 3 to 8 percent, wasted tokens 40,886 to 37,118) at fewer mean tokens (41,885 to 40,258). No
  per-repository regression at the headline budget (AutoMapper 46 to 50, FluentValidation 57 and MediatR 94
  unchanged, Newtonsoft.Json 32 to 42), and every change-set-size stratum improved (small 68 to 71, medium 56
  to 61, large 7 to 10). The held-out test fold moved 51 to 48 percent, a two-PR swing on a ten-PR fold; the
  change fits no scalar to the corpus, so this is fold noise rather than overfitting. On by default; set
  `FUSE_BUDGET_EXPANSION=0` (or `off`/`false`) to reproduce the unbounded expansion. Covered by orchestrator
  tests (a tight budget keeps the seed and admits fewer files; an ample budget is neutral) and an
  environment-override test.

- **Graph-centrality prior uses PageRank instead of raw in-degree (item 7/Q7).** The query-independent
  importance prior blended into seed and expansion scores is now PageRank over the file dependency edges
  (importance flows from a file to the files it depends on), so a type referenced by many already-central files
  inherits more weight than a count of distinct referrers gives it. Computed once per run by a few power
  iterations over the already-built graph, so it adds no file reads. Measured recall is unchanged at every
  budget on the pinned corpus (query 57 percent, focus 77 percent, no per-repo change), as expected: the prior
  enters at the small default centrality weight (0.15), so this is a more principled prior and the foundation
  for personalized-PageRank-from-seeds (the higher-value follow-on), not a recall lever on its own. layer4 is
  recall-neutral and was not re-run.
- **MCP tool descriptions and server instructions steer mode selection (item 13, routing portion).** The server
  instruction block now leads with a "choosing a mode" guide (branch/PR/fix work with a git base to
  `fuse_changes`, a named type to `fuse_focus`, a concept to `fuse_search`, a broad survey to `fuse_toc`, unsure
  to `fuse_ask`), and the `fuse_changes` description and reference page state that it has by far the highest
  recall (87 percent versus 55 percent for query at the same budget) so PR-shaped work should route there. This
  biases clients (especially Cursor and Copilot, which lean on descriptions) toward the highest-recall mode
  without changing any behavior. The `fuse_focus` and `fuse_search` descriptions also now cross-reference each
  other (focus when you know the type, search when you do not) and `fuse_changes` for branch or PR work.

### Fixed

- **Dependency graph no longer treats method calls as type references (item 6, false-edge fix).** The Roslyn
  dependency extractor took every PascalCase identifier in expression position as a type reference, so a bare
  call `Configure()` created a false edge to any file declaring a type named `Configure`, and a generic method
  call `mapper.Map<OrderDto>(...)` created one to a type named `Map`. It now excludes the invoked expression of
  a call (`Foo()`, `Foo<T>()`) and a generic method name on a receiver (`x.Map<T>()`), while still capturing
  real references: generic types, generic arguments, base types, return types, and constructions. The cleaner
  graph improved focus recall at the tight 10,000 token budget (54 to 56 percent) with no per-repo regression
  at the headline budget; it mainly helps method-call-heavy code. Covered by tests for the false-edge cases and
  the retained real references.
- **Structural maps prepend to the first output part (P2).** Route and project maps are an overview header for
  the whole output. On multipart disk output they were prepended to the last part, where they trailed the
  content; they now head the first part (single-part and in-memory output were already correct). Covered by a
  multipart test asserting the map heads part one and appears exactly once.
- **Whole-PEM-block redaction and additional provider key patterns (C6).** The PEM rule matched only the
  `-----BEGIN ... PRIVATE KEY-----` header line, so the base64 key body and the `END` line were left in the
  output: the secret survived. It now matches the entire block, header through matching footer, and removes it
  as one unit (RSA, EC, DSA, OPENSSH, ENCRYPTED, PGP, and plain variants). Added redaction patterns for GitHub
  tokens (`ghp_`/`gho_`/`ghu_`/`ghs_`/`ghr_` and fine-grained `github_pat_`), Google API keys (`AIza...`),
  Slack tokens (`xox[baprs]-...`), and Stripe live keys (`sk_live_`/`rk_live_`). These matter more now that
  skeleton and slice output is redaction-correct (C1) and goes straight to an agent.
- **Collision-free symbol identity for member operations (C5).** `SymbolChunk` exposed only a `QualifiedName`
  (`ParentType.SymbolName`), which collides for overloads, nested types that share a simple parent name, the
  same member name across namespaces, and partial-class members. Operations keyed on it could conflate
  distinct members: body deduplication, for instance, could collapse a sibling overload's unique body when
  another overload was a duplicate, because both shared the same display name. Each chunk now carries a
  collision-free `StableId` (namespace, full containing-type chain with generic arity, member name, and for
  methods and constructors the generic arity and parameter type list), and member selection, thin-skeleton
  assembly, and body deduplication key on it. `QualifiedName` is retained for display (provenance, markers).
- **SQLite pending-write race in the cache flush (C4).** `SqliteKeyValueStore.FlushAsync` snapshotted the
  buffered writes, committed them, then removed the flushed keys by key alone. A `Set` on a snapshot key while
  the commit was in flight was therefore dropped: the just-flushed (older) value's key was removed, discarding
  the newer pending value. Removal is now by value (the `KeyValuePair` overload), so a concurrent update with a
  different array reference stays pending for the next flush instead of being lost. Covered by a test that
  overlaps a hot-key writer with repeated flushes over a large batch. (The related per-call store pooling, where
  the serve path opens a fresh store per call and so does not share the pending buffer, is folded into item 24's
  repo-scoped index cache rather than fixed in isolation, since the two share a lifecycle.)
- **Concurrency hazards under the MCP server (C3).** The orchestrator is a singleton and the MCP server can run
  tool calls concurrently against different repositories, which exposed three shared-state races, now fixed.
  (1) `GitIgnoreFilter` held a mutable pattern set updated per run via `SetPatterns`; concurrent collection runs
  could apply one repository's ignore rules to another. The filter is now immutable, and the collection pipeline
  builds a fresh per-run instance carrying that run's patterns. (2) Pattern detectors accumulate mutable state
  during a detection pass but were registered as singletons and shared across runs; they are now transient and
  the post-reduction pipeline resolves a fresh detector batch per run through a factory. (3) The collection
  pipeline no longer mutates any shared filter, so being captured by the singleton orchestrator is safe. Covered
  by concurrency tests that interleave runs with differing `.gitignore` rules and with the pattern summary on.
- **Strict total-token accounting for `--max-tokens` / `maxTokens` (C2).** The token budget is now a hard cap
  on the complete payload, not just the file bodies. Two issues are fixed. First, emission charged an entry to
  the budget and only then checked the limit, so the one entry that crossed `MaxTokens` was still written;
  emission now rejects and stops before writing the over-budget entry (the single most-relevant entry is still
  emitted unconditionally so a scoped run never returns nothing). Second, the manifest, route and project maps,
  redaction report, pattern summary, and the session, header, and review preambles were appended after packing
  and never charged, so a scoped payload could overrun the requested budget by the size of its framing. The
  packer now reserves room for that framing (measured against the packed file set in a tight two-pass scheme),
  so the full payload an MCP client receives fits the budget. Verified by tests asserting the complete
  in-memory payload, including the manifest, stays within `MaxTokens`.
- **Secret redaction now covers post-reduction source rewrites (C1).** The thin-skeleton path (query scoping,
  which keeps the query-matched members verbatim) and the symbol-slice path (a `Type.Member` focus seed)
  rebuild a file's content from raw source after the reduction stage has already run, and secret redaction
  runs inside the reduction stage. A secret in a kept member body, field initializer, attribute argument, or
  const literal therefore bypassed the redactor and could reach an agent in clear text. Both rewrites now
  re-run the redactor on the assembled content before emission, and the reported per-kind redaction counts
  describe what was actually emitted. The invariant (any content rebuilt from source after reduction must
  pass the redactor) is enforced in code and covered by tests on both paths.

### Added

- **Opt-in fielded comments on the query path (Q2).** Comments and documentation comments can be indexed as
  their own weighted BM25F field (above the body, below symbols and path) so a query term documented in a
  file's comments contributes a deliberate signal rather than being diluted in the body. Fully lexical, no
  model; extraction is a single regex pass. Enable with `FUSE_FIELDED_COMMENTS=1`. A measured A/B
  (`spike-fielded-comments.ps1`) was neutral at the 50,000 token headline (61 percent on and off; FluentValidation
  57 to 59, a 2-point dip at the 10,000 token budget, others flat), so it is off by default; see the research
  note. The comment field is inert unless comment text is supplied, so the default ranking is byte-identical.
- **Opt-in member-level retrieval (Q5).** A query-path pass that indexes each declared member as its own
  document and rolls the per-member scores up to a file score (best member wins), then adds any file the member
  pass surfaces that the file-granular pass missed as an extra seed. This reaches a file whose match is
  concentrated in one member of an otherwise large file, which whole-file length normalization dilutes.
  Measured both arms: it lifts query recall at the 50,000 token headline from 61 to 68 percent (AutoMapper 50
  to 62, FluentValidation 57 to 69, Newtonsoft.Json 42 to 46, MediatR unchanged), with no per-repository
  regression at the headline, the biggest single query lever this release. It is **off by default** because the
  warm-call latency cost is steep: indexing member chunks re-parses files every query (the chunk extraction is
  not yet cached), which roughly doubles to triples warm latency (Newtonsoft.Json warm p50 2178 to 5752 ms,
  AutoMapper 1333 to 3173). Enable with `FUSE_MEMBER_LEVEL=1`; default-on awaits caching chunk extraction by
  content hash (the follow-on that would remove the per-query re-parse). Covered by orchestrator tests (a
  file whose match is diluted across a large file is surfaced when on, not when off) and an environment test.
  My earlier prediction that Q5 would be neutral was wrong; building and measuring it found a real win, which
  is why it is shipped (opt-in) rather than deferred.
- **Opt-in cross-encoder reranker (item 11), selectable with `FUSE_RERANK_MODEL=cross`.** A second `IReranker`
  built on the ms-marco-MiniLM-L-6-v2 cross-encoder, which scores each query-document pair jointly (the query
  attends directly to a candidate's text) instead of comparing two independently pooled embeddings like the
  bi-encoder (item 9). It reuses the ONNX plugin's tokenizer and download/cache machinery; `fuse models
  download --model cross` fetches it (SHA-256 pinned, about 23 MB), and `FUSE_RERANK=1` with
  `FUSE_RERANK_MODEL=cross` selects it. Both rerank arms always fall back to the lexical floor when the model
  is absent or offline, preserving the no-model invariant. Measured both arms on the hard repos (AutoMapper,
  Newtonsoft.Json) at the 50,000 token headline: cross-encoder 33 percent against the lexical floor at 46 and
  the bi-encoder at 44, so it did not clear the plan's ~60 percent bar and is **not** a default. The cause is
  structural, not a wiring bug (the scores select real changed files): a cross-encoder runs once per candidate
  over the pair truncated to the model's context window, so at file granularity the relevant member deep in a
  large file falls outside the window. It ships opt-in and documented because it is the per-pair-accurate
  reranker family and may help on other corpora or once member-level chunks (Q5) feed it shorter inputs.
  Covered by reranker fallback tests and model-locator/downloader tests; see the research note.
- **Opt-in coarse project-reference graph expansion (item 8), `FUSE_PROJECT_GRAPH`.** A `ProjectGraphEdgeBuilder`
  parses `.csproj` `ProjectReference` elements (regex, no MSBuild on the hot path) into a coarse inter-project
  graph and links each candidate to the files in the projects its owning project references or is referenced by,
  bounded per file. These cross-project edges feed the same decayed expansion channel as the item 7 proximity
  edges, so a seed can reach a related file across an assembly boundary (for example a changed library file and
  an integration test that does not name its type) that the intra-project type-reference graph misses. Measured
  rather than predicted (I had expected it to repeat item 7's neutral result): an A/B at the 50,000 token
  headline lifted query recall 61 to 62 percent overall, concentrated on FluentValidation (57 to 61), with no
  per-repository regression. Off by default because the lift is within the query confidence interval and it adds
  a `.csproj` disk scan per query; enable with `FUSE_PROJECT_GRAPH=1`. Covered by `ProjectGraphEdgeBuilder` unit
  tests and the `spike-projectgraph.ps1` harness; see the research note for the open multi-assembly re-test.
- **Session-delta diff overlays for changed files (item 14).** When a file already sent earlier in a session
  is requested again after it changed, the session path now re-sends a unified diff of the change rather than
  the whole file, so a long multi-turn MCP session spends tokens only on what moved. Unchanged files are still
  omitted, as before. The session tracker now retains each emitted file's content (capped at 64 KB per file;
  larger files keep only their hash and fall back to a whole-file resend), and `ISessionTracker.Claim` reports
  new, changed, or unchanged plus the prior content. The diff is computed over already-reduced, already-redacted
  content, so it reintroduces no secret (C1) and needs no re-redaction; a near-total rewrite, an evicted prior,
  or a file above the diff line cap falls back to the whole file, and the session note tells the agent which
  files came as diffs so it can re-request full content or reset the session. Covered by a unified-diff
  generator with its own tests (insert, delete, context, multi-hunk, whole-file fallbacks), tracker tests
  (new/changed/unchanged, oversized-prior fallback), and an orchestrator test asserting a multi-line change is
  sent as a diff.
- **Opt-in deterministic file sketches for over-large files (item 16).** A file still very large after
  reduction (over roughly 6,000 tokens) can be replaced with a compact structural sketch (its declared types
  and member names, no bodies, from the Roslyn outline), so it keeps presence and navigation in the output
  instead of consuming the budget several smaller files need, or being dropped. The sketch is a post-reduction
  rewrite routed through the redactor (C1), so it cannot reintroduce a secret. Off by default
  (`FUSE_SKETCH_HUGE=1` to enable) and leaves the default output unchanged: the corpus failure mode is
  multi-file truncation rather than single-giant-file pack-outs, so this is opt-in for the codebases where one
  huge file dominates. Covered by builder unit tests (outline rendering, member cap, empty outline) and an
  orchestrator test (a huge file emits as an outline with no body when on, full body when off).
- **Opt-in git churn ranking prior on the query path (Q6).** A new `FUSE_GIT_CHURN_WEIGHT` multiplies each
  candidate's score by `1 + weight * normalized recent commit count`, so a recently and frequently changed file
  ranks a little higher; work clusters where code recently changed. It reuses `IGitStatsProvider`, widens the
  candidate pool (like dense rerank) so the prior can promote a file the lexical pass ranked just outside the
  seeds, and is **off by default** (`weight 0`). It is a production-routing lever, not a benchmark lever, and is
  honest about why: the pinned corpus checks out historical PR-head commits, so churn-from-now is uniformly
  empty (the multiplier is a no-op), and a commit-date-relative churn would leak because the changed files are
  the most recently changed by construction. A measured A/B at the headline budget (`spike-churn.ps1`, both
  arms) is therefore not a real test of the prior: query recall moved 61 to 60 percent overall (FluentValidation
  57 to 53, the other repos flat), and that 1 point is the candidate-pool widening perturbing the
  pseudo-relevance pass on an empty churn signal, not churn doing anything. So the prior cannot be validated by
  this benchmark and stays off by default; its mechanism is covered by unit tests with a stub stats provider
  (a high-churn candidate is promoted to the seed when on; off breaks the lexical tie by path).
- **Opt-in dense rerank of the query candidate pool (item 9), plus `fuse models` and the `Fuse.Plugins.Rerank.Onnx`
  assembly.** An optional `IReranker` reorders the BM25 candidate pool by blending the lexical score with a
  dense query-to-document similarity from an in-process all-MiniLM-L6-v2 ONNX embedder (no Python, no HTTP at
  query time), so the reranker chooses the seeds from a candidate pool widened to several times the seed count.
  It is wired end to end but stays **off by default**: enable with `FUSE_RERANK=1` after fetching the model
  with `fuse models download` (about 23 MB to `~/.fuse/models`, pinned by SHA-256). With no model, offline, or
  the flag unset, the query path runs on the lexical BM25F floor unchanged, so the no-model, no-network guarantee
  holds. `fuse models status|download|remove` manages the cache.

  **It is not the MCP default, because it did not clear the bar.** The plan's policy is to default dense rerank
  on only if it reaches about 60 percent on the hard repos; a measured A/B (`spike-rerank.ps1`, both arms) shows
  it does not and regresses the headline: query recall at 50,000 tokens 61 to 57 percent overall (AutoMapper 50
  to 42, Newtonsoft.Json 42 to 38, FluentValidation 57 to 55, MediatR unchanged). It helps only AutoMapper at
  the tight 10,000 token budget (25 to 42 percent) and is mixed elsewhere. The general-purpose sentence embedder
  promotes plausible-but-wrong files over lexically exact ones once the budget is generous; a code-trained
  embedder is the path to clearing the bar, and the `IReranker` seam plus the candidate/seed split (A4) are now
  in place for it. The lexical path remains the published default and headline (61 percent at 50k). Covered by
  orchestrator wiring tests (a stub reranker selects the seed when on; off and unavailable keep the lexical
  order) and plugin tests (model-absent is a no-op, the integrity check, the cache locator).
- **`fuse_explain` MCP tool: preview a scoped selection before fetching it (item 33).** A read-only tool that
  reports which files a focus, query, or change-scoped fusion would include and which it would exclude, with a
  per-file token estimate, without returning any file bodies. It mirrors the existing `fuse explain` CLI command
  and shares the same scoping path, so an agent can check the effect of a seed, query, or change range and its
  token cost before spending tokens on the real context. The three scoping parameters stay mutually exclusive;
  with none set it previews the whole collected set. Covered by a registration test and a focus-scope test
  asserting the preview names included and excluded files but emits no bodies.
- **`fuse_find` MCP tool: a cheap Fuse-native exact lookup (item 33).** A read-only tool with three modes:
  `symbol` finds a declared type or member by its exact simple name (using the same Roslyn outline that powers
  the table of contents, so a member hit is reported as `member {name} in {Type}`), `text` finds an exact
  substring and shows context lines around each match, and `path` finds files whose path contains the query. It
  gives an agent one coherent interface for exact lookups in place of a broad grep, returning locations rather
  than fused context; `maxMatches` caps the output and summarizes the remainder as a count. Together with
  `fuse_explain` this completes item 33 and brings the MCP surface to eleven tools. Covered by a registration
  test and per-mode functional tests (symbol type and member, text with context, path fragment, and the
  empty-query and no-match paths).
- **Title-only versus title-plus-body query measurement (B12).** `tests/benchmarks/harness/spike-b12.ps1`
  fetches each PR's description from the GitHub API (into the spike, not the transcript) and compares query
  recall from the title alone against the title plus body at the headline budget. Over the 22 non-merge PRs the
  body lifts mean recall from 57 to 66 percent, but the signal is not clean: 4 of the 22 bodies name a changed
  file outright (a direct answer leak), and on some PRs the body's off-topic prose hurts (Newtonsoft.Json
  #1153 drops 16 to 0 as added terms pull in wrong files). So a richer query helps on average, but the gain
  here conflates answer leakage with legitimately richer vocabulary, and the terse title remains the honest,
  leak-free benchmark query. A clean richer-query test needs task descriptions written before the fix (issue
  text), which is a corpus-construction concern for B2, not the merged PR body.
- **Bootstrap confidence intervals for the scoping recall (B6).** `tests/benchmarks/harness/bootstrap-ci.ps1`
  reads the per-PR recall already in `layer2a.json` (no fusions re-run) and reports a deterministic, seeded
  2,000-sample 95 percent bootstrap interval for each mode's mean recall at the headline budget: changes 87
  percent (72 to 98), focus 92 (84 to 98), query 61 (42 to 78), grep 38 (22 to 56). With 24 PRs the intervals
  are wide and query's overlaps grep's, so the benchmarks page now states plainly that a few-point move is
  within noise and a delta is trustworthy only when it also holds per repository and across budgets (the B9
  gate). This keeps the headline numbers honest about sampling uncertainty.
- **Per-repo regression gate for the scoping benchmark (B9).** `tests/benchmarks/harness/check-regressions.ps1`
  recomputes per-repo per-mode mean recall at the 50,000 token headline budget from a fresh `layer2a.json` and
  compares it against a committed baseline (`layer2a-baseline.json`), exiting non-zero if any repository's
  recall drops below the baseline minus a tolerance. This enforces the standing invariant "no per-repo
  regression at the 50k budget" mechanically instead of by eye, across all three scoping modes and four
  repositories. A deliberate, measured improvement updates the baseline in the same commit; the gate never
  relaxes silently to hide a regression. The baseline records the current numbers (changes, focus 92 percent,
  query) after the budget-aware expansion and downgrade-before-drop work landed.
- **Routed arms in the Layer 4 context-acquisition benchmark.** The one-call scenario harness now measures
  `fuse --changed-since <base>` (the routed default when a Git base is available) and `fuse ask` alongside the
  existing `fuse --query` arm, and reports a routed-arms table plus a tokens-to-target-recall metric (the
  smallest budget whose mean recall clears 80 percent). This makes the headline the routed arm the plan calls
  for: the change-scoped arm reaches 88 percent recall at 26,825 mean tokens (and clears 80 percent recall at
  the 25,000 token budget, 14,631 mean tokens), against Repomix's 511,574 tokens at the same one call, so with
  a Git base the story is "the task's files at a fraction of Repomix tokens" rather than fewer tokens at lower
  recall. The `fuse ask` arm (57 percent) tracks the query floor because Fuse routes most PR titles to search;
  it is the routing convenience, change scoping is the recall win. The `fuse --query` arm (61 percent, 39,947
  tokens) stays as the labeled stress floor (a sentence, no base). The two-call `fuse-guided` arm
  (`fuse_toc` then `fuse_search`) is left as a follow-on.
- **Held-out dev/test split in the scoping benchmark (B5).** Layer 2A now assigns each PR to a dev or test fold
  by a fixed hash of its PR id (parity), so every repository contributes to both folds, and reports mean recall
  per mode per fold. This is the methodology gate the plan requires before any scalar tuning (item 5): tune on
  dev, publish test, so a tuning gain is never measured on the data it was fit to. With the 24-PR corpus it is
  scaffolding that pays off once the corpus grows (B2); on the current split, query recall is 62 percent dev /
  51 percent test at the headline budget.
- **Separate candidate, seed, and emit counts for query scoping (architecture enabler A4).** `QueryOptions`
  now distinguishes `CandidateTopK` (the BM25 pool that pseudo-relevance feedback and member selection operate
  on, which a reranking stage would reorder) from `SeedTopK` (the top candidates promoted to expansion seeds);
  the packer already decides the emitted set. Both default to `TopFiles`. This caps the expansion seed set at
  `TopFiles` even after a pseudo-relevance-feedback merge, which previously could union past `TopFiles` and seed
  every merged file; capping it is a small, honest improvement on the corpus (query recall at the headline
  budget steady at 57 percent with no per-repo regression, 25k 44 to 45 percent, precision up and mean tokens
  down, because the over-seeding at the tail is trimmed). The split also gives a future learned reranker a pool
  wider than the seed set without widening what is expanded from. Tested: `SeedTopK` limits the seed set, and a
  wider `CandidateTopK` does not change the seed count on the lexical path.
- **`TokenCostModel` for unified pre-reduce estimate and post-reduce count (architecture enabler A3).** A new
  `ITokenCostModel` (default `DefaultTokenCostModel`) gives a cheap per-file token estimate at a reduction level
  before any file is reduced, and the exact count once content exists, so scoping and packing can reason about
  one consistent notion of token cost. The estimate applies a per-level retention factor (calibrated from the
  Layer 1 benchmark ratios: roughly 0.92 at default/standard, 0.70 aggressive, 0.15 skeleton for C#; 0.95 for
  non-C#) to the raw count. It is the foundation budget-aware expansion needs (a per-file estimate before
  reduction); not yet wired into the hot path, so behavior and benchmarks are unchanged. Unit-tested for the
  retention profile and the skeleton-costs-far-less direction.
- **Identifier-aware tokenization for relevance ranking (item 3).** The shared relevance tokenizer now splits
  identifiers on digit boundaries and around all-caps acronym runs in addition to camelCase and snake_case, so
  `OAuth2Token` yields `auth`, `2`, `token`, `HTTPClientFactory` yields `http`, `client`, `factory`, and
  `base64Url` yields `base`, `64`, `url`, while the whole token is still kept for exact matches. The same
  normalization applies to the index and the query, so a query that uses only part of a compound name now
  bridges to it. Measured over the pinned corpus this lifted query recall at the 50,000 token headline budget
  from 55 to 57 percent, concentrated on AutoMapper (38 to 46 percent), with no per-repository regression at
  the headline budget (the tight 10,000 token budget dips two points as the extra sub-words shift ranking).
- **`fuse ask` CLI command (item 31).** The deterministic task routing that the `fuse_ask` MCP tool uses
  (skeleton for a broad question, focus for a single named type, search otherwise, with a focus-to-search
  fallback when the type does not resolve) is now a CLI command: `fuse ask --task "..." --max-tokens N
  --directory ...`. It exposes the routed agent surface to the benchmark harness and CI without an MCP client,
  the enabler for a routed Layer 3/4 arm. No model is called; the routing is heuristic and reuses the existing
  focus, search, and skeleton paths, so retrieval behavior and benchmarks are unchanged.
- **Agentic next-best-action breadcrumb for tiered emission (item 30).** When tiered emission skeletonizes a
  dependency-expanded neighbour, the output now ends with a machine-readable `<!-- fuse:next ... -->` comment
  that names each skeletonized file and the `fuse_focus "Type"` call that expands its body, so the budget wall
  is a navigable next step instead of a silent loss. The breadcrumb is deterministic, lists at most twenty files
  (the rest summarized as a count), and is charged against the token budget (reserved before packing) so the
  strict-accounting guarantee still holds. It pays off on the interactive round-trip metric rather than one-shot
  recall; its only measured one-shot effect was focus recall at the tight 25,000 token budget (71 to 69 percent),
  with the 50,000 token headline figures and the per-repo table unchanged.
- **Tiered emission for query and focus scoping (on by default).** Dependency-expanded neighbour files
  (provenance hop two or deeper) are now reduced to signature skeletons instead of full bodies, so each costs
  fewer tokens and the budget-aware packer fits more files under the same budget; because recall counts file
  presence, fitting more truth files raises recall. Seeds keep the request's level, and change mode is excluded
  (its recall rests on emitting the changed files in full). Built on the per-file reduction-level mechanism, so
  the skeletonization happens inside the reduction stage and is redaction-correct, not a post-reduction source
  re-read. Measured over the pinned corpus at the 50,000 token headline budget, tiered emission lifted query
  recall from 51 to 55 percent and focus from 71 to 77 percent at fewer tokens (focus 46,543 to 41,505, query
  46,366 to 43,190), with no per-repository regression: focus rose on AutoMapper (88 to 92), FluentValidation
  (74 to 88), and Newtonsoft.Json (21 to 28), query on AutoMapper (29 to 38), FluentValidation (51 to 57), and
  Newtonsoft.Json (30 to 32). The gain concentrates on the medium and large change-set strata where the budget
  previously truncated truth files. Layer 4 one-call context rose to 55 percent recall at 43,165 tokens (from 53
  percent at 44,694). Single-turn localization (layer 2B) is unchanged at slightly fewer tokens. Set
  `FUSE_TIERED_EMISSION=0` to reproduce the untiered ordering. The resolved setting is recorded in the run report.
- **Per-file reduction levels in the reduction pipeline (tiered-emission enabler).** `ContentReductionPipeline.ReduceAsync`
  accepts an optional per-file level selector, so one run can reduce different files at different tiers in a
  single pass (for example seeds kept full while neighbours are skeletonized) instead of re-reading source after
  reduction. The per-file level folds into the reduction cache key, so tiered and untiered runs do not share
  entries, and redaction still runs in the reduction stage so a skeletonized neighbour is redaction-correct
  (unlike the orchestrator-level rewrites C1 had to patch). The selector is the mechanism tiered emission will
  drive; the default path passes none, so current behavior and benchmarks are unchanged.
- **Ranking-quality benchmark layer (B3 / B4).** A new `layer-ranking.ps1` scores the ranked seed-plus-expansion
  list at a budget large enough that packing never truncates, so the metrics isolate retrieval quality from
  packing. It reports recall@k for k in {1,3,5,10,20}, mean reciprocal rank, and nDCG@10 per mode, and is wired
  into `run-all.ps1`. Paired with layer 2A's recall@budget (B4), it separates a ranking loss (the truth file is
  not ranked high) from a packing loss (it ranks high but the budget drops it). On the pinned corpus: focus
  reaches recall@5 48 percent at MRR 0.98, query recall@5 39 percent at MRR 0.55, so focus seeds its first hit
  almost immediately while query's first hit is deeper, and both leave headroom that ranking work (not packing)
  must close.
- **Latency benchmark layer (B13).** A new `layer-latency.ps1` measures the end-to-end wall clock of a scoped
  query call per corpus repo, cold (no reduction cache, no persistent index) versus warm (both, after a warmup),
  reporting p50/p95 over repeated samples plus peak working set. It is wired into `run-all.ps1`. This is the
  latency an agent waits on; absolute times are machine-dependent, so the committed figures are a reference and
  the warm-versus-cold ratio is the portable signal (warm runs land at roughly 0.3 to 0.7 of cold on the pinned
  corpus, consistent with the persistent index amortizing the Roslyn parse). Per-stage timing (for example
  reduction time) is a follow-on once the pipeline surfaces it.
- **Layer 2A benchmark diagnostics: wasted tokens, change-set-size strata, and cost-adjusted recall.** The
  scoping benchmark now reports, alongside recall@budget, the budget spent on emitted files outside the truth
  set (B8, a proportional estimate), recall broken out by change-set size (B10: small 1-3, medium 4-9, large
  10-plus, where the token budget truncates large change sets the mean hides), and a cost-adjusted recall equal
  to mean recall times mean precision (B11, which punishes buying recall with a wide low-precision set). These
  are reporting-only additions over the existing per-PR measurements; recall, precision, and token figures are
  unchanged. The large stratum makes the budget wall explicit: at the 50k headline budget, changes recall is 97
  percent on small change sets but 12 percent on large ones.
- **Adversarial-case reporting (B7).** Layer 2A now tags merge-noise titles (which carry no task vocabulary, so
  the query falls back to the changed type names) and reports query recall three ways at the headline budget:
  all PRs, adversarial-only, and excluding adversarial. This keeps the adversarial cases from silently inflating
  the headline: the two merge-noise PRs recall 100 percent via the type-name fallback, so the all-PRs mean is 61
  percent while the honest clean-title recall (excluding them) is 57 percent over 22 PRs. Reporting-only; recall
  computation is unchanged.
- **Typed experimental options recorded in the run report.** The experimental scoring knobs (graph-centrality
  weight, pseudo-relevance feedback query expansion) are now a typed `ExperimentalOptions` record carried on
  `FusionRequest` rather than ambient process state read deep in the orchestrator. `FUSE_CENTRALITY_WEIGHT` and
  `FUSE_QUERY_EXPANSION` are still honored, but only as an override applied when the orchestrator resolves the
  request's configured values, and the environment is consulted at exactly one point. The resolved knobs are
  written into the machine-readable run report (`--report`) under an `experimental` object, so a committed
  measurement names the configuration that produced it instead of depending on invisible environment state.
  Defaults are unchanged (centrality weight 0.15, query expansion on), so scoping behavior and benchmark
  numbers are identical.
- **Pseudo-relevance feedback query expansion (on by default, fast path).** Query scoping now runs a second
  BM25F ranking pass seeded with recurring declared-symbol terms harvested from the first pass's top files,
  so a sparse natural-language query (a PR title, a task sentence) is rewritten in the codebase's own
  vocabulary before files are selected. The pass is entirely lexical: no model inference and no network, so
  the default scoping path stays fast. Several guards keep it from the classic feedback failure mode of
  broadening or drifting away from a weak first pass: candidate terms come only from the high-signal
  declared-symbol field; a term must recur across at least two feedback files; a term must clear a corpus
  inverse-document-frequency floor, so boilerplate names shared across most files are dropped; expansion
  terms are blended in at a low weight (0.2) relative to the original query, so they nudge ranking toward
  co-occurring concepts without displacing incidental first-pass hits when a query's title is poorly aligned
  with its change; and the expanded ranking is merged with the first pass rather than replacing it, so a
  first-pass seed is never dropped. Tunable through `QueryExpansionOptions`; set `FUSE_QUERY_EXPANSION=0` to
  disable and reproduce the single-pass BM25F ordering exactly.

### Research notes

- **Source:** larger corpus rebaseline (B2): expand the benchmark from 24 PRs over four repositories to 90 PRs
  over five (adding Serilog, 18 PRs per repository via `gen-prs.ps1`, all commit-pinned and git-derived).
  **Fit:** a larger, less favorable corpus to test whether the 24-PR headline holds. **Decision:** **executed
  and published as the new headline.** Layer 1 (now five repositories, plus a Serilog reduction row; the
  published Layer 1 skeleton table and prose were also resynced to the Roslyn extractor that `layer1.md`
  already reflected, where benchmarks.mdx still carried stale regex-era skeleton numbers), Layer 2A,
  Layer 4 (with the live Repomix arm over `npx`), and the bootstrap intervals were regenerated end to end over
  the 90-PR corpus, and every coupled surface (benchmarks.mdx, AGENTS.md, README.md, the user docs, the B9
  baseline) was resynced in one pass so no published number is left at a 24-PR value. The 50,000-token headline
  means moved to changes 89 percent (from 87) at 54 percent precision, focus 78 (from 92), query 48 (from 61),
  grep 34 (from 38); per repo, query was AutoMapper 35, FluentValidation 42, MediatR 83, Newtonsoft.Json 26,
  Serilog 55, and the adversarial-excluded query mean was 45 percent over 81 PRs. Layer 4 over 90 PRs: blind
  whole-repo about 409,000 tokens, Repomix about 425,000, one scoped `--query` call about 37,000 tokens at 49
  percent recall, and the routed change-scoped arm 91 percent at about 29,500 tokens. The honest reading: the
  original 24-PR sample was favorable for query and focus, while change scoping is robust (holds at about 89
  percent) and remains the strong default when a git base exists. The per-feature A/B lift figures recorded in
  earlier release notes and in `opt-in-levers.md` were measured over the original 24-PR corpus and are kept as
  the historical record of why each lever was adopted, explicitly labeled as such; the headline tables are the
  current 90-PR measurement. The B9 per-repo gate (`layer2a-baseline.json`) was updated to the new per-repo
  cells, so a future regression below them still fails the gate.
- **Source:** rules-based query rewrite (item 12): emphasize the compound PascalCase identifiers in a query so
  a query that names a type leans toward the file that declares it. **Fit:** the prose-to-identifier gap on the
  query path, the no-model half of item 12. **Decision:** **built and measured, kept opt-in.** A query-mode
  A/B over the 24-PR corpus (`spike-query-rewrite.ps1`) was neutral at the 50,000 token headline (61 percent on
  and off; Newtonsoft +1, a 2-point FluentValidation dip at 25,000), so it does not clear the bar to change the
  default. The cause is structural on this corpus: the PR titles are already identifier-rich, and BM25F weights
  the declared-symbol field 5x, so re-emphasizing the query's identifiers is largely redundant with the field
  boost. It ships opt-in (`FUSE_QUERY_REWRITE=1`) because a deployment with genuinely prose queries is the case
  it targets; the LLM-backed rewrite (the other half of item 12) stays a separate opt-in and off the no-model
  path. The open follow-up is to re-test against a prose-query benchmark rather than identifier-like PR titles.
- **Source:** fielded comments (Q2): index comments and doc-comments as their own weighted BM25F field so
  prose queries match their natural-language vocabulary. **Fit:** the vocabulary wall on prose titles.
  **Decision:** **built and measured, kept opt-in.** A query-mode A/B over the 24-PR corpus
  (`spike-fielded-comments.ps1`) was neutral at the 50,000 token headline (61 percent on and off), with a
  small gain on FluentValidation (57 to 59) offset by a 2-point dip at the 10,000 token budget and no overall
  movement, so it does not clear the bar to change the default. The cause is structural on this corpus: the PR
  titles are mostly identifier and feature phrases that already match through the symbol and body fields, and
  the comment field overlaps the body, so promoting comments rarely surfaces a file the body did not already
  rank. It ships opt-in (`FUSE_FIELDED_COMMENTS=1`) because a deployment with genuinely prose queries (natural
  -language task descriptions rather than titles) is exactly the case it targets, and the lever is free of any
  model or network. The open follow-up is to re-test it against a prose-query benchmark (B12 title-plus-body,
  or a future natural-language task corpus) rather than identifier-like PR titles.
- **Source:** cross-encoder rerank (item 11): rerank the BM25 candidate pool with a model that scores each
  query-document pair jointly (ms-marco-MiniLM-L-6-v2), the more accurate reranker family than the item 9
  bi-encoder. **Fit:** clear the ~60 percent bar on the hard repos that the bi-encoder missed. **Decision:**
  **built and measured, kept opt-in, not defaulted.** A three-arm A/B (`spike-rerank-cross.ps1`) on AutoMapper
  and Newtonsoft.Json at the 50,000 token headline scored cross-encoder 33 percent against the lexical floor at
  46 and the bi-encoder at 44, so it regressed rather than clearing the bar. The cause is structural: a
  cross-encoder runs once per candidate over the pair truncated to the model's 512-token window, so at file
  granularity the relevant member deep in a large file is outside the window the model sees (the scores still
  select real changed files, so it is not a wiring fault). This corroborates the item 9 finding that the rerank
  family does not beat lexical BM25F on this corpus at file granularity. The code ships opt-in
  (`FUSE_RERANK_MODEL=cross`) because it is a genuine alternative reranker and the natural next experiment is to
  feed it the shorter member-level chunks (Q5) rather than whole truncated files; that re-test is the open
  follow-up, and the bar to default it remains ~60 percent on the hard repos with no per-repo 50k regression.
- **Source:** reference graph beyond syntax (item 8): a cheap first cut parsing `.csproj`
  `ProjectReference` for a coarse inter-project assembly graph, then a later Roslyn `Compilation` tier with
  metadata references. **Fit:** cross-assembly recall on hard repos. **Decision:** **built and measured, kept
  opt-in.** I had predicted this would repeat item 7's neutral result; measuring it proved that wrong. A coarse
  project-reference edge builder (`ProjectGraphEdgeBuilder`) links each candidate to the files in the projects
  its owning `.csproj` references or is referenced by, bounded per file and fed into the same decayed expansion
  channel as the item 7 proximity edges. An A/B at the 50,000 token headline (`spike-projectgraph.ps1`, query
  mode) lifted overall query recall 61 to 62 percent, concentrated on FluentValidation (57 to 61), with no
  per-repository regression (AutoMapper, MediatR, Newtonsoft.Json unchanged): a small but clean win, where the
  reference edge reaches a changed test or sibling across the library/test project boundary that the title's
  lexical match missed. It ships opt-in (`FUSE_PROJECT_GRAPH`) rather than default because the lift is within
  the wide query confidence interval (it holds per-repo but is one point on the mean), it adds a `.csproj` disk
  scan per query, and the corpus is mostly two-project so the cross-assembly case is lightly exercised; the
  deeper compiled-`Compilation` tier remains XL and gated behind the persistent index. The honest next step is
  to re-measure it on a multi-assembly corpus (B2), where the staged Serilog repo already adds a fifth project
  set.
- **Source:** structural proximity graph edges (item 7): link a file to its test or implementation counterpart
  and same-stem siblings by path, followed at a weight below a real type reference, to reach a related file the
  type-reference graph misses. **Fit:** focus and query recall, all graph modes. **Decision:** built behind the
  `ProximityEdges` knob (env `FUSE_PROXIMITY`) with a tested path-based `ProximityEdgeBuilder` (base-stem
  grouping with a generic-stem cap) and expander support, then **kept off by default** after a full layer2a
  A/B showed it exactly neutral at every budget and for every repository (focus 72/88/92, query 46/54/61
  identical on and off; only mean tokens shifted by a few as a sibling within budget was pulled in). The
  existing reverse-edge dependents and type references already reach the test and sibling files that share a
  type, and focus is near its ceiling (92 percent), so the proximity edges are redundant on this corpus rather
  than additive. The builder, knob, and tests are retained (off by default, default output unchanged) for a
  repository where the type graph is sparser; the edge-kind weighting half of item 7 (weighting a base-type or
  constructor-parameter reference above an incidental one) is the more promising remaining part and is left as
  a follow-on. `FUSE_PROXIMITY=1` reproduces the A/B via layer2a.
- **Source:** local distributional thesaurus (Q4): mine identifier co-occurrence (PMI between declared symbols
  in the same file) and expand a query term with its top statistically-associated identifiers, to bridge to a
  related vocabulary the pseudo-relevance feedback set never contained, fully lexically. **Fit:** Layer 2A
  query recall, default path, no model. **Decision:** built behind the `DistributionalThesaurus` knob (env
  `FUSE_THESAURUS`) with a tested in-memory PMI helper, then **kept off by default** after a measured A/B
  regressed the headline rather than helping: query recall at 50,000 tokens 61 to 57 percent, driven by
  AutoMapper 50 to 33 (10,000 and 25,000 token budgets also flat-to-negative overall). Corpus-global PMI
  associates a query term with broadly co-occurring generic identifiers (in AutoMapper, names like `Map`,
  `Source`, `Destination`), and merging those into the seed set injects off-target files that displace truth
  files under the budget; the existing top-documents pseudo-relevance feedback is better targeted because it
  conditions on the query's own first-pass results. The helper, knob, and tests are retained (off by default,
  default output unchanged) for a future higher-PMI-gated or seed-restricted variant; do not enable naively.
  `tests/benchmarks/harness/spike-thesaurus.ps1` reproduces the A/B.
- **Source:** exact symbol/path boosts (Q3): when a query token is an identifier, boost files that declare that
  exact symbol above files that merely mention the words. **Fit:** Layer 2A query precision and recall. **Decision:**
  **spiked and not built.** A gated exact-declared-symbol boost (multiply a candidate's score by 1.5 when its
  declared types or members contain a compound identifier the query names exactly) was implemented and A/B'd
  with `tests/benchmarks/harness/spike-exact-symbol.ps1`: recall was identical to three decimals at every budget
  and for every repository (10k/25k/50k overall 39/50/61 percent on and off). The shipped fielded BM25F symbol
  field (5x weight) plus the identifier-aware tokenizer (which keeps the whole identifier token, item 3) already
  rank the declaring file at the top of the candidate pool, so a further multiplicative boost does not change the
  seed or emitted set. The PR-title benchmark also does not exercise bare type-name queries (a query that IS a
  symbol), Q3's strongest case, so the corpus cannot confirm value there either. Reverted rather than ship a
  dead-neutral code path and flag; the spike helper is retained. Revisit only if a symbol-name-query benchmark
  (an agent calling `fuse_search "OrderService"`) is added, where an exact-match prior is theory-grounded but
  unmeasured here.
- **Source:** change-anchored hybrid retrieval (item 32): when a Git base exists, seed BM25 with the changed
  files at a boosted prior, then expand along the graph, to keep change mode's recall while pulling in the
  unchanged interfaces, callers, and tests a diff never shows, and to lift change mode's modest precision.
  **Fit:** Layer 2A/4 recall and precision when a base is available. **Decision:** **spiked and not built.** A
  throwaway spike (`tests/benchmarks/harness/spike-hybrid-change-query.ps1`) measured the union of the
  change-only and query-only emitted sets per PR, the ceiling any change+query merge could reach. The union
  recall sat at 88 percent against pure change's 88 percent (a +1 point ceiling overall), and 0 points on the
  small and medium change-set strata where change mode already captures every truth file (small 97, medium
  100). Only the three large-change PRs showed headroom (change 17 to union 24 percent), and even that ceiling
  stays poor in absolute terms; a real merge would land below it. A naive union also lowers precision (more
  files), the opposite of the precision goal. So the recall headroom does not justify the multi-surface
  complexity (relaxing the changes/query mutual exclusion across the validator, orchestrator dispatch, CLI, and
  MCP). Revisit only with a precision-targeted merge evaluated on a larger large-change corpus (B2), not as a
  recall lever. The spike helper is retained.
- **Source:** Reciprocal Rank Fusion (Cormack et al., SIGIR 2009) and multi-query fusion in code search. **Idea:**
  rank several query variants (the raw query, an identifier-only subset of the compound type tokens, and the
  pseudo-relevance-expanded query) and combine them with RRF so a file several variants agree on outranks one
  variant's lone top hit. **Fit:** Layer 2A query recall on the fast lexical path; "can subsume the single-pass
  plus PRF merge." **Decision:** implemented as a tested `Scoping/RankFusion.cs` utility wired behind the
  `MultiQueryFusion` knob (env `FUSE_QUERY_FUSION`), then **rejected as the default** after a measured A/B over
  the pinned corpus regressed query recall at every budget: 50k 57 to 53 percent, 25k 44 to 33, 10k 33 to 19,
  with AutoMapper 46 to 33 and Newtonsoft.Json 32 to 26 at the headline. RRF is rank-only, so it discards the
  calibrated BM25F score magnitude that separates a strong declared-symbol hit from an incidental mention, and
  the identifier-only variant injects off-target files at equal fusion weight; the existing seed-preserving PRF
  merge, which never drops a raw seed, is strictly better here. The utility and gated wiring are retained (off
  by default, behavior unchanged) for a future score-aware or seed-restricted variant; do not enable naively.
- **Source:** RM3 / pseudo-relevance feedback (classical IR; recent survey "Query Expansion in the Age of
  Pre-trained and Large Language Models", arXiv 2509.07794, and "A Systematic Study of Pseudo-Relevance
  Feedback with LLMs", arXiv 2603.11008). **Idea:** mine top first-pass documents for expansion terms and
  re-rank. **Fit:** maps to the roadmap's "query expansion from the symbol index" item and Layer 2A/4 query
  recall; stays on the fast lexical path. **Decision:** adopt, constrained to the declared-symbol field with a
  multi-document-frequency and corpus-IDF gate, blended at a low weight and merged with (not replacing) the
  first pass. The literature's headline caveat (PRF degrades recall when the first pass is weak) reproduced in
  A/B spikes over the pinned corpus: an aggressive symbol-field PRF lifted FluentValidation and Newtonsoft.Json
  but regressed AutoMapper, entirely on one PR whose title ("Handling lower case") is disconnected from its
  change (Licensing files), where expansion correctly sharpened toward casing and dropped an incidental hit.
  **Rejected** a seed-overlap drift guard: measuring overlap on the disagreeing PRs showed the harmful case
  (overlap 0.50) sits between helpful cases (0.30 and 0.90), so no overlap threshold separates them, and a
  guard at 0.5 rejected a beneficial low-overlap expansion while keeping the harmful one. **Adopted** instead
  a low expansion weight (swept per PR: 0.2 preserves the off-topic AutoMapper hit while keeping the
  FluentValidation and Newtonsoft.Json gains), the IDF gate to drop corpus-wide boilerplate symbols, and a
  seed-preserving merge so expansion is recall-additive at the seed level.
- **Source:** LARGER (Lexically Anchored Repository Graph Exploration and Retrieval, arXiv 2605.16352) and
  recent SWE-bench localization work, which anchor lexical search into a repository graph and expand to
  structurally related evidence (callers, tests). **Idea:** follow reverse edges from query seeds to reach the
  users and tests of a matched concept. **Fit:** Layer 2A/4 query recall on the fast graph path. **Decision:**
  rejected. A measured A/B over the pinned corpus (query mode, headline budget) dropped mean recall from 51 to
  45 percent: FluentValidation rose (51 to 55) but MediatR (94 to 89), AutoMapper (29 to 25), and especially
  Newtonsoft.Json (30 to 13) regressed, because dependents of common types flood the candidate set and displace
  the real targets under the token budget. The existing forward-only query expansion is retained. A
  confidence-scored or seed-restricted reverse hop (LARGER-style) might recover the FluentValidation gain
  without the broad regression and is left for future work.
- **Source:** BM25 + small code-embedder rerank (multiple 2025 code-search papers report recall lifts to the
  low 70s percent at small K). **Idea:** rerank BM25 top-K with a learned model. **Fit:** Phase C opt-in
  hybrid rerank. **Decision:** deferred; remains opt-in only per the design invariant (no mandatory model
  bundle on the default path).
- **Planned (Phase A, scoped): tiered emission.** Emit expansion-neighbor files (provenance hop >= 1, the
  non-seed context) as skeletons rather than fully reduced, so each costs fewer tokens and the
  relevance-per-token packer (`ReductionAwarePacker`) fits more files under a fixed budget. Expected to lift
  recall most on large change sets where the budget currently truncates truth files (for example
  Newtonsoft.Json PR sets of 20+ files). Approach: thread seed-vs-neighbor provenance into a per-file
  reduction level (the pipeline currently applies one global `ReductionOptions`) or reuse the post-reduction
  thin-skeleton path that query member selection already uses. Needs golden-output coverage and a full
  Layer 2A/4 regeneration; left for a focused iteration.

### Breaking changes

- **Removed `--rerank` and `--embeddings` CLI flags.** Query scoping is BM25F-only again.
- **Removed `FUSE_EMBEDDINGS` and `FUSE_EMBEDDINGS_MODEL_PATH`.** No embedding backend or model resolution remains.
- **Removed MCP tool parameter `rerank` on `fuse_search` and `fuse_dotnet`.**
- **Removed bundled ONNX model from NuGet and runtime packages.** Release artifacts no longer ship a `models/` directory.
- **Removed the entire hybrid retrieval stack** (`IEmbeddingModel`, hashing rerank, vector cache in `.fuse/fuse.db`).

## [2.4.0]

### Added

- **`fuse reduce` CLI command and `fuse_reduce` MCP tool.** Compacts a caller-supplied set of files, or raw content, by running Fuse's reduction without collecting a whole directory. The agent (or a script) compacts context it has already identified instead of re-scoping. The CLI takes `--files` (a path list, written to stdout) or `--stdin` (piped content, with `--ext` selecting the reducer); the MCP tool takes `files` (paths) or `content` (with `extension`). Both accept a reduction `level` and a token ceiling. Content mode materializes a temporary file so the reducer is selected by extension, then deletes it. Backed by a new explicit-file collection mode (`CollectionOptions.ExplicitFiles`, `FusionRequestBuilder.WithExplicitFiles`) that reuses the full reduction and emission pipeline; missing paths are skipped rather than failing the run.

## [2.3.0]

### Added

- **`fuse mcp install --rules`**: opt-in flag that, alongside registering the MCP server, writes a short rule biasing the agent toward the `fuse_*` tools into each client's instruction file (Claude `CLAUDE.md`, Cursor `.cursor/rules/fuse.mdc`, GitHub Copilot `.github/copilot-instructions.md`). The rule is conservative: prefer Fuse for surveying and context-gathering, use grep for exact-string and symbol lookups. Freeform files (Claude, Copilot) get a marker-delimited block that re-runs replace in place rather than duplicate, preserving surrounding content. Rules are project-scoped; under `--scope user` only Claude has a global equivalent (`~/.claude/CLAUDE.md`) and the others are skipped with a note. A normal `fuse mcp install` now prints a tip pointing at the flag.

### Changed

- **More directive MCP tool guidance.** The `fuse_toc` and `fuse_search` tool descriptions and the server instruction block now tell the agent to prefer the `fuse_*` tools over raw grep or reading files one by one when surveying or scoping, and to use grep only for exact-string or symbol lookups. This biases clients (especially Cursor and Copilot, which lean on tool descriptions) toward Fuse without changing any behavior.

## [2.2.1]

### Fixed

- **`fuse mcp install` no longer requires `--command`.** The optional `--command` option was inferred as required by the command framework, so `fuse mcp install` failed with `Option '--command' is required` unless a value was passed. It is now declared optional and defaults to the running fuse binary, as documented. (2.2.0 shipped with this regression; 2.2.0 is unusable for install without an explicit `--command`.)

## [2.2.0]

Registering Fuse with an AI client is now one command, and the MCP surface is grouped under `fuse mcp`. The change is about setup ergonomics; the reduction, scoping, and emission paths are unchanged.

### Breaking changes

- **`fuse serve` moved to `fuse mcp serve`.** The stdio MCP server (the long-running process your client launches) is now a subcommand of the new `fuse mcp` group. Client configuration that launched `fuse serve` must change its arguments from `["serve"]` to `["mcp", "serve"]`. Re-running `fuse mcp install` rewrites them for you; for hand-written config, edit the `args` array. The MCP Registry manifest and the published package arguments are updated to match.

### Added

- **`fuse mcp install`**: registers Fuse as an MCP server with Claude Code, Cursor, or GitHub Copilot in one command, replacing per-tool JSON editing. `--client` targets one client or `all` (default); `--scope` writes project-local config (default, commit it so the team inherits Fuse) or user-global config (every project you open); `--command` overrides the launched executable. Claude Code user scope is registered through the Claude CLI; the other clients have their config file written directly. The installer merges into an existing config without dropping a co-located server's `env` or `cwd` block or top-level keys such as Copilot's `inputs` array, and it resolves the per-OS VS Code user profile directory rather than a path VS Code does not read.
- **`fuse mcp` command group**: parents `install` and `serve`, separating the MCP server surface from the one-shot CLI.

## [2.1.0]

### Breaking changes

- **Single reduction level replaces the C# reduction flag cluster.** `--all`, `--skeleton`, `--public-api`, `--aggressive`, and the `--remove-csharp-*` switches are removed in favor of one `--level` option (and a matching `level` MCP parameter) with the values `none`, `standard`, `aggressive`, `skeleton`, and `publicApi`. The CLI commands default to `none`; the scoped MCP tools (`fuse_focus`, `fuse_search`, `fuse_changes`, `fuse_dotnet`) default to `standard`, so an agent gets the standard removals (which preserve 99 to 100 percent of the public API surface on the benchmark corpus) without naming a level. Migrate `--all` to `--level aggressive` (add `--collapse-generated` if you relied on `--all` collapsing generated code), `--aggressive` to `--level aggressive`, `--skeleton` to `--level skeleton`, `--public-api` to `--level publicApi`, and any `--remove-csharp-*` flag to `--level standard`. Redaction, generated-code collapse, semantic markers, pattern summary, route map, project graph, and minification stay orthogonal to the level.

### Added

- **Opt-in local embedding model for hybrid rerank** (`FUSE_EMBEDDINGS`): the `--rerank` vector path can use a real local ONNX embedding model, realizing the `IEmbeddingModel` plug point that shipped in 2.0 behind a deterministic lexical fallback. The model assembly is excluded from the Native AOT package, matching the isolation of the Roslyn precision tier, so the default AOT binary stays reflection-free.
- **Chunk-granular query retrieval.** A `SymbolChunk` model and member-level chunk extractors let query scoping rank and pack at member granularity rather than whole files, feeding a thin-skeleton packing path that keeps the matched members in full while reducing the rest. Member selection is decoupled from file ranking so that packing at the member level does not lower file recall.
- **Reduction-aware single-pass packing.** Packing fits content to a token budget in one pass with reduction accounted for, instead of reducing and then re-fitting.
- **Near-duplicate member-body deduplication.** Members whose bodies are near-identical are collapsed, so repeated boilerplate bodies cost their tokens once.
- **Persistent BM25 body-tokenization cache.** Body tokenization for the relevance index is cached by content hash, so repeated scoped runs skip re-tokenizing unchanged files. This is separate from the persistent analysis index added in 2.0.
- **Tokenizer calibration harness.** A harness and a gated accuracy test calibrate the estimating tokenizers (the Anthropic and Gemini estimators) against reference counts.
- **Redaction fidelity reporting.** The redaction report distinguishes secrets found in code literals from those in configuration, so a run can show where redaction acted.

## [2.0.0]

Fuse 2.0 replaces the monolithic 1.x engine with axis-based projects and adds a Roslyn precision tier, hybrid retrieval, survey and round-trip tools, and a reproducible benchmark suite. Every measured figure below comes from the benchmark harness over the pinned corpus, counted with `o200k_base`; see [the benchmarks page](https://fuse.codes/docs/project/benchmarks). The precision tier and the survey, round-trip, and retrieval-rerank features are opt-in and do not change the default reduction or scoping path, so the default Layer 1 reduction and fidelity and the Layer 2 recall and precision are stable across runs.

### Breaking changes

- **Solution restructure.** Monolithic layers replaced by axis-based projects: Collection, Reduction, Emission, Fusion, plus language plugins (`Fuse.Plugins.Languages.CSharp`, `Fuse.Plugins.Formats.Web`).
- **MCP tools.** Legacy `get_optimized_context` removed. Replaced by eight focused tools: `fuse_toc`, `fuse_skeleton`, `fuse_focus`, `fuse_search`, `fuse_changes`, `fuse_ask`, `fuse_dotnet`, and `fuse_generic`.
- **Default tokenizer.** Token counting now defaults to `o200k_base` (was `cl100k_base`). Counts will differ from 1.x for the same content.
- **Secret redaction default ON.** API keys, JWTs, connection strings, and high-entropy literals are redacted before token counting. Use `--no-redact` to disable.
- **Manifest header default ON.** Output is prefixed with a file tree and per-file token costs. Use `--no-manifest` to disable.
- **Emission ordering.** Files emit in descending token-count order (largest first) to help agents prioritize within a budget.
- **Options model.** Monolithic `FuseOptions` replaced by `CollectionOptions`, `ReductionOptions`, and `EmissionOptions` carried in a `FusionRequest`.

### Added

#### Retrieval and scoping

- Dependency-aware focus scoping (`--focus`, `--depth`): scope a fusion to a type, file, or path and its dependency neighborhood.
- Git change scoping (`--changed-since`, `--include-dependents`): scope to files changed since a git ref, optionally pulling their first-degree dependents.
- BM25 query-scoped fusion (`--query`, `--query-top`): rank files by relevance to a natural-language or keyword query and expand from the top seeds.
- Reverse edges in scoping: focus and `--changed-since` pull a seed's dependents (files that reference the types it declares), not only its dependencies.
- Fielded ranking (BM25F): the relevance index weights declared type and member names and path tokens above the body, so the file that declares a concept ranks above files that merely mention it.
- Comment and string stripping before dependency extraction, removing false graph edges from type names that appear only in prose or text.
- Budget-aware, rank-decayed expansion: best-first traversal scores neighbours by parent score times a per-hop decay and stops at an optional token budget; seeds are always admitted.
- Query normalization: a shared tokenizer splits camelCase and snake_case, drops stopwords, and applies a light suffix stemmer to both documents and queries.
- Relevance-ordered truncation: emission writes most-relevant first under `--max-tokens` for scoped runs, so the seed survives the cut.
- Measured effect over the pinned corpus: Layer 2A recall rose for changes (71 to 88 percent), focus (26 to 43), and query (37 to 54), with changes precision 21 to 61; Layer 2B accuracy rose for query (25 to 67) and focus (25 to 42). All three scoping modes now clear the grep baseline.

#### Survey and round-trips

- Table-of-contents mode (`--toc`, `fuse_toc`): a directory tree with per-file token costs and a symbol outline instead of file bodies. A cheap first call for surveying a codebase before fetching files. Backed by a new `ISymbolOutlineExtractor` capability.
- `fuse_ask` MCP tool: takes a task and a token budget, deterministically picks skeleton, focus, or search from the task text, and packs the result to budget. Focus falls back to search when the named type does not resolve.
- Review-shaped change emission (`--review`, `fuse_changes` `review`): prepends a review map pairing each changed file's unified diff hunks with its direct callers.
- Session-delta emission (`--session`, the `session` parameter on `fuse_focus` and `fuse_search`): omits files whose identical content was already emitted under a session id, with a note listing them; a changed file is resent. Backed by a process-scoped session tracker.

#### Precision tier (opt-in, AOT-isolated)

- Roslyn semantic plugin (`--semantic`, `FUSE_SEMANTIC`): Roslyn syntax-tree implementations of the C# skeleton, dependency, type-name, and outline capabilities, registered after the regex defaults so they win for `.cs`. Fixes the regex skeleton collapse on conditional compilation and partial classes and captures references the regex misses. Shipped in a separate assembly the Native AOT package does not reference; the AOT build stays regex-only and IL2026/IL3050 clean.
- Symbol-level scoping: with the precision tier, a `Type.Member` focus seed scopes the seed file to that member (full body) with the rest of the file reduced to signatures. Backed by a new `ISymbolSliceExtractor` capability.
- Persistent analysis index (`--index`, on by default in watch and serve): caches per-file dependency and symbol analysis under `.fuse/index`, keyed by content hash and analyzer tier, shared across a run. Amortizes the Roslyn parse cost; measured roughly halving warm-call wall-clock on MediatR.
- Hybrid retrieval (`--rerank`, `fuse_search` `rerank`): reranks BM25 candidates by blending the normalized BM25 score with embedding-vector cosine similarity. The bundled embedding is a deterministic, AOT-clean lexical hashing model; the `IEmbeddingModel` interface is the plug point for a learned model. Vectors cached under `.fuse/index/vectors`.
- Cross-language reduction: the JavaScript reducer now covers TypeScript and the JSX, TSX, and ESM variants; a new SQL reducer handles `.sql`.
- Generated-code collapse (`--collapse-generated`, included in `--all`, `fuse_dotnet` `collapseGenerated`): collapses EF Core migration and model-snapshot generated bodies to their signatures, which the default generated-file exclusion patterns miss.

#### Structural maps and patterns

- .NET structural maps: route map (`--route-map`), public API skeleton (`--public-api`), and project graph (`--project-graph`).
- C# skeleton mode (`--skeleton`) and semantic markers (`--semantic-markers`).
- Cross-codebase pattern summary (`--pattern-summary`).

#### Output, trust, and developer experience

- Manifest header with file tree, token costs, pattern summary, and optional git stats.
- Compact output envelope (`--format compact`): a single header line per file and no closing marker, for fewer envelope tokens. XML stays the default.
- Header dedup (`--dedup-headers`): identical leading comment headers shared by two or more files are emitted once in a preamble and replaced with a marker; preprocessor directives and code are untouched.
- Secret redaction with `[REDACTED:<kind>]` placeholders (`--no-redact`, `--redact-report`).
- Inclusion provenance annotations for dependency-expanded files (`--provenance`).
- Anthropic and Gemini tokenizers: deterministic estimators selected by model name (`claude*`/`anthropic*`, `gemini*`/`google*`); OpenAI encodings remain exact.
- `fuse verify`: reports the preserved percent of public types, methods, and routes after a fusion (Roslyn syntax-only in the framework-dependent tool, AOT-clean regex fallback in the Native AOT build).
- `fuse explain`: dry run listing included and excluded files with a token estimate; writes nothing.
- Machine-readable JSON run report (`--report <path>` or `--report -`): source-generated and AOT-safe; always names the tokenizer used.
- Git enrichment in the manifest (`--git-stats`: churn and last-modified per file).
- Project config discovery: `fuse.json` and `.fuserc` with flag over config over default precedence; `fuse init` scaffolds a config file.

#### Core architecture

- Language plugin model with `ILanguageCapability` and `CapabilityRegistry<T>`.
- `IEntryFormatter` with XML, Markdown, and JSON output formats (`--format`).
- `ISourceContentProvider` for single-read file access across pipeline stages.
- Registry-driven reduction, skeleton extraction, dependency extraction, and type location.
- MCP resources for skeleton, focus, search, and change workflows.

#### Performance and Native AOT

- Parallel collection, reduction, and graph building (`--parallelism`, default: processor count).
- Per-file reduction cache in `.fuse/cache` (`--no-cache`, `--clear-cache`).
- Watch mode for iterative fusion (`--watch`; disabled under MCP stdio).
- Token counting via `Microsoft.ML.Tokenizers` (`o200k_base`, `cl100k_base`).
- Source-generated JSON for config and JSON output (AOT and trim safe).
- Native AOT publish profiles and `Fuse.Runtime.{rid}` satellite packages; see [the performance page](https://fuse.codes/docs/project/performance).
- Windows installer ships an AOT-compiled `fuse.exe`.

### Fixed

- `FusionResult` reconstructions dropped per-file token data and cache statistics, leaving the JSON run report, `fuse explain`, and `fuse verify` with no file list; these now propagate through the pipeline.
- `--format` and `--tokenizer` were inferred as required by the command framework; documented invocations such as `fuse dotnet --directory ./src` now work without them.

### Migration from Fuse 1.x

| Area | 1.x behavior | 2.0 behavior | Action |
|------|--------------|--------------|--------|
| MCP tool | `get_optimized_context` | Eight focused tools (see [MCP tools reference](https://fuse.codes/docs/reference/mcp-tools)) | Update agent prompts and MCP config |
| Token counts | `cl100k_base` | `o200k_base` default | Re-baseline `--max-tokens` budgets, or pass `--tokenizer cl100k_base` |
| Output prefix | None | Manifest header | Use `--no-manifest` if agents expect raw file blocks only |
| Secrets | Passed through | Redacted by default | Use `--no-redact` only when secrets are intentional test fixtures |
| Options object | Monolithic `FuseOptions` | `CollectionOptions`, `ReductionOptions`, `EmissionOptions` in `FusionRequest` | Update programmatic callers |
| Reducers | Static switch in `ContentProcessor` | `CapabilityRegistry<IContentReducer>` | Register reducers via dependency injection |
| Temp files | `{baseFileName}.tmp` collision risk | GUID-based temp names | No action needed |

If your workflow depended on exact 1.x byte output, pin the 1.x tool version or pass explicit flags to approximate prior behavior:

```bash
fuse dotnet --directory ./src --no-manifest --no-redact --tokenizer cl100k_base
```

For MCP agents, replace single-tool calls with the recommended workflow: survey with `fuse_toc` or `fuse_skeleton`, then `fuse_focus` or `fuse_search`, then `fuse_changes` for PR review. See [Context for an agent](https://fuse.codes/docs/scenarios/context-for-an-agent).
