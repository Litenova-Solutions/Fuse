# Changelog

All notable changes to Fuse are documented here. The format is based on Keep a Changelog. Fuse 4.0.0 is the first public release; it carries the whole product and there is no prior public version to migrate from.

## [Unreleased]

## [4.2.0] - 2026-07-14

### Added

- CI SDK workflow (`.github/workflows/ci-sdk.yml`) runs `Category=RequiresSdk` integration tests on win-x64 and linux-x64 with the .NET 10 SDK; the default PR leg excludes them via `Category!=RequiresSdk`. `build/verify-ci-sdk.ps1` fails when either leg is missing or misconfigured.
- `build/verify-briefing.ps1` fails on stale product claims in `briefing.md` (fourteen MCP tools, `ext/vscode`, retired tool names); wired into the default CI build job.
- `FUSE_HOOK_VERBOSE=1` logs swallowed hook RPC failures to stderr with method name and error code; default remains silent.
- `fuse update --force-kill-peers` restores the pre-4.2 broad peer termination behavior; the default path terminates only the updating tool's own child lineage and same-install peers.
- Unified API surface reference (`reference/api-surfaces`) maps each user intent to the MCP tool, CLI command, and host RPC method; `ApiSurfacesDocParityTests` keeps the doc in sync with shipped tools and RPC methods.
- `FUSE_HOST_RESTRICT_PIPE=1` restricts the Windows named pipe ACL to the current user; default remains open to local processes.
- Recorded index hot-path profile artifact (`tests/benchmarks/results/profile-v42.json`) with schema validation in `ProfileV42ResultTests`; regenerate with `fuse eval profile-v42 --repo NodaTime`.
- Narrow index store ports behind `WorkspaceIndexStore`: `IndexSchemaMigrator`, `FtsSearchEngine`, `SymbolGraphStore`, and `SessionStore`.
- Shared `Fuse.Scoping` module with a single `ContextPlan` type used by Fusion focus scoping and MCP review/localize.
- Semantic-tier provider seam (`ISemanticLanguageProvider`, `SemanticLanguageProviderRegistry`); C# registers through `CSharpSemanticLanguageProvider` from the Roslyn plugin; `Fuse.Semantics` no longer references the Roslyn plugin project directly.
- Host RPC outcome tests for protocol version mismatch, served-root rejection, reconcile stamped headers, and `fuse_check` grade stamping.
- `IndexCoordinator`: one writer queue per workspace root; cross-process contention returns `index_busy:` instead of throwing.
- Stable operational error prefixes on MCP tools and mirrored CLI commands: `index_busy:`, `index_not_built:`, `workspace_not_found:`, `validation_error:`, `index_rebuilding:`, and `internal_error:`.
- Fast `fuse_workspace action=status` and `action=doctor` summary header: read-only `index_meta` when `.fuse/fuse.db` exists; cold workspaces report `not_indexed` without creating the database.
- `WorkspaceIndexStore.OpenForReadAsync` for warm read opens that verify schema without writing `index_meta`.
- `fuse_check` and `fuse_test` run compiler-grade verification before opening the store; repair-packet enrichment is omitted with a named note when the store is unavailable.
- Daemon-owned index writes (G5 phase 2): `fuse host` owns index open, reconcile, syntax-first, and semantic upgrade; `fuse mcp serve` delegates store-backed calls over the pipe when the daemon is active. `FuseHostService.ProtocolVersion` bumped to 8.
- Daemon lifecycle visibility (R28): each `fuse host` writes a descriptor (served root, PID, version, start time) to a machine registry under `{user-data}/daemons/` on start and removes it on shutdown; `fuse_workspace action=doctor` lists running daemons and prunes descriptors whose process is dead, so accumulated or version-mismatched daemons are visible. One daemon per root is enforced by the single-instance lock; idle daemons shut down after `FUSE_DAEMON_IDLE_MINUTES`; in-session auto-update hands off via the detached updater (waits for the process to exit) rather than a kill race.
- Bounded, visible cold start (R27): the first read on a cold repo runs the syntax-first index build in the background (deduplicated per root) and waits only up to `FUSE_COLD_READ_DEADLINE_MS` (default 2500 ms); if the build outruns the deadline the read returns a bounded `index_state: building_syntax` header with `files_indexed` instead of blocking for the whole build (tens of seconds on a large repo), and a second read in the same session serves the warming index. A small repo whose build finishes within the deadline serves results on the first read; a synchronous in-process caller is unaffected.
- `fuse_review` bounds a large diff (R26): when the changed-file set exceeds a cap (default 150, override with the `maxChangedFiles` parameter or `FUSE_REVIEW_MAX_CHANGED_FILES`), it returns the changed-file list and a narrow-the-base-ref note instead of running blast-radius resolution unbounded. A normal PR-sized diff is unaffected. `maxTokens` still bounds output.
- Availability headers on store-backed read tools lead with `index_state:` (`not_indexed`, `building_syntax`, `upgrade_pending`, `ready`, `index_busy`, `stale_as_of`) plus `files_indexed` when known; blocked reads return the header as the tool body within bounded time instead of hanging.
- Corrupt or version-incompatible `fuse.db` self-heals: derived index data is deleted and rebuilt from source with serialized recovery per root; MCP callers receive `index_rebuilding:` instead of an unhandled exception.

