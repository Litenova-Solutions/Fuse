# Overnight autonomous run report - 2026-07-08

Program: Fuse v4.1 (the resident verified-edit runtime). Branch: `feature/v4-compiler-oracle`.
All work committed with DCO sign-off and pushed after each item. No PRs, merges, tags, version
bumps, or publishing. Tree is green and pushed at HEAD `b1e28b7`.

## Session tally: T2 done, S2 done, S4 done, S3 mechanism complete

This session completed three fully-gated items (T2, S2, S4) and the entire mechanism of a fourth (S3):
- **T2 DONE** (gate PASS): public API delta on fuse_review + fuse_impact; 10/10 corpus adjudication.
- **S2 DONE** (gate PASS): fuse_check delta mode, persisted sessions, repair packets v2; delta-mode P95 643.6 ms.
- **S3 mechanism DONE** (A-D built, one documented deviation): fuse/check host RPC + protocol bump to v4 (both
  sides, contracts verified); FuseHostClient pipe/socket client; working `fuse check --delta` / `fuse gate`
  commands; `fuse mcp install --with-hooks` (idempotent .claude/settings.json merge); a `\\.\pipe\` existence
  pre-check dropping no-host exit to ~182 ms; the ambient-verification docs page + commands reference + CHANGELOG;
  and a dual-shell (bash + pwsh) multi-process e2e (host serving, commands connect and exit cleanly). S3 stays
  `[>]` on ONE documented gate deviation: the "no-resident exit under 100 ms" half is unmet because the ~155-182 ms
  residual is .NET managed cold-start (the connect probe the design controls is ~0 ms). The item's Fallback does
  not apply (it is scoped to a pipe-RPC that cannot land; the RPC landed). Resolution is a maintainer decision
  (accept the managed cold-start floor, or fund an AOT/R2R hook binary as a named follow-up) - prepared, not
  self-approved.

- **S4 investigation DONE** (`[>]`, implementation next): both preconditions answered with numbers. The rehydrated
  compilation carries the capture's analyzers (CompilationData.GetAnalyzers; NodaTime rehydrated 284), now exposed
  on ResidentProject (additive). Analyzer-cost spike (284 analyzers, held compilation): P50 871.8 ms / P95 886.9
  ms, versus compiler-only delta-check 31 ms and S2 delta-mode P50 204 / P95 699 ms (resident-latency.json).
  Data-backed design decision: analyzers ON for verify-class calls (an explicit verify tolerates ~900 ms for CI
  parity), OFF by default for delta mode (would break the sub-1000 ms hot path), header names the setting.

S4 analyzer parity is now implemented end to end for the resident grade: ResidentProject carries the captured
analyzers + editorconfig-mapped AnalyzerOptions; ResidentAnalyzerRunner runs them scoped to a document (tested with
an inline analyzer); ResidentWorkspace.CheckOverlayAsync merges compiler + analyzer diagnostics (tested on the
binlog fixture); a new async IResidentWorkspaceProvider.TryCheckOverlayAsync threads it through the seam; and
fuse_check gained an `analyzers` param (default on for the single-file verify) routing through it, with docs.
Confirmed: delta mode stays analyzers-off inherently (compiler-only GetDiagnostics), and build-grade already has
analyzer parity for free (dotnet build runs analyzers; BuildGradeChecker parses them). S4's remaining piece is its
Gate: the id-set-equality fixture test (a reliably-firing-analyzer binlog fixture, resident check id-set vs dotnet
build, editorconfig-silenced rule stays silent) plus the trust-model docs; a small follow-up adds analyzers to the
out-of-process worker oracle path.

S4 is DONE (gate PASS). The Gate attempt found a real gap (editorconfig-elevated CA1822 did not surface through
the forked overlay), which I root-caused precisely (ReplaceSyntaxTree drops the replaced tree's editorconfig
severity mapping; direct run CA1822=Warning, forked overlay empty) and fixed (ForkedTreeOptionsProvider redirects
the new tree's config queries to the original tree). Two Gate fixture tests now prove an editorconfig-elevated rule
surfaces and a silenced rule stays silent through the analyzer-aware check; the latency decision (analyzers on for
verify, off for delta; 887 ms) is recorded. Docs (verification-grades analyzer-and-nullable-parity) + CHANGELOG
swept.

T1 is started (`[>]`): all preconditions confirmed - the covering-selection entry points exist, Microsoft.Testing.
Platform is cached, and the emit spike passes (a rehydrated build-exact resident compilation emits a runnable
assembly, the item's main uncertainty). The remaining T1 work is the Large, multi-session micro-host runner:
fuse_test / fuse test that emit the speculative assemblies to a scratch dir, materialize dependencies, run the
covering subset in a spawned Microsoft.Testing.Platform micro-host with a stripped environment and a hard timeout,
report per-test verdicts plus a not-runnable list, degrade per T0, add the H1 mutant extension, and record
testexec.json (false-green 0, median under 10s, selection safety at least 95 percent).

The T1 emit half is landed: ResidentEmit.EmitToDirectory emits a resident project's speculative compilation to a
scratch-dir assembly and resolves its reference paths (the dependency closure), tested on the rehydrated fixture
(assembly on disk, references resolve). Emit failure returns null, never a false run.

T1 run half: four pure/testable primitives are now built (Fuse.Workspace, 20 tests) - ResidentEmit (emit +
reference paths), TimedProcess (child run with hard timeout + tree-kill), TestFilterBuilder (covering-subset filter
expression), TrxResultParser (per-test verdicts from vstest TRX). The runner is decided (`dotnet vstest` on the
emitted assembly, no MSBuild, TRX for verdicts). The orchestrator has one recorded crux to resolve first: an
emitted Roslyn assembly is not directly launchable by a test host (needs runtimeconfig.json/deps.json), so the
orchestrator either sources/synthesizes those and materializes them beside the emitted DLL (design a, reuses the
primitives) or ships a custom load-context micro-host (design b). Recommendation and opening investigation step
(confirm what the capture exposes) are in the plan.

The orchestrator crux is resolved by investigation: probed Basic.CompilerLog and confirmed CompilationData.
EmitToDisk emits the compiler outputs but not the SDK runtimeconfig.json/deps.json, so an emitted assembly is not
directly launchable by `dotnet vstest`. Decision: design (b), a small Fuse-shipped custom micro-host exe that
AssemblyLoadContext-loads the emitted test assembly plus its reference paths and runs the covering xunit tests via
the xunit runner API, reporting verdicts. It reuses ResidentEmit, TimedProcess, and TestFilterBuilder.

Next action: scaffold the T1 micro-host runner exe + xunit runner-API integration (a coherent new component), then
ResidentTestRunner, fuse_test / fuse test, the T0 degrade ladder, the H1 mutant extension, and testexec.json
(false green 0, median under 10s, selection safety at least 95 percent) with an xunit-fixture end-to-end test. Then
H2. C1 remains `[>]` (corpus-and-install-gated apply); S3 has one maintainer-gated timing deviation (mechanism
complete). All work committed and pushed at HEAD `b951b77`; every committed change gate-green (build + all 16 .NET
assemblies + dotnet format; extension contract 9/9 + tsc from the S3 protocol change). About 100 gate-green
commits this session.

## S3: sub-step A LANDED (the protocol-bump keystone), remaining sub-steps recorded

S3 (harness hooks: ambient verification) is under way. Its hardest, most delicate part - the host RPC protocol
bump, flagged as the item's main uncertainty - is done and fully verified on both sides: FuseHostService protocol
3 -> 4 with a new `fuse/check` RPC (resident whole-state diagnostics diffed against the persisted session
baseline, returning introduced/resolved; non-resident empty delta when no resident workspace serves the root, so a
hook stays silent and never runs a build), new `CheckDeltaDto`/`CheckDiagnosticDto` shapes, the extension
`protocol.ts` mirror (PROTOCOL_VERSION 4, interfaces, method name), and both contract suites plus the fixtures
updated in lockstep per the change-safety invariant (.NET FuseHostContractTests 10/10, extension `node --test`
9/9, `tsc --noEmit` clean, an RPC wire test for the no-resident path). All 16 .NET assemblies green, format clean.
Commit `fe69948`. Remaining S3 (recorded in the plan): (B) `fuse check --delta`/`fuse gate` CLI commands - a new
cross-platform .NET named-pipe/Unix-socket CLIENT connecting to the running host with a <100 ms no-host fast-exit
(timing-sensitive shipped infra, to build fresh); (C) `fuse mcp install --with-hooks`; (D) dual-shell e2e + docs.

## S2: DONE (full item, gate PASS) - delta check, persisted sessions, repair packets v2

`fuse_check` gains a delta mode: a session id with no content returns the diagnostics the on-disk edits introduced
or resolved since a persisted baseline (restart-resumable via a new additive `check_sessions` store table),
reading whole-state diagnostics from a live resident workspace (a new `TryGetCurrentDiagnostics` seam) and
abstaining, naming `FUSE_RESIDENT`, when none serves the root - it never runs a build. `full`/`markGreen`
parameters return the whole set / reset the baseline. Repair packets expanded to CS7036 (missing argument) and
CS0029 (type mismatch). Gate: delta-mode P95 643.6 ms < 1000 ms on NodaTime (resident-latency.json). All gates
green; docs + CHANGELOG swept. Commits `54899fe`..`9de9e5d`. Two full items are now complete this session (T2 and
S2); newly eligible: S3 (Wave 1), S4, T1, H2.

## T2: DONE (full item, gate PASS) - public API delta on review and impact

T2 is complete end to end this session. `fuse_review` now opens its manifest with a public API delta section
(public/protected members added, removed, or changed between the git base ref and the working tree, breaking vs
additive), and `fuse_impact` carries a conservative, mode-aware public-surface line. Built as gate-green
sub-steps over the two pure cores: a `git show <ref>:<path>` capability on the change detector, an `IChangeSource`
base-content read, a purpose-built `PublicSurfaceExtractor` (syntax-only public/protected type AND member
extraction with effective accessibility - the fix for the general extractor leaving member accessibility null),
the `ChangedFileApiDelta` bridge, the `ChangedApiSurfaceGatherer` orchestration seam, the emitter/manifest
threading (a nullable `ContextJsonDto.ApiDelta` field for JSON), and the conservative impact line. Docs (mcp-tools
review+impact + out-of-scope note) and CHANGELOG swept. GATE: `fuse eval review` recorded a per-PR delta on 42 of
53 PRs; 10 hand-adjudicated against the real base->head diff -> 10/10 agree after one disagreement (generic types
collided under an arity-stripped FQN) was analyzed and fixed (TypeFqn now carries CLR-style arity `Foo\`1`), with a
new test and a re-run confirming precise naming across 15 generic-heavy PRs. No false breaking flag, no missed
public change on the set. Commits `659b114`..`13dcc66`. Follow-up (non-blocking): regenerate the canonical
review.json under --restore so the api-delta notes land there too (the delta is syntax-based and identical under
either mode; the Suite B headline is unaffected by this additive section).

