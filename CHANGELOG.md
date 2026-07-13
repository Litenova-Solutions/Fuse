# Changelog

All notable changes to Fuse are documented here. The format is based on Keep a Changelog. Fuse 4.0.0 is the first public release; it carries the whole product and there is no prior public version to migrate from.

## [4.0.1] - 2026-07-13

### Added

- `fuse mcp install --rules` at project scope appends `.fuse/` to `.gitignore` when no equivalent entry exists (same helper as `fuse init`).
- Connect-your-agent documentation covers manual registration for other MCP clients (Windsurf, Cline, Zed, and custom agents) over the same `fuse mcp serve` stdio server.
- Host RPC threat-model documentation (`internals/host-rpc`) describes the local-trust IPC model: predictable per-root pipe or socket, handshake session token, and served-root binding on RPC methods that carry a `root` argument.

### Changed

- Host RPC methods that carry a `root` argument reject calls where the root differs from the daemon's served root (the `--directory` the `fuse host` process started with), so a peer on the correct pipe cannot pivot to another repository path.

- `ExperimentalOptions` now carries only focus/change scoping and emission-shaping knobs the fusion pipeline consumes (`CentralityWeight`, `TieredEmission`, `SketchHugeFiles`, `DowngradeBeforeDrop`, `ProximityEdges`, `ProjectGraph`). Query-path retrieval levers (`HopDecay`, `QueryExpansion`, `ExpansionWeight`, `MultiQueryFusion`, `BudgetAwareExpansion`, `GitChurnWeight`, `DistributionalThesaurus`, `MemberLevelRetrieval`, `HeuristicQueryRewrite`, `FieldedComments`) were removed from this type; open-ended localize and related lexical ranking live in `Fuse.Retrieval`. The corresponding `FUSE_*` environment overrides on the fusion path are dropped; set retrieval options on the retrieval entry points instead.
- Derived key-value cache data (reduction cache and per-file analysis index) now lives in `.fuse/fuse-cache.db` instead of sharing `.fuse/fuse.db` with the semantic index. Existing cached entries in `fuse.db` are not migrated; rerun with `--use-cache` or `--use-persistent-index` to rebuild the derived cache. The semantic index in `fuse.db` is unchanged.
- Consumer copy on fuse.codes, the README, and the docs index uses mechanism-first language for senior .NET developers and MCP authors (warm index, typed graph, verification grade) instead of two-beat marketing slogans.
- Docs and install help state that MCP read tools build `.fuse/fuse.db` on first use; `fuse index` or `fuse_workspace action=index` remain optional pre-warm steps before the agent's first turn.
- Product copy names Cursor, Claude Code, and Copilot as common MCP clients with auto-install, not as an exclusive list; any MCP-compatible client can run `fuse mcp serve`.
- The landing page hero is simplified: one demo, Connect and Quickstart CTAs, proof stats and install blocks moved below the fold.

### Fixed

- When FTS5 is unavailable at index init, the store persists `fts_available=false` in index meta, names it in `fuse_workspace` status and the availability header, and `fuse_find kind=task` refuses with an actionable message instead of returning empty hits.
- Mermaid flowcharts and theme-aware SVG diagrams render on the documentation site instead of appearing as raw code blocks.
- Portable capture bundles no longer fail the secret scan on Roslyn `RegexGenerator.g.cs` emitted files.

## [4.0.0]

Fuse is a resident .NET compiler service for AI agents. It holds a workspace's Roslyn compilations in memory, verifies a proposed edit before it lands, computes a change's blast radius, stages compiler-executed refactors as diffs, runs the covering tests for a symbol, and resolves what the code actually runs. Every answer is stamped with a verification grade, and Fuse abstains honestly when it cannot answer at compiler grade rather than guessing. Scoped, reduced context and a deterministic lexical retrieval channel are the supporting machinery that feed those answers. It ships as a .NET global tool (`fuse`) and as a Model Context Protocol server (`fuse mcp serve`).

### The MCP tool surface