### Changed

- Index freshness is decoupled from the product version (R22): reuse is gated on the relational schema version AND a new extraction-contract version (`WorkspaceIndexSchema.ExtractionContractVersion`, stamped as `index_extraction_version` in `index_meta`), never on `fuse_version`. A minor or patch upgrade that does not change what is extracted now reuses a good index instead of forcing a reindex on every bump (with `FUSE_AUTO_UPDATE` default-on, the old policy discarded a good index on each minor bump). The `fuse_version` stamp is kept for diagnostics only. Migration: a pre-4.2 index carrying only `fuse_version` rebuilds once on first open to gain the extraction stamp, then reuses. This supersedes the earlier `FuseBuildInfo.IsCompatible` major.minor index-open gate (capture-bundle compatibility still uses it).
- `FUSE_DAEMON` defaults on for `fuse mcp serve`; set `FUSE_DAEMON=0` to run in-process without the shared `fuse host` daemon per repository.
- `FUSE_AUTO_UPDATE` defaults on for `fuse mcp serve`; the updater runs after session exit without killing sibling sessions (`stopOtherHosts: false`). Set `FUSE_AUTO_UPDATE=0` to opt out.
- `fuse mcp install` writes command-only client config (no `env` block); agent-first defaults (daemon, auto-update, background upgrade, build capture) ship in the binary unless explicitly opted out.
- `fuse update` default peer termination is narrowed to the updating install's lineage; unrelated `fuse mcp serve` sessions in other repositories are not killed.
- `fuse_workspace action=status` no longer triggers a full syntax-first index on a cold workspace; use `action=index` to build explicitly.
- Warm `WorkspaceIndexStore` reads use `OpenForReadAsync` instead of write init, reducing lock contention during background semantic upgrade.
- `briefing.md` body aligned with nine MCP tools, no VS Code extension (D15), and canonical benchmark figures from `tests/benchmarks/results`.
- `roadmap/README.md` notes that `briefing.md` tracks shipped product, not the executable checklist.
- `SECURITY.md` local-trust subsection documents host pipe limits and `FUSE_HOST_RESTRICT_PIPE`.
- `AGENTS.md` design invariants updated for default-on daemon (D13), single-writer index, semantic provider seam, and index self-heal policy.

### Removed

- `FtsCandidateGenerator` and the `FUSE_FLAT_FTS=1` diagnostic flag; `LexicalCandidateGenerator` is the sole lexical retrieval path.

### Fixed (semantic discovery)