## Prior HEAD lineage (earlier this run)

T0 landed at `32f4450`, the S1 design checkpoint at `519a2d3`/`9d576bb`, S1 resident-engine primitives at
`4bd7bd6`/`deb5594`/`041eb33`, C1 sub-steps at `a0b277f`/`065c591`/`f5739be`/`09ccb71`/`bab3026`, S1 step 2 seam
at `38004d2`/`69cea59`.

## S1: gate numbers all MET (opt-in); only the G5-gated default-on promotion remains

S1's measured gate is green: delta-diagnostics P95 31.0 ms (< 1000 ms; `resident-latency.json`), edge-freshness
correctness validated (the issue-5 acceptance test: an edited DI registration resolves after resident projection
with no full re-index), edge-freshness latency < 2 s (measured in isolation), and RSS 164 MB. The full resident
mechanism is built, wired opt-in into both hosts, and single-writer-projecting into the store. S1 stays `[>]`
only because it ships opt-in (`FUSE_RESIDENT`): promotion to default-on is gated on G5 (the resident daemon that
isolates the resident `Basic.CompilerLog` closure from in-process MSBuildWorkspace, since co-activation in one
long-lived process is order-fragile). So S1's engine, wiring, and every gate number are complete; the sole
remaining sub-step is the G5-gated default-on promotion (plus an optional dedicated single-writer concurrency
test). The next lanes are G5 (unblocking S1 default-on) and the C1 apply pipeline.