The server exposes a loop-shaped surface of eight tools, each one mental act, plus `fuse_reduce` as the one out-of-loop utility. The loop is teachable in a tool description: after an edit run `fuse_check`; before a signature change run `fuse_impact`; before done run `fuse_review`.

- **`fuse_workspace`** - status and lifecycle. `action=status` (index mode, verification grade, freshness), `index` (build or refresh the index), `map` (symbols, routes, counts), `doctor` (per-project semantic-load diagnosis), and `apply` (write a proposed single-file edit to the working tree - the one explicit tree-write path, a dry run unless `write=true`, refusing any path that escapes the workspace root).
- **`fuse_find`** - the find union, keyed by `kind`: `symbol|path|text|all` (exact lookup), `service|request|route|config` (resolve wiring to implementation, handler, action, or options), `signatures` (a symbol's exact signature, resolved from a resident compilation's real metadata when one serves the root), `neighbors` (callers and implementers), and `task` (rank candidate files with the graded refuse-and-route contract). No source bodies.
- **`fuse_context`** - emit scoped, reduced source with a semantic manifest and per-file provenance for selected seeds.
- **`fuse_impact`** - blast radius for a symbol (callers, implementers, referencing types) from the persisted graph; also a NuGet upgrade break set via `package:{id,fromVersion,toVersion}`.
- **`fuse_check`** - typecheck a proposed single-file edit and return the diagnostics it would produce, with repair packets on API-shape errors. The verification-grade ladder means it never shrugs: oracle-grade against the build-captured compilation when tier-1 is available, else build-grade by running `dotnet build` scoped to the owning project, abstaining only when even the toolchain cannot run.
- **`fuse_test`** - run the covering tests for a symbol (the tests that reach it through the persisted `tests` edges), scoped by filter so the whole suite never runs. Candidate racing (`candidates`): speculatively typecheck several proposed single-file edits against one resident compilation and get a per-candidate verdict plus a winner by strict dominance (a clean candidate beats any with errors; ties reported), so an agent weighing plausible fixes learns which survives without picking on vibes.
- **`fuse_refactor`** - compiler-executed, verify-gated refactors staged as a diff: rename, add/remove/reorder-parameter, add-cancellation-token, extract-interface, move-type, apply-codefix. Each recompiles and returns the diff only when no new diagnostic is introduced, else abstains.
- **`fuse_review`** - diff-first change impact and packed context, opening with a public API delta and carrying a paste-ready PR handoff packet (`handoff=true`) gated on a clean check session.
- **`fuse_reduce`** - compact a known set of files or raw content; the one utility outside the loop.

The MCP server also exposes playbook prompts (anchored plans that teach the verified-edit loop) and addressable resources for the map, localize, context, review, status, diagnostics, diff, and session-ledger workflows.

### Verification and honesty

- **The verification-grade ladder.** Every `fuse_check` answer is stamped `oracle` (a speculative in-memory typecheck against the tier-1 build-captured compilation), `build` (the real `dotnet build` toolchain scoped to the owning project, parsed into the same shape), or `abstain` (only when even the toolchain cannot run, always naming the missing prerequisite). The build-grade path never writes the working tree; it mirrors the owning project to a temporary directory with the one file replaced and project references rewritten to absolute paths. Because a rehydrated compilation is analyzed, never emitted, strong-name signing is neutralized before its diagnostics are read, so a captured relative key-file path that does not resolve in the rehydration sandbox never surfaces as a spurious signing error (CS7027) nor drops a cleanly building strong-named repository below tier-1.
- **Delta mode and persisted sessions.** Pass a `session` id with no content and `fuse_check` returns the diagnostics your on-disk edits introduced or resolved since the session baseline. Baselines persist to the store, so a restarted process resumes intact; `markGreen` resets the baseline and `full` returns the whole current set. Delta mode never runs a build; it reads whole-state diagnostics from a live resident workspace and abstains when none serves the root.
- **Repair packets.** An API-shape diagnostic (a missing member, an unknown type, a missing argument, a wrong-type assignment) carries a machine-applicable fix (the offending token and the nearest recorded name to substitute), rendered as an `apply: replace 'X' with 'Y'` line, drawn from the persisted symbol table so the fix costs no round-trip.
- **Analyzer and nullable parity.** When a resident workspace serves the root, `fuse_check` also runs the repository's configured analyzers and nullable warnings against the overlay at the editorconfig severities, so a green check matches what CI's build step enforces. On by default for the single-file verify, off for the hot per-edit delta path.
- **Graded claims and the evidence ledger.** The statements Fuse emits are graded by the evidence behind them: `verified` (compiler- or test-grade), `partially verified` (graph-grade, the inflation guard), `stale`, and `contradicted`. Claims accumulate into a session ledger addressable as a resource.