- Indexing scope excludes vendored and generated trees (R25): the file scanner honors a `fuse.json` `ignore` array (directory names, merged with `.fuseignore` and the built-in defaults) and prunes any nested version-control root (a vendored checkout or submodule with its own `.git`) in the directory-walk path, matching the git-native path which already excludes `.gitignore`d trees such as `tests/benchmarks/.corpus` and `site/node_modules`. There is no silent file cap; `fuse_workspace action=status` counts reflect the real source set.
- Semantic solution/project discovery targets the repository's own solution (R24): a solution at or near the repo root is preferred over one nested under a test, fixture, or sample directory, so a repo like Fuse (root `Fuse.slnx` plus a nested `tests/.../SampleShop.sln`) no longer binds the typed graph to the fixture solution. Multiple distinct root-level solutions are resolved by name order and the choice is surfaced in `doctor`; when the only solutions are under fixture directories but the repo has real projects, those projects are loaded instead. A new `fuse.json` `solution` key pins the target explicitly, and `fuse_workspace action=doctor` names the selected solution and warns on ambiguity or a fixture-directory selection.

### Fixed

- `SqliteException` database locked during `OpenIndexedAsync` no longer escapes MCP tool boundaries as an opaque `An error occurred invoking ...` error; CLI `fuse find` no longer crashes with a stack trace on index lock.
- Read-tool store opens and the per-read reconcile now apply a short (1 s) SQLite `busy_timeout`, so a contended store surfaces the `index_busy` availability header within a couple of seconds instead of blocking on the 30 s write-path timeout; cold index builds keep the long timeout. This makes the R20 "blocked reads return the header within bounded time" contract hold in practice.
- Host RPC error responses serialize `StreamJsonRpc.Protocol.CommonErrorData` through the source-generated `FuseHostJsonContext`; previously any RPC method that threw surfaced a `NotSupportedException` at the transport instead of the actual error (the ambient-verification hooks then stayed silent on real failures).
- `fuse_check` delta mode initializes the session-baseline store when the index is not yet built, matching the host RPC baseline path, instead of abstaining with "index unavailable" while a resident workspace is active.
- Served-root binding enforced on every host RPC entry point that carries a `root` argument.
- `operator.mdx` no longer overclaims automatic `fuse.db` recreation; corrupt index recovery matches the implemented self-heal path.
- A version/schema-mismatch rebuild now produces a fully working, searchable index (R23): the rebuild path re-probes FTS, recreates `chunk_fts`, and stamps the index mode, instead of returning early and leaving a store that had indexed files but no `chunk_fts`. Previously the next search threw `internal_error: SQLite Error 1: 'no such table: chunk_fts'`.
- A search issued against a store missing `chunk_fts` now maps to the `index_rebuilding:` operational prefix (via `SearchIndexUnavailableException`) and triggers a rebuild, never a raw `internal_error: SQLite Error`.
- FTS availability is a single source of truth (R23): `OpenForReadAsync` and `GetStateAsync` reconcile the `fts_available` stamp against the actual `chunk_fts` table, so the availability line and the status body never disagree, and a stamp of "available" over a store missing the table forces a rebuild rather than serving broken search.
- A store with indexed symbols but zero chunks on an FTS-available runtime is never reported `index_state: ready` (R23); it reports `index_rebuilding` so the read path repairs it instead of serving silent-empty results.

## [4.1.0] - 2026-07-14

### Added