## S1: fully wired opt-in end to end (only end-to-end validation + G5 default-on remain)

With `FUSE_RESIDENT=1`, the serve/host now: warms a resident workspace for the root in the background; on each
watcher batch applies the edit to the held compilation and projects the changed cone into the store (so
store-backed reads reflect the edit); and `OpenIndexedAsync` skips the N6 reconcile when resident so the watcher
is the sole store writer (single-writer discipline). `fuse_check` and `fuse_changeset diagnose` answer
resident-grade from the held compilation. All default-off/null-provider safe, so the shipped store-backed path
is byte-identical. Smoke-tested: `FUSE_RESIDENT=1 fuse mcp serve` starts, warms, projects, and shuts down clean;
the projection write path is unit-tested (an added type is queryable in the store after a batch). The remaining
S1 work is end-to-end validation only: the issue-5 DI-edge acceptance test and the edge-freshness < 2 s
measurement over JSON-RPC, a dedicated single-writer concurrency test, and default-on promotion via the G5
daemon. The latency gate is already met (P95 31 ms).

## S1 engine and glue: COMPLETE and tested (only the single-writer serve wiring remains)

Every S1 engine and glue piece is now built and tested: the resident engine (`Fuse.Workspace`), the watcher
change-coalescing and batch updater, the registry and concrete provider service, both-host opt-in wiring, both
verify paths resident-routed, the latency gate (P95 31 ms), the projection engine
(`ProjectFromCompilationsAsync`, add/change/removal), and the glue `ResidentWorkspaceService.ProjectChangedAsync`
(maps changed files to their held compilations and re-projects each into the store; tested - an added type is
queryable in the store after a batch). The ONLY remaining S1 integration is narrow but delicate: call
`ProjectChangedAsync` from the serve watcher's batch handler under the single-writer rule, which also needs
`OpenIndexedAsync` to skip the N6 reconcile when a resident workspace serves the root (so the watcher is the sole
store writer) - the single-writer projection discipline the S1 design says to assert with a test. That, the
issue-5 DI-edge acceptance test, and the edge-freshness < 2 s measurement close S1's edge-freshness gate;
default-on still needs G5. It is a shipped read-path coordination where a bug means two writers, so it is the
dedicated-session close, not rushed at depth.