### The resident workspace

A resident workspace rehydrates a tier-1 build capture once and holds the per-project Roslyn compilations in memory, so a proposed edit typechecks against a live compilation with no build and no disk write, and a file watcher keeps it current. It is opt-in this release (`FUSE_RESIDENT`), projecting the changed cone into the store so store-backed reads reflect edits.

### The shared daemon

One `fuse host` daemon per repository can hold the resident workspace as a shared asset, so multiple agent sessions and the ambient hooks read one warm compilation instead of each paying its own cold start and memory. A daemon acquires a single-instance lock per root (a redundant second host exits cleanly), stops itself after an idle window (`FUSE_DAEMON_IDLE_MINUTES`), and refuses a protocol-mismatched client so a stale client after an upgrade triggers a clean restart. `fuse mcp serve` with `FUSE_DAEMON=1` delegates its resident checks to the daemon over the pipe instead of holding its own workspace, falling back to in-process when no daemon can start; `fuse workspace status` names the daemon (PID, uptime, memory). Measured on NodaTime: two sessions cost one resident workspace (about 109 MB) instead of two. Opt-in this release.

### Compiler-executed refactors

`fuse_refactor` runs Roslyn-driven, verify-gated edits: rename (a same-named unrelated symbol is not renamed), the change-signature family (add/remove/reorder-parameter with semantic-safety abstentions, add-cancellation-token threading an in-scope token), extract-interface, move-type, and apply-codefix (driving a diagnostic to zero with the repository's own analyzer fixes). Every operation recompiles and returns a diff only when no new diagnostic is introduced, otherwise abstaining with the offending sites named.

### Semantic index and retrieval

- **The persistent index.** A single SQLite database at `.fuse/fuse.db` (WAL mode) holds files, projects, symbols, chunks, a typed semantic graph, routes, and an FTS5 full-text index. The workspace loads through MSBuild and Roslyn with a syntax-only fallback; re-indexing is incremental per changed file, and no read tool serves silently stale data (a warm store is reconciled against the current on-disk content before it answers).
- **The wiring analyzers.** The semantic analyzers resolve DI registration and constructor injection (including keyed DI), MediatR request-to-handler, ASP.NET route-to-action, options binding, EF Core, Scrutor decoration, factory and hosted-service registration, pipeline behaviors, minimal-API, gRPC, and SignalR. Persisted `references` and DI-resolved `tests` edges back `fuse_impact` and covering-test selection.
- **Retrieval.** A deterministic lexical channel (BM25F over the FTS5 table) with offline subword, stem, and comment bridges plus a dependency-centrality prior. No model is fetched or shipped. Ranked task localization is the fallback mode; the precise path is an anchor (symbol, route, service, request, config, git base) resolved through the graph. The git co-change prior is off by default: its semantic-mode re-adjudication on corpus v2 recorded it as net-negative to ranking (MRR 0.434 with the prior versus 0.489 without), so it was dropped from the shipping default; the ranking gate keeps it measured behind a diagnostic config.
- **Multi-language syntax tier.** A provider seam drives the syntax tier; C# is first-party, and Python and JavaScript/TypeScript are supported at the syntax tier. Each indexed file carries a `language` tag. The deep typed graph is C#/Roslyn only.
- **Tier-1 build capture is default-on and the worker is bundled.** The oracle is the product: the build-capture worker ships inside the global tool (under `build-capture/` beside `fuse`) and is discovered with no configuration, and tier-1 build capture is attempted by default. It degrades cleanly when no build target exists or a build fails (to the MSBuild and syntax tiers), and opts out with `FUSE_BUILD_CAPTURE=0`. In `fuse mcp serve` the cold start is syntax-first with a supervised background upgrade to the semantic/tier-1 graph, so the first reads return in seconds while the build runs behind them; the availability header names when a build is running for tier-1, and `FUSE_BG_UPGRADE=0` opts back into a synchronous first read. `fuse index` on the CLI is always synchronous.
- **Portable capture bundles.** `fuse capture --out <bundle>` builds once and packages the compiler log, the extracted graph, and a versioned manifest; `fuse index --from-capture <bundle>` rehydrates the semantic graph and answers `fuse_check` at oracle grade on a machine that cannot restore or build, with no build. The bundle never ships the MSBuild binary log, and a planted secret fails the capture closed. An in-repo GitHub Action captures a bundle on `main`. `fuse capture --merge <dir>` assembles a bundle from per-project fragment binary logs (a build-target channel), equal in extracted graph to a direct capture; the bundle format is backward-compatible (a newer Fuse reads an older bundle; a bundle newer than the running Fuse is refused with an actionable message).

### Context, review, and reduction

`fuse_context` plans and emits source at mixed render tiers with a semantic manifest and per-file provenance, honoring a token budget and eliding files already sent in a session. `fuse_review` opens with a public API delta (members added, removed, or changed between the git base and the working tree, each flagged breaking or additive) and produces a paste-ready PR handoff packet gated on a clean check session. The Roslyn skeleton reduction keeps every public and protected type and nearly all public methods while removing a large share of tokens; fidelity is measured against Roslyn as independent ground truth. Secret redaction runs before any source reaches a payload.

### CLI

`fuse index`, `map`, `localize`, `resolve`, `context`, `review`, `find`, `impact`, `check`, `test`, `refactor`, `diagnostics`, `doctor`, `reduce`, `up`, `verify`, `gate`, `init`, `update`, `mcp` (install and serve), and `host`. `fuse review --handoff` and `fuse impact --package` mirror the agent verbs.

### Ambient verification (hooks)

`fuse check --delta` prints the diagnostics your session's on-disk edits introduced or resolved since its baseline; `fuse gate` exits nonzero while the session has introduced errors it has not resolved (baseline discipline: only errors the session itself introduced block). Both connect to an already-running host over a pipe and never run a build; with no host serving the root they exit 0 silently, so a hook never blocks editing. `fuse mcp install --with-hooks` writes the matching Claude Code hooks into project `.claude/settings.json`.

### Environment remediation

`fuse up` diagnoses why a workspace does not load at oracle grade (per-project tiers and the concrete reason for each downgrade) and, with `--apply`, applies an install-free remedy (a NuGet source-mapping overlay for the NU1507 multi-source case) and re-attempts the load, never editing the repository. Consent-gated install remedies (an SDK band per `global.json`, a missing workload) run only behind `--allow-install`. `fuse verify --ci-parity` reads the repository's CI workflows, extracts the dotnet command sequence, and names the steps it cannot rehearse locally.

### The NuGet upgrade oracle

`fuse_impact package:{id,fromVersion,toVersion}` diffs the public API of two package versions (resolved from the local NuGet cache) and lists the breaking changes a bump would introduce, abstaining offline and naming its blind spots (reflection and dynamic usage; external call sites are not tracked) on every report.

### Licensing and contribution

Fuse ships under the Apache License, Version 2.0, with an explicit patent grant and a `NOTICE` file. Contributions require a Developer Certificate of Origin sign-off (`git commit -s`), enforced in CI.

### Storage and versioning

Persistent cache and index data live in a single SQLite file at `.fuse/fuse.db` (WAL mode). The index records the Fuse build that wrote it and rebuilds itself on an incompatible upgrade rather than serving stale extraction. The product version lives in the codebase (`Directory.Build.props`), and one tag releases the NuGet package, the GitHub binaries, and the MCP registry manifest together.