- Fuse brand icon (`assets/fuse-icon.png`, `assets/fuse-icon.svg`) on the NuGet package gallery, MCP Registry manifest, WinGet locale, site favicons, and the repository README.
- Host RPC threat-model documentation (`internals/host-rpc`) describes the local-trust IPC model: predictable per-root pipe or socket, handshake session token, and served-root binding on RPC methods that carry a `root` argument. The page documents `fuse/check` and `fuse/checkOverlay`.
- Opt-in MCP metrics via `FUSE_METRICS=1` (`fuse.tool.duration`, `fuse.index.mode`, `fuse.reconcile.stamped`) using `System.Diagnostics.Metrics` (no OpenTelemetry dependency).
- `WorkspacePathResolver` confines MCP file arguments (`fuse_check`, `fuse_test`, `fuse_reduce`, `fuse_context`, and related paths) to the workspace root.
- `fuse.json` / `.fuserc` parse failures write a warning to stderr with the file path instead of failing silently.
- `FUSE_MCP_INSTALL_HOME` redirects user-scope MCP install paths for isolated testing; user-scope install coverage for Cursor and Copilot.
- Integration tests for split-store cache recovery, storm reconcile (>300 dirty files), MCP read-after-edit freshness, resident storm eviction (301-file batch), and workspace path escape refusal.

### Changed

- Derived key-value cache data (reduction cache and per-file analysis index) now lives in `.fuse/fuse-cache.db` instead of sharing `.fuse/fuse.db` with the semantic index. Existing cached entries in `fuse.db` are not migrated; rerun with `--use-cache` or `--use-persistent-index` to rebuild the derived cache. The semantic index in `fuse.db` is unchanged.
- `ExperimentalOptions` now carries only focus/change scoping and emission-shaping knobs the fusion pipeline consumes (`CentralityWeight`, `TieredEmission`, `SketchHugeFiles`, `DowngradeBeforeDrop`, `ProximityEdges`, `ProjectGraph`). Query-path retrieval levers were removed from this type; open-ended localize and related lexical ranking live in `Fuse.Retrieval`.
- Host RPC methods that carry a `root` argument reject calls where the root differs from the daemon's served root (the `--directory` the `fuse host` process started with).
- Syntax indexing routes exclusively through `SemanticIndexer` and language syntax providers; the standalone `SyntaxIndexer` type is removed.
- The retired flat FTS candidate generator is gated behind `FUSE_FLAT_FTS=1` diagnostic mode; shipping default remains `LexicalCandidateGenerator` only.
- Performance documentation and README warm-latency figures align with `tests/benchmarks/results/performance.json` and `resident-latency.json`.
- `briefing.md` opening reflects nine MCP tools, no VS Code extension (D15), and corpus-v2 localize recall (37.7 percent).
- The fuse.codes landing page uses a centered hero with terminal-style install commands, benchmark stats, and a shared site footer; WinGet manifests are included in `set-version.ps1` and `verify-version.ps1`.

### Fixed

- When FTS5 is unavailable at index init, the store persists `fts_available=false` in index meta, names it in `fuse_workspace` status and the availability header, and `fuse_find kind=task` refuses with an actionable message instead of returning empty hits.
- `SqliteKeyValueStore` corruption recovery refuses to delete the semantic index database; only `fuse-cache.db` is recreated on corruption.

## [4.0.1] - 2026-07-13

### Added

- `fuse mcp install --rules` at project scope appends `.fuse/` to `.gitignore` when no equivalent entry exists (same helper as `fuse init`).
- Connect-your-agent documentation covers manual registration for other MCP clients (Windsurf, Cline, Zed, and custom agents) over the same `fuse mcp serve` stdio server.

### Changed

- Consumer copy on fuse.codes, the README, and the docs index uses mechanism-first language for senior .NET developers and MCP authors (warm index, typed graph, verification grade) instead of two-beat marketing slogans.
- Docs and install help state that MCP read tools build `.fuse/fuse.db` on first use; `fuse index` or `fuse_workspace action=index` remain optional pre-warm steps before the agent's first turn.
- Product copy names Cursor, Claude Code, and Copilot as common MCP clients with auto-install, not as an exclusive list; any MCP-compatible client can run `fuse mcp serve`.
- The landing page hero is simplified: one demo, Connect and Quickstart CTAs, proof stats and install blocks moved below the fold.

### Fixed

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