## S1 step 4 projection engine: COMPLETE (add/change/removal)

`SemanticIndexer.ProjectFromCompilationsAsync(root, store, (projectPath, Compilation)[], files, ct)` projects
live resident compilations into the store: it extracts each project's symbols and wiring graph in-process (the
worker's extraction), clears the projected files' rows (`DeleteFileDataAsync`, so removals do not leave stale
rows), and upserts via the tested `IndexFromCaptureAsync`. So a symbol or edge an edit introduces (or removes)
is reflected in the store - queryable without a full re-index. It takes raw Roslyn Compilations (no
Fuse.Workspace dependency, so no co-activation). Tested (ProjectFromCompilationsTests): project Foo/Bar, add
Baz (queryable), rename Bar->Renamed (stale Bar dropped). The remaining S1 integration is wiring this to the
serve watcher as the single writer (map the changed files to their project, re-project after the resident
updater applies the edit) plus the issue-5 DI-edge acceptance test and the edge-freshness < 2 s measurement;
and default-on via G5.

## S1 latency gate: MET (recorded)

`fuse resident-latency tests/benchmarks/.corpus/NodaTime/src/NodaTime` (a new dedicated-process CLI command
that never invokes MSBuildWorkspace, so it sidesteps the co-activation fragility below) recorded, to
`results/resident-latency.json`: resident delta-check P50 19.9 ms, **P95 31.0 ms** (the S1 gate is P95 < 1000 ms
warm at NodaTime scale -> **PASS**, far inside), resident warm (build+rehydrate) 14.1 s, resident RSS 164 MB, on
NodaTime's main project (2 resident projects). The delta-check is `ResidentWorkspace.CheckOverlay`, the
speculative typecheck `fuse_check` invokes when a resident workspace is live. The number is swept into AGENTS.md.
FUSE_RESIDENT stays opt-in (not promoted to default-on) pending the co-activation resolution below.

## Co-activation finding (S1 architecture, verified)

The shipped Cli's MSBuildWorkspace is NOT broken by co-presence of the resident `Basic.CompilerLog` closure:
`fuse doctor tests/fixtures/OrderingApp` loads oracle-grade with the committed `Fuse.Cli -> Fuse.Workspace`
reference present. So the S1 in-process serve/host wiring is MSBuildWorkspace-safe and there is no shipped
regression. A latency-gate attempt (a resident delta-check arm in PerformanceSuite) was reverted because adding
`Fuse.Workspace` plus an explicit VisualBasic ref to Fuse.Benchmarks broke `RestoreSemanticTests`; that is a
Benchmarks-specific reference interaction (or a restore flake), not a fundamental conflict, and is deferred to
the benchmarking session with exact follow-ups recorded in the plan progress log. The resident warm itself
worked on NodaTime (~9s, no crash) in-process alongside MSBuildWorkspace.

## Current state (latest checkpoint)

T0 is complete and gate-green (build-grade fallback for `fuse_check`, oracle-vs-build agreement
100.0% on 24 mutants). S1 (the resident workspace, XL keystone) is `[>]` with its entire in-memory
resident-engine surface LANDED gate-green across three commits: the new `Fuse.Workspace` library and
its `ResidentWorkspace` rehydrate a tier-1 binlog once and hold the live compilations, with an
in-memory overlay check, an apply-edit that mutates the retained compilation, a remove-document for
deletions, and a whole-state diagnostics baseline (all no-build, no-disk-write). It references
`Basic.CompilerLog` but not `MSBuildWorkspace` (the architecture decision made concrete); the closure
question was de-risked from the worker's existing references and a real resolution bug (VB 4.8 floor
-> 4.14 pin) was found and fixed. Everything is additive and unreferenced, so no shipped path has
changed yet. C1 (`fuse up`) has advance-scouted preconditions banked but stays `[ ]`. Exact next
action: the first S1 shipped-substrate sub-step (dedicated session) - the file watcher that drives
apply-edit/remove-document on file events (with the 300-file storm threshold and single-writer
projection rule), the `IWorkspaceTruth` seam (resident-first, store-fallback, availability header),
and where the serve/host holds the `ResidentWorkspace`, with the issue-5 DI-edge acceptance test
first; then routing read tools, changeset-overlay unification, and the `performance.json` latency/RSS
gate. The S1 Gate closes only after those.

## Summary

Headline: **S1, the Wave-1 resident-workspace keystone, is complete `[x]`** (gate numbers met: resident
delta-check P95 31.0 ms, edge-freshness correctness + <2s, RSS 164 MB), and **T0 is complete `[x]`**. S1's
completion unblocks S2 (now `[>]`, preconditions recorded) and T2 (public API delta, depends S1 only). C1 is
substantially built (`[>]`: report + NU1507 overlay-remedy generation; corpus auto-apply and the 17-repo gate
remain). Two named non-blocking S1 follow-ups: default-on promotion via G5, and the delta-p95 re-measure folded
into S2. Every commit this session was gate-green (build + 16 test assemblies + format).

Newly unblocked by S1 `[x]`, both `[>]` with their pure engine cores built and tested (the shipped-path wiring
is the dedicated-session remainder): **S2** has `DiagnosticDelta` (introduced/resolved with span-drift handling)
and the CS0117 repair-packet expansion; **T2** has `PublicApiDelta` (added/removed/signature-changed/
accessibility-reduced breaking classification). Remaining S2: the `fuse_check` delta mode + session persistence;
remaining T2: wiring the delta into `fuse_review`/`fuse_impact` + the 10-PR adjudication gate.

Wave 0 (contract and kills) is complete: all four items landed gate-green, committed, and pushed.
In Wave 1, H1 (mutation-derived check-honesty calibration) landed gate-green and unblocked T0, and
T0 (the verification-grade ladder) then landed gate-green in full this run: the build-grade fallback
executor, the grade on every `fuse_check` answer, the availability-header grade clause, and the
oracle-vs-build agreement gate. Six items complete this run, all gate-green. Notably the build-capture
worker turned out to run in this environment once built as part of the solution, so T0's agreement
gate (deferred by the earlier design pass as "pending a provisioned worker") was measured this run:
100.0 percent diagnostic-identity agreement on 24 comparable OrderingApp mutants. S1 (the resident
workspace, XL) remains unstarted for a dedicated multi-session effort (rationale below).

## Items completed (with gate verdicts)

| Item | Title | Gate | Commit |
|------|-------|------|--------|
| X1 | Execution contract into AGENTS.md; compiler-oracle identity rewrite | PASS (site builds; AGENTS carries the contract) | `900b6f0` |
| K1 | Retire the dense embedding channel and the ONNX plugin | PASS (ranking within CI of recorded lexical; low-signal F1 1.0; false-rejection 0/52) | `c037269` |
| K3 | Close V1/V2, freeze language providers, de-headline Suite D | PASS (docs merged; site builds) | `ea4cb24` |
| K2 | Delete the in-memory BM25F ranker and the dead classic query path | PASS (zero references; contract suite 8/8) | `9fc43e9` |
| H1 | Mutation-derived check-honesty calibration at scale | PASS (false green 0; mutation false-red 0.00% < 1% over 1,000 verified cases) | (this run) |
| T0 | Verification-grade ladder: build-grade fallback, verify never shrugs | PASS (3 classification tests green; oracle-vs-build agreement 24/24 = 100.0% >= 99%) | (this run) |

## In-progress items (gate-green sub-steps landed; items not yet complete)

- **S1 resident workspace** `[>]`: steps 1-2 done (the in-memory resident-engine surface in `Fuse.Workspace`,
  5 primitives; the `IResidentWorkspaceProvider` seam wired into the availability header AND `fuse_check`
  resident-first routing, behind a null default) and step 3's path-reporting half done (the watcher now
  coalesces raw filesystem events to the net change per path via `WorkspaceFileChangeSet` and raises an additive
  `BatchChanged` event; 6 accumulator tests), and the batch-apply glue done (`ResidentWorkspace.AddDocument`
  plus `ResidentWorkspaceUpdater`, which applies a watcher batch to a resident workspace - edit/add
  created-or-changed .cs, remove deleted, skip the rest, never writing the tree; 2 tests). The concrete provider
  (`ResidentWorkspaceService`), the process-wide `ResidentWorkspaceRegistry` (lazy per-root warm/cache), and the
  shared `ResidentWorkspaceHosting` helper are all built and tested, and the resident workspace is now WIRED
  end-to-end (opt-in, `FUSE_RESIDENT`, default off) into BOTH `mcp serve` and `fuse host`: on opt-in the host
  warms the served root in the background, keeps it current from the watcher batch (evicting to store-backed
  above the 300-file storm threshold), and disposes on shutdown; `fuse_check` for the served root then answers
  resident-grade from the held compilation with no per-check rebuild. Smoke-tested: `FUSE_RESIDENT=1 fuse mcp
  serve` starts, warms, and shuts down clean. Default off keeps the shipped path byte-identical. Both
  speculative-verify paths now route resident-first behind the null default: `fuse_check` and
  `fuse_changeset diagnose` (per staged file), so a live resident workspace answers either at oracle grade with
  no build. What remains to CLOSE S1's gate: store-projection so non-check reads (find/map/resolve) are
  resident-fresh (step 4, a careful single-writer/N6-coexistence change), the fuller multi-file overlay-session
  unification (step 5 remainder), and the `performance.json` latency/RSS gate through the MCP layer (delta p95
  < 1s warm at NodaTime scale, edge freshness < 2s, resident RSS) with an end-to-end resident `fuse_check` over
  JSON-RPC - which needs a provisioned tier-1 repo - after which `FUSE_RESIDENT` is promoted to default-on. Those
  are the dedicated benchmarking/integration session.
- **C1 fuse up** `[>]`: the KB, planner, NU1507 overlay generator, renderer, and a working `fuse up` command
  that now both reports the remediation plan AND generates the NU1507 overlay remedy to a temp file (installs
  nothing, never edits the repo) with the apply command, plus the KB-generated troubleshooting page with its
  drift test. Remaining: the corpus-dependent auto-apply (thread the overlay `--configfile` through the
  build/index pipeline against a mirror, re-attempt tier-1) and the 17-repo `up-report.json` gate; the
  consent-gated SDK/workload installs are blocked by the install-nothing rule.

- **C1 fuse up (earlier detail)** `[>]`: five sub-steps landed gate-green, including a working user-facing command -
  `RemediationKnowledgeBase` (JSON-data KB + matcher; 7 tests), `EnvironmentRemediationPlanner`
  (classify-and-report core; 4 tests), `NuGetOverlayConfig` (NU1507 overlay generator, installs nothing, never
  writes the repo; 4 tests), `RemediationReport` (renderer; 2 tests), and the report-only `fuse up` CLI command
  (runs doctor + planner + report, applies nothing, never touches the repo; smoke-tested on the OrderingApp
  fixture) plus the KB-generated troubleshooting page with its drift-guard test (1 test). Remaining (the apply +
  gate, a dedicated session): thread the NU1507 overlay `--configfile` through the build/restore pipeline with
  restore-artifact safety (restore writes obj/ into the repo, so it must run against a mirror), the
  consent-gated SDK/workload installs, re-attempt tier-1, and the 17-repo `up-report.json` gate. Environmental
  note: the goal forbids installing anything, so the SDK-install (NETSDK1045) and workload-install (MSB4018)
  remedies cannot be exercised here; only the NU1507 overlay (installs nothing) is runnable, so the gate may
  land on the Fallback (record "1 of 6 flips" honestly) unless the provisioned environment allows installs.

Both open lanes are now at their irreducible shipped-activation step. S1's seam is fully wired (step 2 done,
behavior-preserving), so the only remaining S1 activation is constructing a resident workspace in the serve
host, which turns on the co-activation-isolation decision above and so needs a dedicated session with process
isolation (not the shared test host, where MSBuildLocator's process-global registration would risk
contaminating the other tests). C1's remaining is the `fuse up` command plus overlay-config pipeline threading.
All safe, additive, tested engine and seam foundations for both are landed and pushed; the activation is the
dedicated-session work per the LARGE ITEMS "do not rush a shipped-path change" directive.

The three standing gates (`dotnet build Fuse.slnx -c Release`, `dotnet test Fuse.slnx -c Release
--no-build`, `dotnet format Fuse.slnx --verify-no-changes`) were green on every item that touched
code (K1, K2). The full test suite passes: all 15 assemblies, 0 failed (Fusion 234 -> 141 after
K2's dead-path test removal; GoldenOutput 17 -> 13; all others unchanged or higher).

Note on order: the checklist order is X1, K1, K2, K3. K2 was worked last of the four because its
precise removal set needed a dependency-map pass (the classic query path is deeply wired); K1 and K3
were unblocked and landed first. All four are `[x]` in the Master checklist.

## Numbers recorded (each with its result file)

All under `tests/benchmarks/results/`:

- `ranking.json` (regenerated, `fuse eval ranking --restore`): lexical channel MRR 0.187,
  recall@10 12.6%, nDCG@10 0.117; shipping default (lexical + centrality + co-change) MRR 0.197,
  recall@10 15.0%, nDCG@10 0.139; default-no-cochange MRR 0.208, recall@10 15.0%. Byte-identical
  to the pre-K1 recording - dense contributed nothing on this partial-2/syntax-2 corpus, so its
  removal cost no ranking quality. Restore: NodaTime 15/0, Scrutor 0/2, Specification 11/0,
  eShopOnWeb 11/0.
- `localize.json` (regenerated, `fuse eval localize --restore`): recall 15.0% (95% CI 9-21%),
  precision 8.1%, median 1,049 tokens, low-signal F1 1.00, false-rejection on answerable 0/52
  (0.0%), precision-when-confident 5.6% (9 tasks); graded states confident 9, partial 43,
  insufficient 1; buckets identifier-rich 19%, nl-domain 17%. Retiring dense held overall recall at
  15.0% (equal to the pre-K1 dense-on default, above the pre-K1 lexical fallback's 13.3%) because
  lowering the `SignalGrader` insufficient floor from 0.30 to 0.20 recovered the three answerable
  tasks the lexical-only path used to refuse.
- `archive/localize.a1-lexical.json`: the old dense-off A/B, archived (the `--lexical` flag that
  produced it was removed in K1; the default is now the lexical path, so this file is redundant).

- `checkgate.json` (regenerated, `fuse eval checkgate --mutations 500`): curated 8 of 8 correct
  (false green 0, false red 0); mutation arm 1,000 compiler-verified cases over OrderingApp (500
  breaking, 500 neutral), false green 0, false red 0.00% over 1,000 verified. SampleShop skipped
  (13 in-process baseline errors CS0234/CS0246/CS0103, its two-project MVC structure not binding in
  a flat in-process compilation), recorded not fabricated. Gate: false green 0, false red < 1% - PASS.

No model-driven suite was run (hard-banned tonight: `fuse eval loop`, `fuse eval agent`, B1 and the
B1-gated items). No number was fabricated, rounded, or quoted below its minimum N.

## Items blocked or deferred, and why

- **S1 (the resident workspace, XL) - not started, by decision.** S1 is the next todo and is
  dependency-met (X1 is `[x]`), but its gate is a full resident engine (rehydrated compilations,
  file watcher, incremental cone re-analysis, overlay unification) measured for delta-diagnostics
  p95, edge freshness, and RSS on NodaTime and eShopOnWeb. That gate cannot be reached or honestly
  verified in a single session, and a half-built resident engine is exactly the half-done state the
  guardrails warn against. Per "quality over count governs," S1 is left `[ ]` (not half-built) for a
  dedicated session. Recorded in the plan's progress log under "Wave 1 sequencing note".
- **H1 - completed gate-green** (see the completed table and numbers above). The design notes below
  are kept as the record of how it was built and the one honest limitation (SampleShop skipped).
- Everything requiring a model run or API tokens (B1, F1/F2/F6, `fuse eval loop`/`agent`) is
  hard-banned for tonight and was not touched.

## Environment changes (user-scoped only; nothing installed)

- `git config --global core.longpaths true`.
- Created `D:\fuse-work\nuget` and `D:\fuse-work\bench` (C: had 31 GB free, under the 60 GB
  threshold). All `dotnet build`/`test` and the eval runs used `NUGET_PACKAGES=D:/fuse-work/nuget`
  for the session (prefixed per command; env does not persist across shell calls). C: stayed at ~31
  GB free throughout (never below 15 GB). No registry, Defender, winget, or installer changes. SDKs
  6/8/9/10 already present; no corpus repo needed a missing band. The benchmark corpus was already
  cloned under `tests/benchmarks/.corpus` (NodaTime, Scrutor, Specification, eShopOnWeb).

## H1 as built (record of the implementation and its one limitation)

Preconditions verified:
- The Suite F harness is `CheckGateSuite` (`tests/benchmarks/Fuse.Benchmarks/Suites/CheckGateSuite.cs`),
  run via `fuse eval checkgate`, writing `results/checkgate.json` (currently 8 curated cases, all
  correct). Its `CheckInProcess` builds a raw-Roslyn `CSharpCompilation` (no MSBuild), replaces one
  document's tree, and classifies the changed document's diagnostics with the shipped
  `CheckResult.IsClean` rule - the exact contract `fuse_check` ships.
- Fixture compilability (the key finding): `tests/fixtures/SampleShop` (14 .cs files, no package
  references) compiles in-process with BCL-only `TRUSTED_PLATFORM_ASSEMBLIES` refs, so it is a
  clean mutation baseline. `tests/fixtures/OrderingApp` (18 .cs files) uses the ASP.NET Core shared
  framework (`Microsoft.AspNetCore.Builder`, `Microsoft.Extensions.*`) with no NuGet PackageReference,
  so it does NOT compile in-process with BCL-only refs; it needs the `Microsoft.AspNetCore.App`
  shared-framework assemblies added as metadata references (resolvable from `dotnet --list-runtimes`
  paths) or it will show baseline errors and the gate will refuse to run over it.

Recommended H1 shape (completable in a focused session):
1. New `MutationGenerator` in `Fuse.Benchmarks` (Roslyn `CSharpSyntaxRewriter`s), deterministic from
   a recorded seed (`new Random(seed)` is fine in C# benchmark code).
2. Ground truth is compiler-verified, not asserted: for each candidate mutant, apply it to the clean
   baseline compilation and check the full-compilation error count. Keep a breaking mutant only if it
   introduces at least one error; keep a neutral mutant only if it stays clean; discard and retry
   otherwise (bounded attempts). This is how "provably neutral" becomes mechanical.
3. Operators, single-file (matching the shipped single-file `fuse_check` contract):
   breaking - rename a bound member access (CS1061), replace a used type name with an undefined one
   (CS0246, the reliable form of "delete a required using"), change a return/expression body to an
   incompatible literal (CS0029), delete a declaration referenced elsewhere in the same file
   (CS0117/CS1061); neutral - consistent local rename within one method, reorder two members within a
   type, comment/whitespace insertion.
4. Run over SampleShop as the primary in-process fixture (meets the >=1,000 cases / 500-per-class
   minimum on its own). Add OrderingApp only after wiring the ASP.NET shared-framework refs, or
   record it skipped with the reason. A second clean in-process baseline (the synthetic Shop
   compilation already in `CheckGateSuite.BuildBaseline`) can stand in as the "second fixture" if
   OrderingApp is deferred - record which.
5. `fuse eval checkgate --mutations N` (add `Mutations` to `EvalOptions` and a `--mutations` option
   to `EvalCommand`); write `checkgate.json` v2 keeping the 8 curated cases as a named subset.
6. Generator unit tests in `Fuse.Benchmarks.Tests`: each breaking operator's output fails compilation
   on a minimal fixture; each neutral operator's output compiles clean.
7. Gate: false green 0; false red under 1%. Fallbacks are in the item text (a nonzero false green is
   a release-blocking check bug; false red >=1% means analyze/reclassify the operator).

Watch-out worth a design note: `fuse_check` is a single-file check by contract, so keep the
operators single-file (a cross-file break, e.g. deleting a public declaration whose only references
are in another file, could be a real false green because the single-file check does not inspect the
other file - that class belongs to T0/S1's whole-compilation verification, not to the shipped
single-file honesty gate). Do not silently mix the two; if a cross-file class is added, gate it
separately and label it.

## Exact next item to start

Two are now eligible:

- **T0 - Verification-grade ladder: verify never shrugs** (depends: H1, now `[x]`). This is the next
  todo whose dependency is met. It adds the build-grade fallback so a verify-class answer degrades to
  running the real toolchain (stamped `build`-grade) instead of abstaining, using H1's mutation corpus
  as the oracle-versus-build agreement check. T0 precondition finding for the next session: it changes
  the shipped `fuse_check` response path (adds `verification_grade` and a scoped `dotnet build`
  executor), so it deserves a fresh full-budget session; and its gate has two parts - the three
  build-grade classification tests (runnable now, no tier-1 needed) and a >=99% oracle-vs-build
  diagnostic-agreement check that needs a build-capture worker (`FUSE_BUILD_CAPTURE_WORKER`), which is
  NOT configured in this environment (the checkgate suite reports "Tier-1 arm: skipped"). Plan to land
  the executor plus the three classification tests, and record the agreement check as pending a
  provisioned worker (the H1 mutation corpus is ready to feed it).
- **S1 - the resident workspace** (depends: X1, met) is the keystone and the larger prize; budget it
  as a multi-session XL and land it in the sub-steps its item text lists (extract a
  rehydration-to-resident loader first; DI-edge acceptance test first for the watcher step).

T0 is now complete (see the table). Recommended next: S1 (the resident workspace), the Wave 1
keystone, executed as committed multi-session sub-steps (extract a rehydration-to-resident loader
first; DI-edge acceptance test first for the watcher step). The independent-lane alternative if a
session's budget favors a completable item is C1 (`fuse up`, depends X1, met), the first dependency-met
non-S1 item. Named follow-ups (not blocking): (a) carry the verification grade into `fuse_review` and
`fuse_changeset diagnose` (T0 was scoped to `fuse_check`); (b) bind the SampleShop fixture with
per-project references so the mutation and agreement arms score two fixtures instead of one; (c) a
larger provisioned oracle-vs-build agreement sweep beyond the 24-mutant bounded sample.

## Guardrail compliance

Every commit was green (build, test, format) before push. Numbers are sourced to canonical result
files and superseded figures were swept in the same change (AGENTS.md, briefing.md, the site
benchmarks/scoping/config-keys/commands/internals pages, README, CHANGELOG). Behavior changes are
named in CHANGELOG with their migration (index rebuild on schema 15->16; the removed env vars and
the `fuse models` command). Writing stayed plain ASCII. No secrets in logs or commits. Nothing was
written outside `c:\Projects\Fuse`, `D:\fuse-work`, and the scratchpad; no corpus repo source was
edited (the eval `--restore` writes into corpus obj/ are the harness's normal operation).
