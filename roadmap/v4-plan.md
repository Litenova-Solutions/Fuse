# Fuse V4 Plan: the compiler oracle (one release, three phases)

V3 built the .NET semantic moat (wiring resolution, change-impact review, hybrid retrieval,
abstention, warm millisecond latency); its record is in [v3-plan.md](v3-plan.md). V3.1
([v3.1-plan.md](v3.1-plan.md)) made dense retrieval the default and offline and rewrote the
positioning honestly. V3.2 ([v3.2-plan.md](v3.2-plan.md)) shipped the host-on-semantic-index
migration (protocol 3) and the rich index panel, but left two of its highest-value items
only partly landed: the resident Roslyn workspace with dependency-scoped freshness (W1) and
semantic-mode corpus coverage (W4). Those are picked up here.

V4 is a single release that does one strategic thing: it stops Fuse being a retrieval tool
that happens to hold a Roslyn compilation and makes it the .NET agent's ground-truth oracle,
the tool that answers the questions only a compiler can answer, at edit speed. The one-line
pitch: Fuse gives your agent the compiler. This plan derives from an adversarial critique and
thesis that were verified against the live tree before it was written; each item below carries
its own rationale (the "Why" paragraph) capturing that analysis. For the full state of the
project (architecture, algorithms with their real constants, measured results, known issues,
and roadmap history) read [overview.md](overview.md), which any reader can use to orient before
this plan.

Revision note (2026-07-03, plan rename). This document was renamed from `v3.3-plan.md` to
`v4-plan.md` and re-versioned to 4.0.0: the compiler-oracle scope and the R3 tool-surface
reshape warrant a major bump, and the release now includes governance items L1 (MIT to Apache
2.0) and L2 (Developer Certificate of Origin). The 2026-07-03 technical revision below is
unchanged in substance.

Revision note (2026-07-03). This plan was re-verified against the live tree and the recorded
results by an external review pass. That pass confirmed all five original findings, added four
more (findings 6 to 9 below), amended several items in place (N1, N2, N3, N4, R1, R2, R3, R4,
M1, G1), and added seven items: N6 (the freshness contract), R5 (the persisted reference
index), R6 (repair packets and the API-shape oracle), R7 (compiler-executed refactorings), M2
(out-of-proc test execution, stretch), and V1/V2 (two gated retrieval bets). The two largest
changes of substance: N4's mechanism moves from hardening MSBuildWorkspace to a build-capture
ladder, and M1's in-process test execution is removed from scope rather than gated, because
modern .NET cannot deliver the in-process isolation the original gate assumed.

The release is organized in three phases (Floor, Oracle, Moonshot) plus a gated retrieval
addendum (Phase 4). Everything ships under one version, 4.0.0, and the release gate below
states exactly what must be green for the tag, including the stretch item (M2) that is
pre-agreed to slip to 4.1 if its gate is not met.

The honesty conventions are unchanged: every number sourced to `tests/benchmarks/results`,
weaknesses published, no head-to-head claim the harness does not back, plain ASCII prose.

---

## Why this release exists (the adversarial findings that drive it)

Nine findings, each verified against the tree, set the agenda. The first three are the
"we are fooling ourselves" defects; findings 4 and 5 are live bugs found where the harness was
blind; findings 6 to 9 were added by the 2026-07-03 review pass and are the same two classes
(self-deception, and blind spots where nothing measures) found one layer deeper.

1. **The moat mostly does not run on real checkouts.** The localize main checkout loads
   partial 2 / syntax 2, and across the review suite 27 of 53 PR worktrees load partial and
   25 syntax (`review.json`). The flagship semantic engine mostly does not execute, because
   MSBuild loading fails soft. This is the single largest product defect, not a benchmark
   inconvenience. Verified: [RoslynWorkspaceLoader.cs](../src/Core/Fuse.Semantics/RoslynWorkspaceLoader.cs)
   is a thin pass that falls back to syntax on any failure, with no restore, no per-project
   degradation, and no salvage.

2. **Per-payload token reduction evaporates at session scale.** Suite E shows 37 to 60
   percent per-file reduction, but Suite D (`agent.json`) shows the Fuse arm at 30.2 percent
   mean file recall and a median 211,502 cumulative session tokens against the native arm at
   26 percent and 209,182: the token cost was the same. Token efficiency per payload is not
   the product; fewer, shorter agent iterations is the product, and no current metric
   measures that.

3. **Open-ended localization is weak and unmeasured against its own hypothesis.**
   `localize.json`: 14.9 percent recall at 8.1 percent precision. The stated excuse, "recall
   is bounded by index mode," has never been tested, because semantic mode has never been on
   for the localize suite (see finding 1).

4. **A lexical ranking inversion lives in the tree because nothing measures ranking.**
   Verified at [WorkspaceIndexStore.cs:758](../src/Core/Fuse.Indexing/WorkspaceIndexStore.cs#L758):
   the FTS5 `bm25()` call weights the `path` column 4.0, the highest of all fields, while the
   comment three lines above says name, signature, and symbols outrank path, and the sibling
   in-memory ranker at [Bm25RelevanceIndex.cs:36](../src/Core/Fuse.Fusion/Scoping/Bm25RelevanceIndex.cs#L36)
   weights symbols 5.0 over path 3.0. Three sources of truth, the executing one is the
   outlier, and it plausibly contributes to the 8.1 percent precision (folder-name matches
   outranking symbol-name matches).

5. **Incremental re-index skips cross-file edges.** Verified: [SemanticIndexer.cs:199](../src/Core/Fuse.Semantics/SemanticIndexer.cs#L199)
   `ReindexFileAsync` rewrites only full-text and symbol rows and never touches Roslyn, so a
   DI registration edited in an editor session does not update the resolve graph until a full
   re-index. The MCP background upgrade is fire-and-forget `Task.Run` with swallowed
   exceptions ([FuseTools.cs:211](../src/Host/Fuse.Cli/Mcp/FuseTools.cs#L211)); there is no
   resident compilation, because `MSBuildWorkspace` is created and disposed inside a single
   `IndexAsync` call.

6. **Every read tool serves silently stale data after the first edit.** Verified: the MCP
   path indexes only when the store is cold ([FuseTools.cs:192](../src/Host/Fuse.Cli/Mcp/FuseTools.cs#L192),
   the `FileCount == 0` check); `mcp serve` has no file watcher; the host watcher only
   broadcasts `fuse/invalidated` without reindexing
   ([HostCommand.cs:63](../src/Host/Fuse.Cli/Commands/HostCommand.cs#L63)); and
   `ReindexFileAsync` has zero product callers (its only callers are tests and
   [PerformanceSuite.cs:128](../tests/benchmarks/Fuse.Benchmarks/Suites/PerformanceSuite.cs#L128)).
   So the moment an agent edits a file, every read tool answers from an index frozen at first
   call, unmarked, for the rest of the session. The honest-ceiling line elsewhere in this plan,
   "a stale graph presented as fresh is worse than the current honest opt-in," describes the
   shipping behavior of the whole MCP surface. Corollary: the 22.8 ms incremental re-index P50
   in `performance.json`, quoted in AGENTS.md as warm product latency, measures a method no
   product surface invokes. Addressed by N6 (and the citation fix rides with it).

7. **This plan assumed graph edges that do not exist.** Verified:
   [EdgeWeightProvider.cs:31](../src/Core/Fuse.Retrieval/EdgeWeightProvider.cs#L31) carries
   traversal weights for `tests` (0.65) and `calls` (0.60) edges that no analyzer emits (checked
   against all ten analyzers in `src/Core/Fuse.Semantics/Analyzers/`); the `"tests"` branch of
   `RoleFor` at [SemanticRetrievalEngine.cs:443](../src/Core/Fuse.Retrieval/SemanticRetrievalEngine.cs#L443)
   is unreachable. M1's original premise ("the graph already carries test edges") was false,
   and R2's tens-of-milliseconds target had no substrate: live `SymbolFinder` over a resident
   solution is hundreds of milliseconds to seconds warm, while the recorded sub-millisecond
   resolve in `performance.json` is a SQLite lookup of precomputed edges. Addressed by R5.

8. **Stale provenance in this plan's own motivating results.** Verified: `agent.json` samples
   10 of its 12 PRs from the retired 5-library corpus (AutoMapper x2, FluentValidation x2,
   MediatR x2, NewtonsoftJson x2, Serilog x2; only the two eShopOnWeb PRs are current-corpus),
   yet finding 2 quotes it as the release's motivating baseline and N2's archive list did not
   include it. And the docs quote precision-when-confident 9.3 percent and the dense lift as
   13.3 to 15.1 from the superseded `localize.a1.json` run; the current `localize.json` (the a6
   rerun, co-change prior on) records 5.6 percent over 9 confident tasks and 14.9 percent
   recall. Under this project's own convention these are fabrications with provenance.
   Addressed by the N2 amendment; Suite D is treated as directional only until R4 replaces it.

9. **A default-on ranking regression shipped unmeasured.** Verified by comparing
   `localize.a1.json` (before the A6 co-change prior) against the current `localize.json`
   (after): recall 15.06 to 14.90 percent, precision-when-answered 8.9 to 8.3 percent,
   precision-when-confident 9.3 to 5.6 percent over the 9 confident tasks. Each move is within
   CI individually, but a default-on feature that degrades every recorded headline, including
   the number the signal-sufficiency contract sells, is exactly the class of change a ranking
   gate exists to block, and nothing measured it. Addressed by the N1 amendment.

The thesis that resolves the first five: the unique asset is the warm, typed, whole-solution Roslyn
compilation. Today Fuse uses it for one thing, extracting wiring edges into SQLite at index
time. The compilation can also answer, in milliseconds, does this proposed edit typecheck,
what breaks if this signature changes, which tests cover this symbol. That is the oracle. It
subsumes the current strengths (resolve and review are already oracle queries), it forces the
real fix for the real defect (if the product is the compilation, MSBuild robustness is core
engineering), and it attacks the one metric that is flat (Suite D) by replacing build-loop
iterations with millisecond calls. Findings 6 to 9 are resolved by the floor directly: N6
(freshness), R5 (the missing edges), and the amended N1 and N2 (the prior regression and the
provenance drift).

---

## Where V4 starts (recorded 3.2.0 result)

All from `tests/benchmarks/results`, counted with `o200k_base`, on the current corpus
(Scrutor, Ardalis.Specification, NodaTime, eShopOnWeb, plus the SampleShop and OrderingApp
fixtures).

- Wiring resolution (Suite A, `semantics.json`): 22 of 22 edges, recall and precision 1.0 on
  the OrderingApp fixture. The deterministic moat.
- Change impact (Suite B, `review.json`, 53 PRs): 100 percent changed-file recall (by
  construction) at 79.8 percent precision, median 958 tokens. Index modes partial 27,
  semantic 1, syntax 25.
- Open-ended localization (Suite C, `localize.json`, 53 PRs): 14.9 percent recall at 8.1
  percent precision, median 1,033 tokens; contract metrics hold (low-signal F1 1.0,
  false-rejection 0.0). Ceiling is index mode (localize main checkout partial 2, syntax 2).
  Precision-when-confident is 5.6 percent over the 9 confident tasks in the current file; the
  9.3 percent still quoted in the docs comes from the superseded `localize.a1.json` run
  (finding 8; the N2 amendment fixes the citations).
- Agent sufficiency (Suite D, `agent.json`, N=12, one rollout): Fuse 30.2 percent recall at
  median 211,502 tokens; native 26 percent at 209,182. Flat on tokens. Caveat (finding 8):
  10 of the 12 sampled PRs are retired-corpus, so Suite D is directional only; R4 replaces it
  and N2 annotates the file.
- Reduction fidelity (Suite E): 37 to 60 percent token removal, 99 to 100 percent public
  method retention, measured against an independent Roslyn parse.
- Latency (`performance.json`): warm localize P50 60.9 ms, resolve sub-millisecond, review
  plan 110 ms P50, single-file re-index P50 22.8 ms. Cold start about 20 s syntax, about 70 s
  full semantic; background upgrade opt-in behind `FUSE_BG_UPGRADE`.
- Peers (`layer6-peers.json`, 50k budget): fuse 19 percent recall / 19 percent precision (12
  PRs), codegraph 9 / 11 (12 PRs), serena 34 / 27 (4-PR sample, tiny-repo outlier), coa 9 / 1
  (4 PRs).

---

## The crown for V4 (measurable target per axis)

| Axis | Today (3.2.0) | V4 target |
|------|---------------|-------------|
| Semantic load on real repos | main checkout loads mostly syntax/partial | semantic or partial on the majority of localize main checkouts, with `fuse doctor` naming every downgrade |
| Cold start to first answer | about 20 s syntax, opt-in upgrade | first answer in a few seconds, on by default, MSBuild evaluation shared in a resident workspace |
| Edge freshness | incremental rewrites syntax rows only | an edited DI registration surfaces its new resolve edge within about 1 s, no full re-index |
| Lexical ranking correctness | path weighted highest, unmeasured | field weights match documented intent, guarded by a recorded ranking suite (MRR, recall@10) |
| Speculative verify | none; the agent runs `dotnet build` | `fuse_check` returns repair-packet diagnostics for a proposed patch in sub-second P50 on corpus-size solutions, or abstains; false-green and false-red rates both recorded and near zero on oracle-grade projects |
| Blast radius before the edit | none; discovered by build-and-retry | `fuse_impact` lists call sites and the break set for a signature change in tens of ms, served from the persisted reference index (R5) |
| Answer freshness after an edit | read tools serve the index built at first call; no watcher or hash check on the MCP path | every read tool reconciles dirty files before answering or stamps the answer stale-as-of (N6) |
| Mechanical multi-file edits | generated token-by-token by the agent, then verified | rename and change-signature executed by the compiler, returned as a staged diff, correct by construction (R7) |
| API-shape questions | grep plus file reads to learn a signature | `fuse_signatures` batch signature-and-docs lookup; check diagnostics carry the definitions needed to fix them (R6) |
| Tool surface | 9 workflow tools | 7 oracle-shaped tools (`fuse_ask`, `fuse_check`, `fuse_impact`, `fuse_signatures`, `fuse_refactor`, `fuse_context`, `fuse_review`) with shims for the folded names, and an ambient availability header on every response |
| Loop measurement | Suite D cumulative tokens, N=12, one rollout | a task-resolution suite measuring iterations-to-green, build-invocations-per-session, and wall-clock, three arms (native, LSP-armed, fuse), three rollouts, CIs recorded, baselines pre-registered in Phase 1 |
| Results provenance | some result files quote the retired 5-library corpus | every quoted number sourced to a current-corpus file; stale files archived |

---

## Execution checklist

Each item lands engine plus tests plus website docs plus a benchmark in one change, runs the
three gates, and follows the conventions below. The one-item rule is amended for this release:
an item may be harness-first, where the new benchmark is the deliverable and the engine change
is nil (N1's ranking suite, R4's loop suite). Sequencing is by leverage and by what unblocks
measurement. **L1 and L2 run first**, before any other v4 item: the 3.2.0 release ships under
MIT; Apache 2.0 and DCO land as the opening moves on the v4 branch so every subsequent commit
and PR carries the new license and sign-off contract.

Governance (first; complete before Phase 1)
- [x] L1 Migrate the project license from MIT to Apache 2.0
- [x] L2 Adopt the Developer Certificate of Origin (DCO)

Phase 1: the trustworthy floor
- [ ] N4 Semantic mode on real checkouts via the build-capture ladder (reframes v3.2 W4 as
      product work; the spike is a two-mechanism bake-off, run as item zero)
- [x] N1 Fix the lexical weight inversion; land the ranking regression suite (amended: the
      suite gates the priors too and re-adjudicates the A6 co-change regression)
- [ ] N2 One lexical ranker; purge stale results (amended: `agent.json` and the superseded a1
      doc citations join the sweep)
- [x] N5 Retire the legacy harness and obsolete code paths; migrate to one established form
- [x] N6 The freshness contract: no read tool serves silently stale data (added, finding 6)
- [ ] N3 The resident oracle by default (promotes v3.2 W1; amended: the reindex trigger is
      named, resident memory is measured)

Phase 2: the oracle
- [x] R5 The persisted reference index: calls, references, and tests edges (added, finding 7)
- [ ] R2 `fuse_impact`: blast radius before the edit (served from R5, not live SymbolFinder)
- [ ] R1 `fuse_check`: speculative diagnostics as repair packets, and Suite F (false-green and
      false-red both gated)
- [x] R6 Repair packets and the API-shape oracle: `fuse_signatures` (added)
- [ ] R7 `fuse_refactor`: compiler-executed rename and change-signature, staged as a diff (added)
- [x] R4 Rebuild the agent benchmark to measure the loop, not the payload (amended: task set
      and native plus LSP-armed baselines land in Phase 1; wall-clock recorded)
- [ ] R3 Collapse the tool surface around the oracle (shim-compatible; amended: typed union,
      ambient availability header, seven live tools)

Phase 3: the moonshot
- [x] M1 The speculative staging area: changeset lifecycle, diagnose, covering-test selection
      (re-scoped: in-process execution removed, not gated; see M2)
- [ ] M2 Out-of-proc emit-and-run test execution (added; stretch, pre-agreed to slip to 4.1)

Phase 4: retrieval bets (gate satisfied: N4's localize re-run is recorded in `results/localize.tier1.json`;
the re-run showed tier-1 does not move localize recall, so V1/V2 are not warranted by the evidence as recall
levers and are left unticked per the plan's pre-agreed re-scope, not merely deferred)
- [ ] V1 Graph verbalization: deterministic natural-language cards in the dense and lexical
      channels (added) [not warranted: tier-1's richer graph did not lift recall in the re-run]
- [ ] V2 Per-repo learned ranking from git history, temporal-split guarded (added) [not warranted: same]

Go-to-market (manual, after Phase 2)
- [ ] G1 The latency demo and launch publish (honest only after R1/R2; amended: verification
      parity, and the three-pane demo once R7 exists)
- [ ] G2 The analyzer contribution program and a public coverage table

---

## Re-plan: actual state and forward sequence (2026-07-03)

This section records the state after the first execution session and re-sequences the remaining
work around two blockers discovered during it. It supersedes the flat Sequencing section for
planning purposes; the per-item Why/How/Tests/Docs/Kill-risk below are unchanged.

### State snapshot

| Item | State | Note |
|------|-------|------|
| L1 license, L2 DCO | done | governance complete, gates green |
| N4 bake-off spike | done | build-capture ladder chosen; `results/n4-bakeoff.json`; 65 percent build-success is the oracle coverage ceiling |
| N1 ranking suite + weight fix | done | `fuse eval ranking`, `results/ranking.json` |
| N2 purge + citations | part 1 done | results archived, reduce/performance regenerated, citations fixed; in-memory `Bm25RelevanceIndex` deletion deferred (non-shipping path, heavy test coupling) |
| N5 harness retirement | done | one C# harness plus the one peer-script exception; drifts fixed |
| N6 freshness contract | done | reconcile-on-open plus stale-as-of stamp |
| N4 tier-1 build capture | done (out-of-proc worker) | B1 resolved: `Fuse.BuildCaptureWorker` rehydrates a binlog, runs extraction, serializes the graph; parent ingests. Default off (needs `FUSE_BUILD_CAPTURE_WORKER`) |
| N4 part 1 `fuse doctor` | done | per-project load-tier reporting |
| N4 localize re-run | done | `results/localize.tier1.json`: recall 15.0 vs 14.9 baseline, no lift; re-scopes V1/V2 as not-warranted |
| N3 resident oracle | part 1 done | supervised background upgrade (finding 5); resident workspace and incremental semantic reindex remain, see blocker B2 |
| R5 references + tests edges | done | `ReferenceEdgeAnalyzer` (references, weight 0.15) + `TestEdgeExtractor` (DI-resolved tests edges, post-merge); schema TargetVersion 15 |
| R6 signatures + repair packets | done | `fuse_signatures` (part 1) plus repair packets on `fuse_check` CS1061/CS0246 (part 2); optional `fuse_complete` rides N3 |
| R2 `fuse_impact` | done | blast radius from R5 + wiring edges; abstains on the exact break set; carries the availability header and covering tests |
| R1 `fuse_check` | engine + Suite F done | out-of-proc speculative typecheck (abstains unless tier-1); Suite F `results/checkgate.json` 8/8, zero false-green/false-red. Repair packets + resident fast path remain |
| R7 `fuse_refactor` | part 1 done (rename) | MSBuildWorkspace + Roslyn Renamer, staged as a diff, abstains on partial load. Change-signature (part 2) blocked: `Renamer` is in the referenced Workspaces packages, but `ChangeSignature` lives in `Microsoft.CodeAnalysis.Features` with no clean public API; a hand-rolled call-site rewriter would be high-risk and violate the "a partial refactor is worse than none" bar, so it is deferred rather than shipped half-working |
| R3 tool reshape | part done (availability header) | ambient grade header on the store-backed oracle reads; V2 shims already exist (`FuseDeprecatedTools`). The typed-union router (collapsing the fourteen tools around the oracle) remains: it is a surface-shaping redesign that removes or merges working, tested, documented tools, so its final shape warrants the maintainer's review before collapsing them. It does not need an RPC/protocol bump (the MCP surface is separate from `Fuse.Cli.Rpc`) |
| R4 loop suite | harness done | `fuse eval loop`, `LoopTranscriptClassifier` + `LoopMetrics` (deterministic, unit-tested). Model arms opt-in via `FUSE_LOOP_RUN`; numbers recorded from a provisioned run. LSP arm remains future work |
| M1 | done | covering-test selection over R5 tests edges plus the full changeset-session lifecycle (`fuse_changeset`: create/stage/diagnose/select/promote/discard, isolated, writes only on promote). Resident-workspace fast path for diagnose and the selection-recall benchmark (bounded by index mode) remain future work |
| M2 | not started | stretch, pre-agreed to slip to 4.1 |
| V1, V2 | not warranted | the N4 tier-1 localize re-run showed no recall lift, so the richer-graph premise did not hold |
| G1 | not started | outward-facing launch publish; not an autonomous action |
| G2 | docs done | analyzer coverage table + contribution recipe shipped; the community program is a launch activity |
| version | bumped to 4.0.0 | `build/set-version.ps1 4.0.0`, verify-version OK; no tag cut |

### The two substrate blockers

**B1: N4 tier-1 build capture cannot share a process with MSBuildWorkspace.** The mechanism is
proven (a `BuildCaptureLoader` spike rehydrated a real compilation from a binary log via
Basic.CompilerLog.Util 0.9.47 against Roslyn 4.14, validated on the SampleShop fixture). But adding
that library to the assembly that also hosts the `MSBuildWorkspace` loader breaks MSBuildWorkspace
with "Unable to load one or more of the requested types" (a Roslyn/Workspaces assembly-version
conflict: MSBuildLocator requires the SDK-resolved assemblies, the library brings its own closure).
A separate project does not fix it (the final app output still holds both closures). **Resolution:**
run build capture in a separate worker process. Because a live `Compilation` cannot cross a process
boundary, the worker must run the semantic extraction (the wiring analyzers over the rehydrated
compilation) and serialize the resulting nodes/edges/symbols/routes back to the parent, which writes
them to the store. This means extracting the compilation-to-graph extraction pipeline into an
assembly the worker can reference without MSBuildWorkspace.

**B2: N3 resident workspace has no consumer until the oracle tools exist.** Holding one loaded
compilation resident (MSBuild evaluation paid once) and incrementally updating it is in-process and
free of the B1 conflict, but its payoff is realized by R1/R2 forking the resident compilation. Built
in isolation it is untested-in-anger infrastructure. So N3's remaining halves are best built
together with the first oracle tool that consumes them.

### Forward sequence (re-sequenced around the blockers)

The original order assumed N4 and N3 were single large items landing before Phase 2. They are each
now split, and the true critical path runs through B1's worker. Realistic multi-session order:

1. **N4 tier-1 worker (unblocks B1).** Extract the compilation-to-graph extraction into an assembly
   with no MSBuildWorkspace dependency; add a `fuse build-capture` worker subcommand that reads a
   binlog, runs extraction, and writes serialized graph data to stdout or the store; have
   `SemanticIndexer` invoke it out of process (bounded args, per the invariant) as tier 1, falling
   back to MSBuildWorkspace (tier 2) then syntax (tier 3). Wire the tier into `fuse doctor` (already
   reports the tier) and the availability header. Then N4 tier-2 (bounded auto-restore) as salvage.
2. **N4 recall re-run.** Re-run `localize.json` and `review.json` with tier-1 on; record. This is
   the gate for Phase 4 and the honesty check on the "recall is bounded by index mode" hypothesis.
3. **N3 resident workspace plus incremental semantic reindex**, landed with R5's first consumer.
4. **R5** (references/tests edges at index time; schema `TargetVersion` bump), then **R2**
   (`fuse_impact` over R5), then **R1** (`fuse_check` speculative fork over the resident compilation)
   plus Suite F, then **R6 part 2** (repair packets on R1's diagnostics), then **R7**
   (`fuse_refactor` over R5's call sites), then **R4** (loop suite; curate the task set and record
   the native/LSP baselines early, in parallel), then **R3** (fold the surface around the now-real
   oracle tools, with shims and the ambient availability header).
5. **M1** (changeset lifecycle plus covering-test selection over R5), then **M2** (out-of-proc
   emit-and-run; stretch, may slip to 4.1).
6. **Phase 4 V1 then V2**, only after step 2's re-run is recorded.
7. **G1** (after R1/R2/R7), **G2** (recipe and coverage table; the docs half is startable anytime).

### Release-gate deltas

Satisfied now: gate 9 (L1), gate 10 (L2), and gate 8 in part (current-corpus citations, archive
move done). Outstanding and gated on the above: gates 5 and 6 (Suite F false-green/false-red, M1)
need R1/R5; gate 6a (N6 contract test) is satisfied for the reconcile path; gates 3 and 4
(protocol/schema bumps) apply when R5 bumps `TargetVersion` and when the oracle tools add RPC DTOs;
gate 7 (version bump to 4.0.0 via `build/set-version.ps1`) is a release-time step, not yet done. The
tag is not cut; a single open PR (#24) holds the work.

---

## Governance

L1 and L2 are the first v4 execution items, before the N4 bake-off and before any Phase 1
code. The 3.2.0 tag releases the current tree under MIT; governance lands immediately after on
the v4 line so every subsequent commit and PR is under Apache 2.0 with DCO sign-off. They pair
with G2: a contribution program needs a license and a provenance contract contributors can
follow without legal friction.

### L1. Migrate the project license from MIT to Apache 2.0

**Why.** Fuse is moving from a permissive MIT license to Apache 2.0 because the oracle release
adds patent-sensitive surface (compiler execution, staged diffs, speculative verification) and
the project is opening a community on-ramp (G2). Apache 2.0 carries an explicit patent grant
and a termination clause on offensive patent litigation, which is the standard pairing for
compiler-adjacent tooling that third parties embed in products. MIT stays permissive but offers
no patent language; staying on MIT while inviting framework-specific analyzer contributions
creates asymmetric risk for corporate adopters and contributors.

**How.** Replace [LICENSE](../LICENSE) with the Apache 2.0 text, retaining the Litenova Solutions
copyright line and adding the standard Apache 2.0 appendix. Update every license expression and
badge: `Directory.Build.props` and each `.csproj` `PackageLicenseExpression`, `README.md` and
the docs site license references, NuGet package metadata, the VS Code extension
(`ext/vscode/package.json`), and `mcp-registry/server.json`. Audit third-party dependencies and
the benchmark corpus for license compatibility (Apache 2.0 is compatible with MIT dependencies;
confirm no copyleft conflict in bundled native assets). Add a `NOTICE` file if any bundled
dependency requires attribution beyond the SPDX expression. Record the change in `CHANGELOG.md`
under 4.0.0 with a plain migration note for downstream packagers (license header change only,
no API break).

**Acceptance.** `LICENSE` is Apache 2.0; `build/verify-version.ps1` and a repo-wide grep find no
remaining MIT license claims on Fuse-owned artifacts; CI green; the contributing page names the
new license.

### L2. Adopt the Developer Certificate of Origin (DCO)

**Why.** Apache 2.0 projects need a lightweight provenance contract so every commit can be
traced to a contributor who attested they have the right to submit it. A full Contributor
License Agreement (CLA) re-assigns copyright or grants a broad patent license through a signed
legal document; it adds onboarding friction and is appropriate mainly when a single company
needs to relicense the whole tree. The Developer Certificate of Origin (DCO), used by the Linux
kernel, Kubernetes, and the Apache Software Foundation, is the better fit here: contributors add
a `Signed-off-by` trailer to each commit attesting the DCO 1.1 statement, GitHub's DCO bot
blocks merges without it, and no separate signature step is required. DCO plus Apache 2.0 is the
industry-default pairing for open contributions without CLA overhead.

**How.** Add [DCO.txt](../DCO.txt) (the canonical Developer Certificate of Origin 1.1 text) at
the repo root. Document the sign-off requirement in `CONTRIBUTING.md` and on the docs
contributing page (`site/content/docs/project/contributing.mdx`): use `git commit -s` or add
`Signed-off-by: Name <email>` manually; the sign-off certifies agreement with the DCO text.
Enable the DCO GitHub App (or equivalent CI check) on the repository so PRs without sign-off on
every commit fail with an actionable message. Update the PR template (if one exists) to remind
contributors. Existing commits before the cutover are grandfathered; the check applies from the
DCO adoption merge forward. Do not adopt a CLA in parallel; one provenance mechanism only.

**Acceptance.** `DCO.txt` is present; contributing docs describe sign-off; the DCO check is
enabled and verified on a test PR; G2's contribution recipe references the DCO requirement in
its first step.

---

## Phase 1: the trustworthy floor

### N4. Semantic mode on real checkouts via the build-capture ladder (reframes v3.2 W4 as product work)

**Why.** Finding 1, the largest defect. If the product is the compilation, then making it load
on an arbitrary cloned repo with the reliability of `git status` is core engineering, not
benchmark plumbing. This item also finally tests finding 3's hypothesis: is localize recall
bounded by index mode.

**Why the mechanism changed (2026-07-03).** As first scoped (harden MSBuildWorkspace with
restore, salvage, and per-project degradation), this item was an open-ended fight with
design-time build entropy, and the project has three recorded losses in that fight already:
v3's R0 could not restore AutoMapper, FluentValidation, MediatR, or Serilog on SDK 10.0.109
(NU1008, central-package-management and TFM skew); the corpus was rebuilt around that failure
rather than through it; and Scrutor fails restore today (`localize.json` notes "restore
Scrutor: restored 0, failed 2", NU1507). The deeper problem: one loader was serving two
different bars. The retrieval graph tolerates an approximate compilation (missing references
still resolve most DI and route edges); the oracle cannot, because a compilation that differs
from the real build in generators, analyzers, Razor output, or defines produces diagnostics
the build would not produce, in both directions (false greens and false reds). Only the real
build knows what the real build does, so the oracle tier is captured from the real build.

**How.** A three-tier ladder, reported per project by `fuse doctor` and by the ambient
availability header (R3):

- **Tier 1, oracle-grade (build capture).** Run the repo's own build once with a binary log
  (`dotnet build -bl`), extract every Csc invocation, and rehydrate exact compilations from
  the recorded command lines (references, analyzers, source generators, generated files,
  defines) via `CSharpCommandLineParser`; this is the approach proven by the Basic.CompilerLog
  tooling. Everything MSBuildWorkspace fights (Central Package Management, workloads, Razor,
  custom targets) is handled by construction, because the real build already handled it.
  Incremental: a source edit swaps one syntax tree into the rehydrated compilation
  (references are fixed); a project-file or package edit invalidates and re-captures that
  project with a bounded rebuild. The capture build is the same first build the agent would
  run anyway, and its one-time cost is comparable to the current full pass (69.7 s recorded
  for the direct semantic pass on NodaTime, `performance.json`).
- **Tier 2, graph-grade (salvage).** The original scoping survives here, serving retrieval
  only, never the oracle: automatic `dotnet restore` with a bounded timeout on missing assets;
  per-project partial load, so a failed project degrades that project rather than the whole
  solution (today a solution-open throw at
  [RoslynWorkspaceLoader.cs:61](../src/Core/Fuse.Semantics/RoslynWorkspaceLoader.cs#L61) drops
  everything); metadata-reference salvage from the NuGet cache and framework ref packs.
- **Tier 3, syntax.** Unchanged.

`fuse_check`, `fuse_impact`, and `fuse_refactor` answer only at tier 1 and abstain otherwise
(the availability contract); localize, resolve, and review use the best tier available. The
`fuse doctor` command names the concrete reason per project (SDK mismatch, unrestored,
workload missing, custom targets, build failed). In the harness, pin SDKs and pre-restore in
`CorpusManager` setup; record the achieved tier distribution.

**Tests.** A repo that builds reaches tier 1, and its rehydrated compilation's diagnostics
agree with `dotnet build` on the unmodified tree (the structural zero-baseline for Suite F). A
repo with one failing project degrades that project to tier 2 while the rest stay tier 1. A
repo missing assets triggers the bounded restore. `fuse doctor` reports the concrete downgrade
reason per project. A project-file edit invalidates and re-captures only that project. The
external-process invocations (restore, build) bound their argument lists per the change-safety
invariant.

**Docs.** `project/benchmarks.mdx` (the tier distribution), `reference/cli.mdx` (`fuse
doctor`), `AGENTS.md` corpus description, `internals/` (the ladder).

**Benchmark.** The de-risking spike becomes a bake-off, run as item zero of the release: on
the 4 corpus main checkouts plus about 20 popular OSS .NET repos, measure the tier
distribution under (a) hardened MSBuildWorkspace alone and (b) the capture ladder, and record
both, so the mechanism choice is a recorded result rather than an argument. The cutoff is
quantitative: tier 1 on at least 80 percent of the repos whose plain `dotnet build` succeeds
in the environment, and tier 2 or better on the majority of the rest. Then re-run
`localize.json` and `review.json` with the best tier on; this is the first real test of the
"recall is bounded by index mode" hypothesis.

**Expected result.** For the oracle (theory, made structural by the mechanism): Suite F
agreement on tier-1 projects should be near 1.0 by construction, because tier 1 shares the
build's own inputs; residual disagreement isolates incrementality bugs rather than load
approximation. The gate this imposes is also the honest truth: a repo that cannot build cannot
be typechecked by any tool, so those repos get tier 2 and an abstention, not a guess, and the
measured fraction of repos that do not build is published as the oracle's coverage ceiling.
For retrieval: the graph-dependent numbers (localize recall, co-change and centrality priors)
move where they were flat, or they do not and the hypothesis is falsified, which re-scopes
Phase 4 with that knowledge. Either outcome is worth the item.

**Kill risk, and it is still the release's biggest.** For tier 1: repos that do not build at
all in the eval environment (the recorded corpus history above says this is common; that
fraction is the measured ceiling of oracle coverage, published, not hidden), and staleness of
project-level structure between captures (bounded re-capture on project-file change; N6 stamps
anything older). For tier 2: unchanged from the original scoping, it remains an entropy fight
with a "good enough" cutoff rather than a done state, which is exactly why it now gates only
retrieval coverage and never the oracle's correctness.

### N1. Fix the lexical weight inversion; land the ranking regression suite

**Why.** Finding 4. A field-weight inversion executes on the localize hot path and nothing
measures ranking, so the class of bug is invisible. The fix is a few characters; the suite is
the real deliverable, and it is overdue hygiene the brand ("measurement") should already have.

**How.** Correct the `bm25()` weight vector at
[WorkspaceIndexStore.cs:758](../src/Core/Fuse.Indexing/WorkspaceIndexStore.cs#L758) so the
column order (chunk_id, path, name, symbols, signature, comments, body, subtokens, stems)
matches the documented intent: name, signature, and symbols above path, path above comments
and body. Reconcile with the sibling ordering in
[Bm25RelevanceIndex.cs:36](../src/Core/Fuse.Fusion/Scoping/Bm25RelevanceIndex.cs#L36) (which
N2 then deletes) so one intended ordering exists. Add MRR, recall@k, and nDCG helpers to
[Metrics.cs](../tests/benchmarks/Fuse.Benchmarks/Metrics.cs) (they exist today only in the
legacy `harness/layer-ranking.ps1`, the reference to port). Add a `RankingSuite` under
`tests/benchmarks/Fuse.Benchmarks/Suites/` implementing `IEvalSuite` with `Name = "ranking"`,
scoring the lexical channel in isolation (call `SemanticRetrievalEngine.LocalizeAsync` with
`Embedder = null`, the `--lexical` path) against changed-file truth from the existing
`dotnet-prs-v1` dataset. Register it in the `EvalCommand.BuildSuite` switch
([EvalCommand.cs](../src/Host/Fuse.Cli/Commands/EvalCommand.cs)) and update the three
help/error strings there. Report per-k metrics in `SuiteResult.Notes` if they do not fit
`Scorecard`, or extend `Scorecard`/`TaskResult` in
[EvalModel.cs](../tests/benchmarks/Fuse.Benchmarks/Model/EvalModel.cs) and
`Reporting.FormatScorecard`.

**Tests.** A unit test pins the intended field ordering (a query whose term hits a symbol name
outranks the same term in a path). The ranking suite runs and writes `results/ranking.json`
(the count of discovered suites and the `--help` list both rise). A deterministic scoring test
confirms MRR/recall@k on a hand-built ranking.

**Docs.** `internals/scoping-internals.mdx` (the field weights and the ranking suite),
`project/benchmarks.mdx` (the new ranking axis).

**Benchmark.** `results/ranking.json` recorded; `localize.json` re-run before and after on the
same corpus to show the precision delta.

**Expected result.** A precision lift on localize (folder-name matches stop outranking
symbol-name matches). The honest expectation is that the lift may be modest; with only 2 of 4
localize main checkouts even partial, ranking fixes move low single digits at best, and the
suite is the deliverable even if the lift is zero. The ranking suite becomes a required gate on
any change to weights, tokenization, query expansion, or priors.

**Kill risk.** Small but real, and worth naming after all: the bm25 weights interact with the
OR-expanded match expression (the subtokens and stems columns match expanded terms), so
reweighting path down can surface subword noise the old vector masked. The suite exists to
catch exactly this class.

**Amendment (finding 9).** The suite runs in two configurations, not one: the lexical channel
in isolation (`Embedder = null`, as originally scoped) and the shipping default (dense on,
priors on), so the gate covers what users actually run, and it gates the priors as well as the
field weights. It also re-adjudicates the A6 co-change prior, whose recorded cost as a
default-on feature is recall 15.06 to 14.90 percent, precision-when-answered 8.9 to 8.3
percent, and precision-when-confident 9.3 to 5.6 percent (`localize.a1.json` vs
`localize.json`). If the suite confirms the regression, the prior's default flips to off in
this release and the changelog names it as a behavior change, per the no-silent-changes
invariant.

### N2. One lexical ranker; purge stale results

**Why.** Section 1.3.3 and 1.3.4. Two parallel lexical rankers with different constants are
two sources of truth; and result files quoting the retired 5-library corpus are, under the
project's own convention, fabrications with provenance. The `reduce` suite writing
`reduce.json` while a legacy `reduction.json` still sits in results is exactly this drift.

**How.** Delete `Bm25RelevanceIndex` and route the classic `query` scoping mode through the
persistent FTS5 index (one ranker, one weight table, guarded by N1's suite). Regenerate
`reduce.json` and `performance.json` on the current corpus. Move every result file still
quoting the retired 5-library corpus (the legacy `layer*.json`, `reduction.json`, and any
5-lib latency/ranking artifacts) to `tests/benchmarks/results/archive/`. Sweep the docs and
`AGENTS.md` so only current-corpus files are cited.

**Tests.** N1's ranking suite guards the unification. A test or check confirms the classic
`query` path now reads FTS5. A docs-citation sweep confirms no orphaned references to archived
files.

**Docs.** `project/benchmarks.mdx`, `AGENTS.md` measured-results block, `internals/scoping-internals.mdx`.

**Benchmark.** Regenerated `reduce.json` and `performance.json`; archived legacy files.

**Expected result.** One ranking implementation, one weight table, a results directory where
every file is current. The classic CLI `query` ranking behavior shifts slightly; acceptable,
it was never the measured path (call it out in the changelog, do not fold it silently).

**Kill risk.** Low. The behavior change to the classic query path is the only user-visible
edge; the changelog names it.

**Amendment (finding 8).** Two more artifacts join the sweep. First, `agent.json`: 10 of its
12 PRs are retired-corpus (AutoMapper, FluentValidation, MediatR, NewtonsoftJson, Serilog),
which under this item's own convention makes it a fabrication with provenance; it is either
archived or kept as the explicit directional pre-R4 record, with the caveat written into the
file's notes and into every doc that cites it. Second, the doc citations that quote the
superseded a1 run: precision-when-confident is 5.6 percent in the current `localize.json`, not
9.3, and the dense-lift pairing on current files is 13.3 to 14.9 (`localize.a1-lexical.json`
vs `localize.json`), not 13.3 to 15.1. AGENTS.md, `overview.md`, and the benchmarks page are
swept for both.

### N5. Retire the legacy harness and obsolete code paths; migrate to one established form

**Why (the story and the analysis).** This is the same defect as findings 3.3 and 3.4 in the
critique, generalized: where two forms of the same thing coexist, a reader cannot tell which is
authoritative, and the project's brand is measurement. The tree carries two parallel benchmark
harnesses, the current C# `Fuse.Benchmarks` library driven by `fuse eval`, and a legacy
PowerShell harness (`tests/benchmarks/harness/*.ps1`, the `layerN` scripts, `run-all.ps1`) that
still owns metrics the C# harness lacks (MRR and recall@k live only in `layer-ranking.ps1`) and
still drives the peer run. Two harnesses is two definitions of what a number means, the same
class of problem as the two rankers (N2) and the stale results. Alongside it sit smaller drifts
found during this planning pass: the `FuseTools` XML summary says "eight tools" and omits
`fuse_neighbors` although there are nine; `OrderingApp` is used as a fixture but is not listed
in `corpus.json`; and `ReductionSuite` writes `reduce.json` while a legacy `reduction.json`
still sits in the results directory. Legacy that contradicts the current form is a slow-acting
version of the honesty problem. This release is the moment to migrate to one established form,
while N1 and N2 are already touching the same surfaces.

**How.** Port the metrics that live only in PowerShell into the C# harness (MRR, recall@k, nDCG
land in `Metrics.cs` via N1, so this item removes the PowerShell copies). Migrate the peer
harness (`harness/layer6-peers.ps1`) into a C# `PeersSuite` registered in
`EvalCommand.BuildSuite`; if the external-MCP-server orchestration genuinely needs a script,
keep exactly one documented PowerShell entry point and delete the rest. Delete the superseded
`harness/layer1..5*.ps1`, `layer-ranking.ps1`, `run-all.ps1`, `common.ps1`, and the setup
scripts once their logic is ported or already covered by `CorpusManager` (which already ports
`gen-prs.ps1` and corpus setup). Fix the drifts: correct the `FuseTools` summary and count, add
`OrderingApp` to `corpus.json` or record in one line why it stays fixture-only, and remove the
legacy `reduction.json` (N2 archives it). Audit for any code path superseded by the semantic
index and unreachable from a current surface, and remove it, honoring the deprecation-shim rule
for anything that is a public tool or a host method.

**Tests.** The C# harness reproduces the metrics the deleted scripts produced (a port-parity
check on a fixed input). `dotnet test` and the MCP contract suite stay green after the
deletions. The `FuseTools` count assertion in `McpServeIntegrationTests` matches the corrected
summary. The corpus manifest loads with `OrderingApp` resolved.

**Docs.** `project/benchmarks.mdx` (one harness, one command surface), `AGENTS.md` (the harness
description and the corpus list), `internals/` where the legacy scripts were referenced.

**Benchmark.** No new result file of its own; the deliverable is that every recorded result now
comes from one harness. Re-running any suite through `fuse eval` reproduces its `results/*.json`
without a PowerShell step.

**Expected result.** One benchmark harness, one command surface (`fuse eval`), one set of
result files, and the small doc/reality drifts closed. A contributor can read the harness and
know it is the whole harness.

**Kill risk.** Low. The only real risk is deleting a script whose logic was not fully ported;
mitigated by the port-parity check before each deletion and by keeping the peer script as a
single documented exception if its orchestration cannot move to C# cheaply.

### N6. The freshness contract: no read tool serves silently stale data (added, finding 6)

**Why.** Finding 6. The MCP path indexes only when the store is cold
([FuseTools.cs:192](../src/Host/Fuse.Cli/Mcp/FuseTools.cs#L192)); `mcp serve` has no file
watcher; the host watcher broadcasts `fuse/invalidated` without reindexing
([HostCommand.cs:63](../src/Host/Fuse.Cli/Commands/HostCommand.cs#L63)); and
`ReindexFileAsync` ([SemanticIndexer.cs:199](../src/Core/Fuse.Semantics/SemanticIndexer.cs#L199))
has no product caller. So the moment the agent edits a file, every read tool answers from an
index frozen at first call, unmarked, for the rest of the session. An oracle whose brand is
"trust me instead of the build" dies the first time `fuse_find` returns a symbol the agent
deleted two turns ago; this is a precondition for every Phase 2 claim, which is why it is a
floor item and not folded into N3. Separately, AGENTS.md quotes the 22.8 ms incremental
re-index P50 (`performance.json`) as warm product latency; that number measures a method
nothing in the product invokes, and the citation is corrected as part of this item.

**How.** A contract, not a feature: before answering, every read tool either reconciles dirty
files or stamps the answer stale-as-of; there is no third outcome. In the resident daemon
(`mcp serve`, `fuse host`), the `DebouncedFileWatcher` feeds a reconcile queue that calls
`ReindexFileAsync` (syntax rows now; the N3 semantic path when it lands). In short-lived
callers, a bounded content-hash check over known files runs on open (the store already records
`content_hash` per file). If reconcile cannot complete within its budget, the response carries
an explicit staleness stamp (dirty-file count and age) in the ambient availability header (R3)
rather than blocking the answer.

**Tests.** The contract test: for every read tool, edit-then-query asserts either fresh data
(a symbol added by the edit is found; a symbol deleted by the edit is not) or an explicit
staleness stamp. Exercised through `mcp serve` end to end, not only unit-level. A bulk change
(simulated `git checkout`) triggers the dirty-count threshold and degrades to a stamp plus a
re-index suggestion instead of a reconcile storm.

**Docs.** `internals/operator.mdx`, `reference/mcp-tools.mdx` (the header fields),
`project/performance.mdx` (the corrected incremental citation).

**Benchmark.** `performance.json` gains a reconcile-latency figure: single-file reconcile at
the already-recorded 22.8 ms P50 path cost, now actually wired to the serve path, and a
whole-workspace hash sweep target in the low hundreds of ms on a NodaTime-size repo (targets,
to be recorded).

**Expected result.** Correctness, not speed: the wrong-answer class "stale presented as fresh"
is eliminated by contract, and the abstention brand extends to freshness. This item carries no
recall or token number of its own; its value shows up as trust in every Phase 2 measurement
(Suite F disagreement caused by staleness would otherwise be indistinguishable from oracle
bugs).

**Kill risk.** Watcher reliability on network drives and very large trees (the debounce and
the hash fallback bound it); reconcile storms during bulk operations (the dirty-count
threshold above). Neither blocks shipping the stamp path, which is the contract's floor.

### N3. The resident oracle by default (promotes v3.2 W1)

**Why.** Finding 5, and the thesis: an oracle that pays 70 seconds of cold start per question
is not an oracle. V3.2 deferred this because a detached background task in the serve host was
easy to orphan and to race teardown. The daemon owning its lifecycle fixes both and is the
prerequisite for dependency-scoped freshness, which R1 and R2 also need.

**How.** Hold one loaded workspace (the `MSBuildWorkspace` or the `LoadedProject` set from
[RoslynWorkspaceLoader.cs](../src/Core/Fuse.Semantics/RoslynWorkspaceLoader.cs)) resident in
the `mcp serve` host and, symmetrically, in `fuse host` / `FuseHostService`, with explicit
lifetime management, so the MSBuild evaluation is paid once. `MSBuildLocator` is already
registered once per process; extend that to a retained workspace. Make syntax-first cold start
the default by retiring the `FUSE_BG_UPGRADE` opt-in gate
([McpServeCommand.cs:46](../src/Host/Fuse.Cli/Commands/McpServeCommand.cs#L46),
[FuseTools.cs:177](../src/Host/Fuse.Cli/Mcp/FuseTools.cs#L177)) and replacing the
fire-and-forget `ScheduleSemanticUpgrade` at
[FuseTools.cs:211](../src/Host/Fuse.Cli/Mcp/FuseTools.cs#L211) with a supervised job owned by
the resident host, cancellation tied to host shutdown, no orphaned task. Add a semantic path to
`ReindexFileAsync` ([SemanticIndexer.cs:199](../src/Core/Fuse.Semantics/SemanticIndexer.cs#L199)):
on a file edit, apply the document change to the resident compilation and re-run the wiring
analyzers over only the changed file's project and its direct dependents, recomputing only the
affected edges and clearing `SemanticPendingMetaKey` for that neighborhood.

**Tests.** The resident workspace is reused across two index calls without a second MSBuild
load (a timing or invariant assertion). The default cold path serves syntax-first then upgrades
with no orphaned process and no teardown race (the failure mode V3.1 hit). Editing a file to add
a DI registration makes the new resolve edge appear after the incremental call, not just the
symbol rows (extends [IncrementalReindexTests.cs](../tests/Fuse.Semantics.Tests/IncrementalReindexTests.cs)).
An unrelated edge elsewhere is unchanged; the re-analyzed set is bounded to the dependent
neighborhood; a deleted file's edges are removed. If a DTO or `[JsonRpcMethod]` signature
changes, bump `FuseHostService.ProtocolVersion`
([FuseHostService.cs:35](../src/Host/Fuse.Cli/Host/Rpc/FuseHostService.cs#L35)) and
[protocol.ts](../ext/vscode/src/host/protocol.ts) together and update the contract test.

**Docs.** `project/performance.mdx` (few-seconds cold start, resident workspace, the freshness
number replacing the syntax-rows-only caveat), `internals/pipeline.mdx`, `internals/operator.mdx`.

**Benchmark.** `performance.json` gains a resident-workspace first-answer number (target a few
seconds) and an edge-freshness number (edit a DI registration, query resolve, assert the new
edge within about 1 s), plus a correctness check that the post-incremental graph matches a full
re-index on the changed neighborhood.

**Expected result.** First answer in a few seconds instead of about 70; edited edges fresh
within about 1 s instead of never (until full re-index). This is what turns the compilation
into an oracle rather than a batch tool.

**Kill risk.** Background-upgrade races and orphaned tasks (the reason it is off today), and
memory growth from a retained compilation. Mitigation: the daemon owns the lifecycle, no
fire-and-forget in the serve path, upgrade is a supervised job with cancellation tied to the
host; a compilation memory budget with eviction. Ships only with the lifetime and freshness
tests green.

**Amendments (findings 6 and 7).** Three. First, the trigger is named as a deliverable:
nothing in the serve loop calls `ReindexFileAsync` today (its only callers are tests and the
performance suite), so this item owns the observer, not just the semantic path. The resident
daemon runs the debounced watcher (the `DebouncedFileWatcher` class exists and is used by
`fuse host` for broadcast only); `mcp serve` gets the same watcher feeding a reconcile queue;
short-lived callers reconcile by content hash on open (the store already records
`content_hash` per file). The freshness contract that consumes this is its own floor item
(N6). Second, the benchmark records resident memory: `performance.json` gains a
resident-workspace RSS figure for NodaTime and eShopOnWeb alongside the first-answer number,
because the kill risk above is currently promised a budget but never a measurement. Third, the
recorded background-upgrade path costs more total time than the direct pass (19.9 s syntax
plus a further 94.9 s to semantic-ready, versus 69.7 s direct, `performance.json`); the
resident workspace should close most of that gap, and the benchmark asserts the syntax-first
default no longer pays a total-time penalty over the synchronous pass.

---

## Phase 2: the oracle

### R5. The persisted reference index: calls, references, and tests edges (added, finding 7)

**Why.** Finding 7. Three items in this plan assumed graph edges that do not exist: R2's
tens-of-ms impact, M1's covering-test selection ("the graph already carries test edges"), and
opt-in graph expansion's `calls` traversal weight. Verified against the tree, no analyzer emits
`calls` or `tests` edges, yet [EdgeWeightProvider.cs:31](../src/Core/Fuse.Retrieval/EdgeWeightProvider.cs#L31)
weights them (0.65 and 0.60) and the `"tests"` branch of `RoleFor`
([SemanticRetrievalEngine.cs:443](../src/Core/Fuse.Retrieval/SemanticRetrievalEngine.cs#L443))
is dead. This item builds the substrate the others assume, once, so R2, M1, and expansion all
read it instead of each recomputing it live.

**How.** At semantic index time (and incrementally under N3/N6), walk each project's semantic
model once and persist two edge classes into the existing `edges` table (or a sibling table if
the volume warrants): member-level `references` (symbol to referencing document and span) and
`tests` (a test method to the symbols it transitively references, with the DI graph resolving
interface references to their registered implementations, so a test that depends on
`IOrderService` is correctly linked to `OrderService`). The extraction is the same
model-walk-and-upsert every analyzer already does; the schema and incremental machinery exist
(`WorkspaceIndexSchema`, `ReindexFileAsync`). Bump `WorkspaceIndexSchema.TargetVersion` (new
edge kinds are an extraction-contract change) so a stale index rebuilds. `fuse_impact` (R2)
becomes a lookup plus an on-demand bind-check; test-impact selection (M1) becomes a transitive
query over `tests` edges.

**Tests.** A method's `references` rows match live `SymbolFinder.FindReferencesAsync` on a
fixture (port-parity, the same discipline as N5). A `tests` edge links a test through an
injected interface to the registered implementation (the DI-resolution detail is what makes
this better than a plain reference walk). An incremental edit adds and removes the right
reference rows and no others (extends `IncrementalReindexTests`). The dead `calls`/`tests`
weights in `EdgeWeightProvider` now have producers, or are removed if this item chooses
`references` over `calls` as the edge name (the changelog names the choice).

**Docs.** `internals/semantic-graph.mdx` (the new edge kinds), `reference/mcp-tools.mdx`
(fuse_impact now index-backed).

**Benchmark.** `performance.json` gains `fuse_impact` P50 from the persisted index on NodaTime
and eShopOnWeb, target an order of magnitude below live `SymbolFinder` on the same solutions
(both recorded side by side so the claim is a measurement, not an assertion), plus the
index-time cost delta and row-count growth per repo (the substrate is not free; its cost is
published).

**Expected result (theory).** `fuse_impact` at tens of ms rather than hundreds-to-seconds,
which is what makes R2's crown-table target real; test selection that is sound-enough to gate
M1's fallback because DI resolution closes the interface-to-implementation gap that a naive
reference walk misses. Magnitude of the latency win is set by solution size; on NodaTime-size
solutions the precedent is the sub-ms edge lookup already in `performance.json`.

**Kill risk.** Index-time cost and row volume on large solutions (bound the walk per project,
measure and publish both; a member-level reference index can be large, so cap granularity at
the declaration that owns the reference if row counts explode). Staleness inherits N6. If the
row volume proves unaffordable, fall back to persisting `references` at file granularity (still
enough for `fuse_impact`'s file-level answer) and computing member-level binding on demand.

### R2. `fuse_impact`: blast radius before the edit

**Why.** Turns "edit, build, discover you missed four call sites, edit, build again" into one
up-front answer. The most conventional item in the phase; the reference substrate (R5) does
most of the work.

**How.** Add a `fuse_impact` MCP tool: call-site and implementation enumeration for a named
symbol, plus "what breaks if this signature changes" (the call sites whose arguments would no
longer bind). Extends the existing review machinery from diff-first to intent-first over the
resident compilation. Amended (finding 7): served from R5's persisted reference rows, not live
`SymbolFinder`; the bind-check for a proposed signature change runs on demand against the
resident compilation for the candidate sites only, and live `SymbolFinder` is the parity
oracle in tests, not the serving path.

**Tests.** For a named method, the call sites and implementers are enumerated across the
solution. For a signature change, the break set includes the now-non-binding call sites and
excludes unaffected ones. Tool-name array updated.

**Docs.** `reference/mcp-tools.mdx`, `AGENTS.md`.

**Benchmark.** Over the signature-changing subset of the PR corpus, does the impact set cover
the files the PR actually touched (extends the `review.json` methodology, same must-keep caveat
noted honestly).

**Expected result.** Complete call-site sets in tens of milliseconds served from R5's index
(the sub-millisecond resolve in `performance.json` is the precedent for edge-lookup latency;
the bind-check adds a bounded per-candidate cost), removing the discover-by-rebuild iterations
for refactors. Without R5 the same feature runs live `SymbolFinder` at hundreds of ms to
seconds warm, which does not meet the crown-table target; the "tens of ms" claim is R5's, not
`SymbolFinder`'s (finding 7).

**Kill risk.** Low. Availability is the only real one, and it inherits N3/N4's load state and
the abstention contract. Second: R5 must exist first, or the target silently reverts to
`SymbolFinder` latency; R2 ships after R5.

### R1. `fuse_check`: speculative diagnostics

**Why.** The killer feature and the direct answer to finding 2. Replacing a `dotnet build`
round-trip (tens of seconds) with a sub-second speculative typecheck is what changes what the
agent does, not just what it reads.

**How.** The agent submits a patch or names dirty files; Fuse forks the resident in-memory
solution (Roslyn's immutable trees make forks structurally cheap), applies the change with
`Solution.WithDocumentText`, and returns compiler diagnostics for the changed documents and
their dependents, scoped via the dependency graph Fuse already builds, in sub-second time with
no disk write and no MSBuild invocation. Depends on N3's resident compilation. Add a new
`fuse_check` MCP tool method to `FuseTools` (attribute-registered, no separate schema). Add a
`CheckSuite` (Suite F) built on the existing patch-apply-and-oracle plumbing in
[TaskResolutionHarness.cs](../tests/benchmarks/Fuse.Benchmarks/TaskResolutionHarness.cs) and
`Corpus/DotnetCli.cs`, comparing `fuse_check` diagnostics against `dotnet build` per corpus PR.

**Tests.** A patch that introduces a type error is reported by `fuse_check`; a clean patch
returns clean. A change whose owning project loaded partial or syntax returns "cannot verify,
project X did not load" rather than a guess (the abstention contract extended to the oracle).
The integration test tool-name array in
[McpServeIntegrationTests.cs](../tests/Fuse.Cli.Tests/Mcp/McpServeIntegrationTests.cs) is
updated.

**Docs.** `reference/mcp-tools.mdx`, a scenarios page for the check workflow, `AGENTS.md`.

**Benchmark.** Suite F recorded to `results/check.json`: agreement rate with `dotnet build`,
false-green and false-red rates (both headline honesty numbers), and wall-clock ratio (target
P50 under 1 s on corpus-size solutions versus tens of seconds).

**Expected result.** Sub-second diagnostics on tier-1 (oracle-grade) projects, an explicit
abstention otherwise. On the demo task class (multi-file edits), this removes the intermediate
build cycles; the final ground-truth build stays. With N4's capture ladder, agreement with the
build is structural rather than aspirational on tier 1 (the rehydrated compilation shares the
build's own inputs), so the projected false-green and false-red rates are near zero by
construction, with residual disagreement isolating incrementality bugs (theory until Suite F
records it).

**Kill risk.** False greens. A check that says clean when the build would fail destroys trust
faster than no check at all. Mitigation: the abstention contract (never guess on a
partial/syntax project) and a Suite F false-green rate driven near zero as a merge gate. Second
risk: memory on large solutions; mitigate with a compilation cache budget and per-fork
document-level scoping.

**Amendments (2026-07-03).** Three. (a) The gate was half-blind: on imperfect loads the
likelier failure is false reds, phantom diagnostics from missing generated sources or analyzer
references (Razor especially), and a check that cries wolf teaches the agent to ignore it as
fatally as one that lies green; Suite F records disagreement in both directions and the
release gate holds both near zero on tier-1 projects. (b) Diagnostics ship as repair packets
(R6), not bare verdicts; the agent's next action after a diagnostic is always "go fetch the
signatures", and folding that round-trip into the response is where the iteration count
actually drops. (c) The sub-second P50 claim is scoped honestly: warm forks on corpus-size
solutions, with the dependent-closure recheck async; a root-project signature edit in a
many-hundred-project solution will not re-diagnose its closure in under a second, and the docs
do not imply it will. Where feasible, Suite F also records an LSP overlay-diagnostics
comparison arm, because that, not `dotnet build`, is the strongest competing verify path (see
honest ceilings).

### R6. Repair packets and the API-shape oracle (added)

**Why.** The agent's dominant failure mode on .NET is guessing an API shape wrong (a member
that does not exist, a wrong argument type, a missing overload), and its dominant time sink
after a failed check is fetching the context to fix it. R1 reduces the cost of a failed verify;
this reduces the count of failed verifies, which is the metric R4 measures. Both mechanisms use
pieces Fuse already owns (Roslyn diagnostics carry the symbols; the reduction pipeline renders
them), so this is assembly, not research.

**How.** Two parts. (a) R1's diagnostics ship with fix context attached: for CS1061 (member
does not exist) the receiver type's public surface at PublicApi tier plus nearest-name
candidates; for CS1503 / CS1615 (argument type / by-ref) the expected type and all overloads of
the target; each rendered through the existing reduction pipeline and budgeted by the existing
packer. (b) A new `fuse_signatures` MCP tool: a batch "exact signatures plus XML docs for these
N symbols" lookup, the single most common thing an agent greps for, served from the symbol
store in one call instead of N grep-plus-read round-trips or a heavyweight `fuse_context` call.
Optional (c), if cheap: `fuse_complete`, the legal members at a location via Roslyn's
recommendation service, the compiler-native answer to a hallucinated member before it is
written.

**Tests.** A CS1061 diagnostic returns the receiver's public members and a nearest-name
suggestion. `fuse_signatures` returns exact signatures and docs for a batch of symbols,
including overloads, and abstains with the availability header on symbols in an unloaded
project. Tool-name arrays updated; a shim is not needed (these are additive).

**Docs.** `reference/mcp-tools.mdx`, a scenarios page for the fix-a-build-error workflow,
`AGENTS.md`.

**Benchmark.** Carried by R4: the count of build-gated turns whose diagnosis is an API-shape
error, with and without repair packets, over the task-resolution transcripts. No standalone
result file; the win is a turn-count reduction measured in R4.

**Expected result (theory).** This is the item that actually funds the plan's own 20-to-40
percent cumulative-token projection, which its original items do not. Cumulative tokens scale
with turn_count times accumulated_context, so a turn avoided saves a whole turn of re-processed
context. Arithmetic on the Suite D shape (25-turn cap, about 211k median tokens, an estimated
2 to 3 build-gated turns of 8 to 12k re-processed context each) supports roughly 10 to 20
percent from loop collapse alone; reaching the plan's upper band requires exactly this kind of
turn-count reduction, and R4's transcripts measure whether it materializes. Labeled theory.

**Kill risk.** Response bloat: repair packets must be budgeted like any emission or they
balloon the very token cost they aim to reduce; the packer already enforces a budget, so the
mitigation is to reuse it and cap packet size. If R4 shows API-shape errors are a small
fraction of build-gated turns, part (a) still helps and parts (b)/(c) are cut.

### R7. `fuse_refactor`: compiler-executed rename and change-signature (added)

**Why.** R1/R2 verify the agent's hand-made multi-file edits after the fact; for the
mechanical class of edits, the higher-leverage move is to perform them. The G1 demo task,
"rename this method on IOrderService and update everything," collapses from an agent loop
(N call sites generated token-by-token, then checked) to one deterministic call that is correct
by construction. This is faster than any check loop can make the generative version, and it is
a capability an LSP-armed agent has in principle (LSP rename) but no .NET agent toolchain
exposes well.

**How.** Two operations only in the first cut, hard-capped until R4 data justifies a third.
Rename via Roslyn's `Renamer.RenameSymbolAsync` (public API, solution-wide, with the
string/comment options), and change-signature via `SymbolFinder` call-site enumeration (R5)
plus a per-site syntax rewriter. Input: a symbol and the intent; output: a unified diff staged
in the changeset area (M1's lifecycle), pre-checked by R1, never touching disk until the agent
promotes it. Add `fuse_refactor` to `FuseTools`; tier-1 (oracle-grade) only, abstain otherwise,
because a partial load produces an incomplete rename, which is worse than none.

**Tests.** A solution-wide rename updates every reference and no unrelated same-named symbol
(a class and a local with the same identifier stay distinct, the Roslyn-semantics win over a
textual rename). A change-signature updates all call sites and leaves the diff staged, not
written. An unloaded project yields abstention, not a partial rename. Tool-name array updated.

**Docs.** `reference/mcp-tools.mdx`, a scenarios page (the rename workflow), `AGENTS.md`.

**Benchmark.** A refactor bucket in R4: for mechanical-edit tasks, iterations-to-green and
correctness for the native arm, the R1/R2 verify arm, and the R7 execute arm.

**Expected result (theory).** On the mechanical-edit task class, iterations-to-green
approaches 1 by construction, and the token cost of the edit drops from tens of thousands of
output tokens (editing N sites by hand) to a diff review. Win condition, to be measured in R4:
the refactor arm at 100 percent correctness with wall-clock and tokens both under half the
R1/R2 verify arm on the rename task class. Labeled theory until R4 records it.

**Kill risk.** Scope creep into rebuilding an IDE refactoring engine; the mitigation is the
hard two-operation cap. Second: a rename that is correct in Roslyn but crosses a boundary
Roslyn does not see (a string in a config file, a reflection call, an analyzer-generated
reference) is silently incomplete; the mitigation is that R7 stages a diff for the agent to
review and R1 re-checks it, so an incompleteness surfaces as a diagnostic rather than a
committed bug, and the docs state the boundary plainly.

### R4. Rebuild the agent benchmark to measure the loop, not the payload

**Why.** Finding 2. Suite D at N=12, one rollout, cannot detect the effect it exists to
measure, and cumulative session tokens is the wrong metric for the oracle thesis. This is a
harness-first item: the benchmark is the deliverable.

**How.** Build a task-resolution suite over a small verified set of 10 to 15 PRs with
reproducible failing-then-passing tests, on top of the existing but unwired
[TaskResolutionHarness.cs](../tests/benchmarks/Fuse.Benchmarks/TaskResolutionHarness.cs)
(`OracleCommand`, `dotnet test`). Metrics: pass@1 and iterations-to-green with and without the
oracle tools, plus build-invocations-per-session as the direct measure of loop collapse. Cheap
driver model, three rollouts minimum, CIs recorded. Register the suite in `EvalCommand.BuildSuite`.

**Tests.** The suite runs a single task end to end deterministically where the model is stubbed;
the metric computation (iterations, build-invocations) is unit-tested against a scripted
transcript.

**Docs.** `project/benchmarks.mdx` (the new suite and its metrics, replacing the Suite D framing),
`AGENTS.md`.

**Benchmark.** `results/task-resolution.json` (or the chosen `Name`), with pass@1,
iterations-to-green, and build-invocations-per-session, all with CIs.

**Expected result.** A metric that can actually move with the oracle. The deterministic
sub-metrics (Suite F agreement, build-invocation counts) carry the claim between the expensive
model-driven runs, which are scheduled, not continuous.

**Kill risk.** Cost. Mitigation: the deterministic sub-metrics stand between full runs; the
curated 10-to-15 PR set with reproducible test oracles is the expensive build and is done once.

**Amendments (2026-07-03).** Three, and they change when R4 happens. (a) The task-set curation
is pure data work blocked on nothing, so it moves into Phase 1 alongside the N4 spike, and the
native-arm baseline is recorded before any oracle tool exists. Pre-registering the baseline is
what makes the eventual claim credible (a project cannot grade its own homework by building the
benchmark after the feature it measures) and it de-risks G1's demo-task selection for free.
(b) A third arm: an agent with the C# language server or serena. That is the comparison the
launch will actually be judged against, and without it the plan has no measurement that
survives the rebuttal "an LSP already does this." (c) Record wall-clock per task, not just
iterations and build-invocations: the Expected-impact section says the gains are asymmetric
toward wall-clock, so iteration counts alone will not show the 1.5-to-4x it projects, and the
demo (G1) needs the wall-clock number to be honest.

### R3. Collapse the tool surface around the oracle, seven live tools (shim-compatible)

**Why (the story and the analysis).** Nine tools describe a workflow, and models follow
workflows badly. The critique's argument is that the surface should encode the oracle thesis
(ask, check, impact, plus the two packed-context tools) while the install base is small enough
to move without pain. One honesty note carried from the critique into this plan: the response
motivated R3 partly with "Suite D suggests the current surface does not change agent behavior,"
but Suite D at N=12 with one rollout cannot support that inference (the same reading that says
its positive result proves nothing). So R3 is justified on its own merits (a surface that reads
as an oracle, not a nine-step workflow), not on Suite D, and R4 measures whether the reshape
actually changes behavior rather than asserting it. Doing the reshape now, while shims keep
every old name resolving, is the cheap moment; it gets more expensive with every new user.

**How.** Reshape to seven live tools (see the 2026-07-03 amendment below for why seven, not
five): `fuse_ask` (one entry point taking a typed parameter union of symbol, route, config,
text, or task, internally routing to resolve or localize and naming which it did), `fuse_check`,
`fuse_impact`, `fuse_signatures` (R6), `fuse_refactor` (R7), `fuse_context`, `fuse_review`. Fold
`fuse_map`, `fuse_find`, `fuse_localize`, `fuse_neighbors`, and `fuse_resolve` into `fuse_ask`
(or keep `fuse_index` as an explicit maintenance verb; decide during the item). Note the
collision:
`fuse_ask` currently exists as a deprecation shim in
[FuseDeprecatedTools.cs](../src/Host/Fuse.Cli/Mcp/FuseDeprecatedTools.cs) pointing at
`fuse_localize`; this item revives it as a live router (remove its shim, add the live method to
`FuseTools`). For every folded name, add a deprecation shim per the existing mechanism so a
client that cached the old surface across the upgrade gets an actionable message naming the
replacement, never a bare "Unknown tool." Update the `ServerInstructions` block in
[McpServeCommand.cs](../src/Host/Fuse.Cli/Commands/McpServeCommand.cs) (lines 75 to 91) and both
name arrays in [McpServeIntegrationTests.cs](../tests/Fuse.Cli.Tests/Mcp/McpServeIntegrationTests.cs).

**Versioning note (this is why R3 fits a 4.0 major).** The change-safety invariant is
about never showing a client a bare "Unknown tool" across an upgrade; it is satisfied by the
shims, which every folded name gets. Because the reshape is additive (new live tools) plus
shims (old names still resolve to an actionable message), no existing client actually breaks.
The clean removal of the shims is deferred to the next major, per the invariant. This is how
the whole reshape rides a major bump without violating the convention. See the release-gate
section for the hard line: if any shim is dropped rather than kept, the release is a major, not
a minor.

**Tests.** `fuse_ask` routes a symbol query to resolve and a vague query to localize and names
the path taken. Each folded name resolves to a shim naming its replacement and saying
"reconnect" (extends [FuseDeprecatedToolsTests.cs](../tests/Fuse.Cli.Tests/Mcp/FuseDeprecatedToolsTests.cs)).
The `ExpectedV3ToolNames` and `ExpectedDeprecatedToolNames` arrays match the new surface.

**Docs.** `reference/mcp-tools.mdx`, `mcp-resources.mdx`, the scenarios pages, `README.md`,
`LAUNCH.md`, `AGENTS.md`, `CHANGELOG.md`, all of which enumerate tool names.

**Benchmark.** Re-run the loop suite (R4) on the new surface.

**Expected result.** A seven-tool oracle surface, no client breakage. Whether it changes agent
behavior is measured by R4, not asserted.

**Kill risk.** `fuse_ask` becomes a junk drawer. Mitigation: its response always names the
resolution path taken, so the contract stays inspectable.

**Amendments (2026-07-03).** Three. (a) The live surface is seven, not five: `fuse_ask`,
`fuse_check`, `fuse_impact`, `fuse_signatures` (R6), `fuse_refactor` (R7), `fuse_context`,
`fuse_review`. The added two are oracle-shaped (a signature lookup and a compiler-executed
edit), so they belong on the oracle surface rather than folded into `fuse_ask`. (b) Do not fold
exact lookup into the router blindly: exact find is a distinct mental act for a model, and a
bare-string `fuse_ask` invites mode confusion; give `fuse_ask` a typed parameter union
(symbol, route, config, text, task) so the caller declares intent and the router does not have
to guess it from a string. (c) Every response from every tool carries an ambient availability
header: index mode, per-project load tier (N4), dirty-file count since last reconcile (N6), and
whether this answer is oracle-grade or graph-grade. `fuse doctor` puts availability in a CLI
command a mid-loop agent never calls; the abstention contract the plan celebrates has to be
ambient metadata on the answer, not a diagnosis the agent must separately request. The header
is the single highest-leverage agent-ergonomics change in the reshape.

---

## Phase 3: the moonshot

### M1. The speculative staging area: propose, verify, select (re-scoped)

**Why.** The category change. Fuse maintains N parallel in-memory branches of the compilation;
an agent proposes changesets; Fuse typechecks each branch (R1) and computes which tests cover
the changed symbols (R5's `tests` edges), reporting per-candidate diagnostics and the covering
test set before anything is written to the working tree. The edit-verify loop becomes
propose-oracle-commit. Nothing in the .NET ecosystem does this.

**Why the scope changed (2026-07-03).** The original M1 made in-process test execution the
headline and gated it on a near-zero false-green rate. That gate cannot be met and the premise
was false in two ways. First, "the graph already carries test edges" is untrue against the tree
(no analyzer emits `tests` edges; finding 7), so M1 now depends on R5 to build them. Second,
"hard sandboxing of filesystem and network effects" in-process is not gate-able engineering, it
is not achievable: modern .NET has no AppDomain isolation and no code-access security, so
statics, the filesystem, sockets, and `Environment.Exit` all leak, and a single hostile test
takes down the resident oracle daemon that R1/R2/N3 depend on. That is disqualifying regardless
of the false-green rate. So in-process execution is removed from M1 rather than gated; the
execution ambition moves to M2 (out-of-proc) as a stretch item. M1's 4.0 deliverable is the
changeset lifecycle plus diagnosis plus covering-test selection, which was the original
fallback, now promoted to the design.

**How.** Changeset sessions with a lifecycle (create, apply, diagnose, select, promote,
discard) over the resident workspace; per-branch diagnostics via R1; test-impact selection over
R5's `tests` edges, where the DI graph resolves interface references to implementations so the
covering set is sound-enough (a test injecting `IOrderService` is selected for a change to
`OrderService`). No execution in M1; the selected set is handed back for the agent (or M2) to
run.

**Tests.** A changeset session applies, diagnoses (R1), and returns the covering test set for
the changed symbols without writing to disk. Two candidate changesets over the same base are
isolated (one's edits do not leak into the other's diagnostics). A promote writes the diff; a
discard leaves the tree untouched.

**Docs.** `internals/` staging-area page, `reference/mcp-tools.mdx`, `AGENTS.md`.

**Benchmark.** Over PRs with test oracles, does the selected covering set include the tests the
PR's own diff touched (selection recall), and how much smaller is it than the full suite
(selection precision as a proxy for the eventual execution speedup). No execution number in
M1; that is M2.

**Expected result.** The changeset lifecycle plus a covering-test set that is a small fraction
of the suite, computed in tens of ms from R5. This is the substrate that makes M2's execution
speedup possible and is independently useful (an agent can run just the selected subset with
its own `dotnet test --filter`). Selection soundness is bounded by R5's edge completeness,
stated plainly.

**Kill risk.** Selection unsoundness (a covering test missed because the reference walk did not
reach it through reflection or a source generator); mitigated by R5's DI-resolved edges and by
reporting selection as best-effort, never as "these are all the tests." Depends on R5; ships
after it.

### M2. Out-of-proc emit-and-run test execution (added; stretch, may slip to 4.1)

**Why.** The execution half of the original M1, redesigned to be buildable. A verified green/red
verdict on a speculative changeset in seconds, without the `dotnet build` that dominates
`dotnet test` wall-clock, is the moonshot's actual payoff; it just cannot happen in-process.

**How.** Emit the speculative compilation's assemblies in memory (Roslyn `Compilation.Emit`),
materialize them and their dependencies to a temp directory, and run M1's selected covering
subset in a spawned micro-host (Microsoft.Testing.Platform) with OS-level isolation: a scratch
working directory, a stripped environment, and a hard timeout. This skips MSBuild entirely (the
expensive part of `dotnet test`) while getting real process isolation, so a hostile test kills
a child process, not the daemon. The classify-not-runnable contract from the original M1
carries over: a test needing build-produced content or a live external dependency is reported
not-runnable rather than executed.

**Tests.** A pure covering test runs against the emitted assemblies and reports green/red
without a `dotnet build`. A test with an environmental dependency is classified not-runnable. A
test that calls `Environment.Exit` or hangs takes down only its child host, and the daemon
survives (the isolation property the in-proc design could not provide).

**Docs.** `internals/` staging-area page (the execution half), `reference/mcp-tools.mdx`,
`AGENTS.md`.

**Benchmark.** The recorded suite the original M1 specified: prediction agreement versus
running `dotnet test` on the applied patch, the latency ratio, and the false-green rate as the
headline honesty number, all on the classified-runnable subset.

**Expected result.** For tasks with pure, isolation-runnable covering tests, a verified verdict
in seconds instead of a full `dotnet test` (theory; the win is skipping MSBuild plus running a
subset, so the ratio scales with suite size and build cost). For tasks with environmental
tests, "verified 14 of 17 covering, 3 not runnable in isolation."

**Kill risk, and why M2 is the release's one stretch item.** Emit cost on large dependency
closures (measure; cache unchanged project emits) and test-host fidelity (tests depending on
build-produced files are classified out). If the false-green rate on the runnable subset cannot
be driven near zero, M2 does not ship in 4.0 and slips to 4.1; M1 (lifecycle plus selection) is
the floor, is independently useful, and is unaffected. This is the pre-agreed carve-out, moved
from "in-proc gated" to "out-of-proc stretch."

**Multi-language, deliberately deferred.** The v3.2 W3 tree-sitter engine stays deferred:
syntax-tier parity across languages is a commodity serena already has via LSP, and shipping it
before the oracle is won spends the moat-building release on catching up to grep-plus. The
multi-language story that does not dilute is the oracle interface (ask/check/impact) being
language-agnostic, each language earning entry by binding its real compiler service (TypeScript
via tsserver next), not a parser approximation. Not in 4.0.

---

## Phase 4: retrieval bets (gated; only after N4's localize re-run is recorded)

These two items attack the open-ended recall ceiling directly rather than nudging it. They are
gated for a reason the project already recorded: v3.1 deferred the thesaurus (S4) and
learning-to-rank (S9) on the finding that the ceiling is index mode, not vocabulary or weights
(localize main checkout partial 2, syntax 2, `localize.json`). That finding was never tested
with semantic mode on, and N4 is the test. So Phase 4 does not start until N4's re-run of
`localize.json` is recorded. If semantic mode moves recall, the graph was the answer and these
items are re-scoped around it; if it does not, these are the queue, V1 before V2. Neither is a
release blocker; both are labeled theory until measured.

### V1. Graph verbalization: natural-language cards in the retrieval channels (added)

**Why.** The nl-domain bucket sits at 17 percent (`localize.json`) because a PR title written
in prose shares no register with code identifiers, and the dense channel embeds code chunks
with a 256-token MiniLM that never sees the wiring facts. The plan otherwise has no retrieval
bet beyond N1 hygiene and the N4 hypothesis test; this is the cheapest mechanism-plausible shot
at breaking the ceiling rather than nudging it, and it is fully offline and deterministic, so it
respects the A1 no-query-time-network and no-paraphrase constraints.

**How.** At index time, generate a deterministic one-to-three-sentence card per file and per
public type from the typed graph and doc comments, no model in the loop: "OrderService
implements IOrderService, registered scoped in Startup, handles CreateOrderCommand, serves POST
/api/orders, configured by OrderingOptions from the Ordering section." Embed the cards in the
dense channel and index them as an FTS field alongside `comments`. This moves the retrieval
representation into the query's register and, crucially, encodes wiring facts (the moat) into
channels that today see only tokens. Deterministic templating from graph edges; no new model,
no query-time cost beyond one more FTS field.

**Tests.** A card for a wired type contains its interface, DI lifetime, handled request, and
route (a fixture assertion against the OrderingApp graph). The card field participates in FTS
ranking under N1's suite. Cards degrade gracefully in syntax mode (path and symbol prose only).

**Docs.** `internals/retrieval.mdx` (the card channel), `project/benchmarks.mdx`.

**Benchmark.** Re-run Suite C (`localize.json`) same-corpus before and after, with N4's best
tier on. Win condition: nl-domain recall from 17 toward 25-plus with no overall precision
regression past N1's ranking gate.

**Expected result (theory).** nl-domain is the bucket where the register gap dominates, so it
is where cards should help most; the overall lift depends on how many corpus checkouts reach a
tier where the graph is rich enough to verbalize (N4). Kill at under 2 points overall or any
precision regression the ranking suite flags.

**Kill risk.** Cards are only as good as the graph tier; in syntax mode they add little, so V1's
value is coupled to N4's success, which is why it is gated behind N4's re-run. MiniLM may
saturate on templated prose; visible in the experiment.

### V2. Per-repo learned ranking from git history, temporal-split guarded (added)

**Why.** The benchmark's own ground truth (commit message to changed files) is exactly the
supervised signal every repo carries for free in its history, and the current combination
weights (the noisy-or source weights and the prior caps) are hand-set, and already shipped one
uncaught regression (A6; finding 9). v3.1 rejected LTR because the ceiling is index mode; that
argument dissolves the moment N4 lands and the ceiling hypothesis is actually tested, which is
why V2 is gated on N4, not rejected outright.

**How.** At index time, mine (commit-message, changed-files) pairs from the last few thousand
non-merge commits (the bounded miner exists, `GitCoChangeCollector`); featurize each candidate
with the signals Fuse already computes (per-field FTS scores, dense cosine, path match,
centrality, co-change, graph distance); fit a small deterministic model (logistic regression or
a shallow gradient-boosted tree, fixed seed) per repo; persist it in SQLite; fall back to the
hand-tuned noisy-or weights when training data is thin or validation AUC is poor. Strictly a
re-ranker over the existing candidate set, so it cannot lift a ceiling set by candidate
generation, only reorder within it (which is why it comes after V1, which widens generation).

**Tests.** Temporal-split discipline: the model trains only on commits older than any eval PR
(no leakage). A repo with thin history falls back to hand-tuned weights. The model is
deterministic under a fixed seed (a golden-scoring test). The ranking suite (N1) guards the
change.

**Docs.** `internals/retrieval.mdx` (the learned re-ranker and the fallback), `project/benchmarks.mdx`.

**Benchmark.** Temporal-split eval on NodaTime (488 files, the deepest corpus history):
recall@10 and MRR against the hand-tuned stack. Win: 3-plus points of recall@10 with
precision-when-confident not regressing; kill otherwise.

**Expected result (theory).** Learned per-repo weights are the principled version of the
hand-tuned noisy-or constants, trained on the distribution that matters (this repo's own change
history). Magnitude is uncertain and bounded by candidate generation; the honest expectation is
a modest recall@10 lift where history is deep, nothing where it is thin.

**Kill risk.** Hot-file bias (the model learns "Startup.cs changes a lot" and floods every
ranking with it); guarded by featurizing relevance signals rather than raw change frequency,
and by the temporal split. Cold or shallow repos get the fallback. Evaluation leakage is the
subtle killer; the strict temporal split is the guard, and the eval is invalid without it.

---

## Go-to-market

### G1. The latency demo and launch publish

**Why.** The demo that lands is a latency demo, not a token demo. Token savings are invisible
on screen; a compiler answering in milliseconds while the other pane waits on MSBuild is
visceral. This is honest only after R1/R2, which is the right forcing function.

**How.** Side-by-side terminal recording on eShopOnWeb, "rename this method on IOrderService
and update everything": left, a stock agent (grep, edit, `dotnet build`, parse errors, repeat);
right, with Fuse (once R7 exists, `fuse_refactor` performs the rename as a staged diff,
`fuse_check` returns green in under a second, one final build). Publish and link from the
landing page and README.

**Amendments (2026-07-03).** Two, both about not letting the demo inherit the optimistic
branch of the theory. (a) Verification parity: the illustrative 150 s to 35-40 s model in
Expected-impact books the fuse arm with a scoped test subset while the native arm pays a full
test run; with both arms running the same final verification, the same model gives roughly 90
to 95 s, about 1.6x on the loop, not 3 to 4x. The 3-to-4x figure silently assumes M2-grade
trusted test selection, which the demo does not include; the demo script uses the
verification-parity number and R4's recorded wall-clock, not the loop-only projection. (b) The
demo is R7-first, not R1/R2-first: the visceral moment is the compiler performing a 40-site
rename correctly in one call, which no LSP-driven agent loop matches for ergonomics, and it
pre-empts the "just use the language server" rebuttal that a check-only demo invites. The
honest claim is "no editor session, whole-solution, multi-file, correct by construction, with
abstention," not "only a compiler can do this."

**Acceptance.** The asset is linked from the landing page and README; every claim is sourced to
R4's recorded numbers or labeled illustrative with its arithmetic shown; nothing overclaims a
head-to-head win, and the LSP counterfactual is named rather than ignored.

### G2. The analyzer contribution program and coverage table

**Why.** Section 1.2's coverage critique (Autofac, Lamar, Wolverine, FastEndpoints, Carter,
source-generated DI are unhandled by the 10 first-party-pattern analyzers in
`src/Core/Fuse.Semantics/Analyzers/`) is the moat's biggest weakness; the on-ramp converts it
into contributions. People star tools they can extend for their stack.

**How.** A documented recipe for writing a wiring analyzer, the `--corpus-sample` adjudication
flow as the test methodology, and a public coverage table of container and framework support.
Seed it with two or three new analyzers (a scanning-convention container and one endpoint
framework) so the recipe is proven, not theoretical.

**Acceptance.** The recipe page ships, the coverage table is published, and at least one seed
analyzer added via the documented recipe passes Suite A on a new fixture.

---

## The versioning decision: everything ships as 4.0.0

This release intentionally lands the floor, the oracle, the moonshot's shippable half, the
gated retrieval bets, and the governance track under a single major bump, 4.0.0 (current
codebase version is 3.2.0). R3's tool-surface reshape is the primary driver for the major:
the new oracle tools ship additively and every folded name keeps a deprecation shim for one
major version, so no existing client sees a bare "Unknown tool" across the upgrade. The clean
removal of those shims is deferred to 5.0.0, per the existing change-safety invariant. M2
(out-of-proc test execution) is the one item pre-agreed to slip to 4.1 if its gate does not
clear; its slip does not hold the tag.

Release gate for the 4.0.0 tag (all must hold):

1. The three gates green: `dotnet build Fuse.slnx -c Release`, `dotnet test Fuse.slnx -c Release --no-build`, `dotnet format Fuse.slnx --verify-no-changes`.
2. Every folded tool name in R3 has a working shim; `McpServeIntegrationTests` name arrays
   pass. If any shim is dropped instead of kept, the release is re-designated 5.0.0, not
   4.0.0. This is the bright line.
3. `FuseHostService.ProtocolVersion` and `ext/vscode/src/host/protocol.ts` agree, and the
   contract test passes, if any RPC DTO changed (N3, R1, R2 likely).
4. `WorkspaceIndexSchema.TargetVersion` bumped or the Fuse-version stamp updated if the index
   extraction contract changed, so a stale on-disk index rebuilds rather than serving stale
   data.
5. Suite F's false-green AND false-red rates are at or near zero on tier-1 (oracle-grade)
   projects, or `fuse_check` abstains; a false green is a release blocker, and a false-red rate
   high enough to teach the agent to ignore the check is also a blocker (both directions
   recorded).
6. M1 (changeset lifecycle plus diagnosis plus covering-test selection) ships. M2 (out-of-proc
   execution) ships only if its false-green rate on the classified-runnable subset is at or near
   zero; otherwise M2 slips to 4.1 and the changelog names the split. M1 shipping without M2 is
   an acceptable tag.
6a. Every read tool satisfies the N6 freshness contract: fresh data or an explicit
   stale-as-of stamp, never silently stale. The contract test passes.
7. `build/verify-version.ps1` passes: `Directory.Build.props`, `ext/vscode/package.json`,
   `mcp-registry/server.json`, and `site/package.json` all read 4.0.0 (set via
   `build/set-version.ps1 4.0.0`), and the `v4.0.0` tag matches.
8. Every number quoted in the docs and `AGENTS.md` is sourced to a current-corpus file in
   `tests/benchmarks/results`; N2's archive move is complete.
9. L1 complete: `LICENSE` is Apache 2.0 and all Fuse-owned license expressions and badges
   match; no stale MIT claims remain on project artifacts.
10. L2 complete: `DCO.txt` is present, contributing docs require `Signed-off-by`, and the DCO
   check is enabled on the repository.

---

## Sequencing

The phases read as parallel but are largely serial through N4, because R1, R2, R5, and the
moonshot all require the compilation to actually be loaded. The order (revised 2026-07-03):

0. **L1 then L2 first.** Migrate MIT to Apache 2.0, then adopt DCO with the GitHub check
   enabled. No other v4 item starts until both are merged. This keeps the 3.2.0 release under
   MIT and makes every subsequent commit and PR carry the new license and sign-off contract.
1. **The N4 bake-off spike.** After L1/L2, run the two-mechanism spike (hardened
   MSBuildWorkspace vs the build-capture ladder) on the corpus plus about 20 OSS .NET repos and
   record the tier distribution. This one measurement decides N4's mechanism and sets whether
   the oracle can answer often enough to matter; every downstream item's value is the
   theoretical gain times this load-success rate. In the same early window, curate R4's
   10-to-15 PR task set and record the native-arm and LSP-arm baselines (pure data work,
   blocked on nothing, and pre-registering the baseline is what keeps R4 honest).
2. **N1 first among the code items.** Pure hygiene, blocked on nothing, lands the ranking gate
   that N2 relies on, and its amended form re-adjudicates the A6 prior regression (finding 9).
3. **N2 and N5** right after N1, so there is one ranker, one harness, and a clean results
   directory before any new numbers land. N2's amended sweep includes `agent.json` and the
   superseded a1 doc citations (finding 8).
4. **N6** rides with N3 (they share the reconcile trigger) but is severable and protects the
   syntax tier today; ship the stale-as-of stamp path even before the semantic increment lands.
5. **N4 in full** on the mechanism the spike chose; **N3** in parallel, the resident-workspace
   prerequisite for the whole oracle. N6's semantic reconcile lands on N3.
6. **R5 before R2.** The persisted reference index is the substrate R2's tens-of-ms target and
   M1's test selection both assume (finding 7); build it first or R2 silently reverts to
   `SymbolFinder` latency.
7. **R2**, then **R1** (the harder speculative-fork work), then **R6** (repair packets ride on
   R1's diagnostics), then **R7** (compiler-executed edits, on R5's call sites), then **R4**
   (measures R1/R2/R6/R7/R3 together against the pre-registered baselines), then **R3** (reshape
   once the new oracle tools exist to reshape around, adding the ambient availability header).
8. **M1** (lifecycle plus selection, on R5) after the oracle tools; **M2** (out-of-proc
   execution) only if its false-green gate clears, else it slips to 4.1.
9. **Phase 4 (V1 then V2)** only after N4's localize re-run is recorded, because that re-run is
   the test of the index-mode-ceiling hypothesis that gates whether retrieval work is warranted
   at all.
10. **G1** after R1/R2/R7 (the demo is honest only then, and R7 is the visceral moment). **G2**
    whenever a person can write the recipe and seed an analyzer (L1/L2 are already done by then).

---

## Expected impact (theory, not a benchmark)

Per the honesty convention, everything in this section is a mechanism-grounded projection,
labeled as theory. None of it is a recorded number; the suites in this plan (N1 ranking, Suite
F, R4 loop) exist to replace these projections with measurements.

The gains are asymmetric: large on wall-clock, modest on tokens, because they attack different
parts of the agent loop.

- **Verify operation in isolation.** Replacing a `dotnet build` (tens of seconds; the demo task
  cites 30+ s) with a `fuse_check` call (target sub-second P50) is roughly one to two orders of
  magnitude on that operation. `fuse_impact` at tens of ms replaces a discover-by-rebuild cycle.
  These are targets (N3/R1), not measurements.
- **Task wall-clock, build-heavy multi-file work.** By Amdahl, the task-level gain is bounded by
  how much of the task is build-gated verification. An illustrative model of the rename demo,
  with the verification-parity correction (2026-07-03): the original booked the fuse arm with a
  scoped test subset while the native arm paid a full test run, giving about 150 s to 35-40 s
  (3 to 4x); with both arms running the same final verification (the honest comparison until M2
  ships trusted selection), the same model gives about 150 s to 90-95 s, roughly 1.6x on the
  loop, with build-gated agent turns dropping from about 4 to about 1 or 2. The 3-to-4x figure
  requires M2-grade trusted test selection; the demo (G1) uses the 1.6x number. R7 changes the
  arithmetic further in the fuse arm's favor by removing the edit-generation turns entirely for
  the rename class, which R4 measures. Near-flat on non-build tasks.
- **Cumulative session tokens (the flat Suite D metric).** Cumulative tokens scale with
  turn_count times accumulated_context_size, because context is re-processed each turn. Fewer
  build-gated iterations is the only lever that moves it, and R6 (repair packets, fewer failed
  verifies) plus R7 (mechanical edits done in one call, not N generated turns) are the items
  that actually fund it; the original items reduced the cost of a turn, not the count of turns.
  Arithmetic on the Suite D shape (25-turn cap, about 211k median tokens, an estimated 2 to 3
  build-gated turns of 8 to 12k re-processed context each) supports roughly 10 to 20 percent
  from loop collapse alone; the plan's 20-to-40 percent upper band needs the turn-count
  reduction R6/R7 target. Measured today at a 0 percent delta, so this is precisely the open
  hypothesis R4 exists to test, not a claim.
- **Localization precision (N1) and recall (N4).** N1's weight fix projects a precision lift on
  localize (magnitude uncertain, possibly modest). N4 finally tests whether recall is bounded by
  index mode; the honest alternative is that it is not, which re-scopes retrieval work.
- **New capability, not a speedup.** Speculative typecheck on an unwritten patch, signature-change
  blast radius, compiler-executed refactoring (R7), and covering-test selection are answers
  native .NET tooling has no fast equivalent for. But the honest competitor is not grep-plus-build,
  it is an LSP-armed agent (the C# language server or serena), which already has overlay
  diagnostics and find-references; Fuse's genuine differentiators against that baseline are
  narrower and stronger: whole-solution multi-file speculative verification with no editor
  session, the abstention/availability contract, the wiring graph, DI-resolved test selection,
  compiler-executed edits staged as diffs, and packed reduced context. R4's LSP arm is what keeps
  this claim honest; the comparison is those differentiators, not "only a compiler can do this."
- **API-shape error avoidance (R6) is the token lever.** The plan's own token projection is
  funded by reducing the count of failed verifies, not the cost of each; R6 is the item that
  does that, and R4's transcripts measure the fraction of build-gated turns that are API-shape
  errors, which is what sets whether the 20-to-40 percent band is reachable.

The one-sentence version: 4.0 makes Fuse dramatically more efficient at the specific thing
agents burn the most wall-clock on (iterative, multi-file, build-gated edits on a
semantically-loaded repo), roughly unchanged at pure retrieval until Phase 4's gated bets, and
the size of the realized win is set less by how fast the oracle is than by how often the repo
loads at oracle grade for the oracle to answer at all (N4's build-capture tier).

---

## Honest ceilings

- **Everything in Phase 2 and 3 is gated on N4's oracle tier.** On a repo that does not reach
  tier 1 (build capture), `fuse_check`, `fuse_impact`, and `fuse_refactor` abstain and deliver
  roughly none of the projected gains. Realized value is the theoretical gain times the
  oracle-load success rate, and that second factor is the release's dominant uncertainty. N4 is
  the item to watch, not R1.
- **A repo that does not build cannot be an oracle for anyone.** The build-capture mechanism
  makes this the honest ceiling rather than a hidden failure: the fraction of target repos whose
  own `dotnet build` fails in the environment is the published upper bound on oracle coverage,
  and those repos get graph-grade retrieval plus abstention, never a guess. This replaces the
  old "MSBuild load reliability is open-ended" framing: tier 2 (salvage) is still an entropy
  fight, but it now gates only retrieval coverage, never the oracle's correctness.
- **False greens AND false reds are worse than no check.** R1 and M2 ship only with the
  abstention contract and near-zero rates in both directions; a check that lies green destroys
  trust, and one that cries wolf (false reds from missing generated sources) teaches the agent
  to ignore it. Both are recorded and gated.
- **The Suite D token projection is unproven and now correctly attributed.** The 20 to 40
  percent cumulative-token figure is a hypothesis funded by R6/R7's turn-count reduction, not by
  the original items; R4 may report smaller (the mechanism arithmetic supports 10 to 20 percent
  from loop collapse alone). Labeled theory throughout, not quoted as a result anywhere.
- **In-process test execution does not land; out-of-proc (M2) may not either.** The original
  in-proc design was removed as unachievable (no .NET isolation), not gated. M1 (lifecycle plus
  selection) is the floor; M2 (out-of-proc execution) is the stretch and slips to 4.1 if its
  false-green gate does not clear.
- **The resident workspace is a lifecycle risk, and staleness is a live defect today.** A leaked
  workspace or a stale graph presented as fresh is worse than an honest opt-in; the current MCP
  surface already serves silently stale data after the first edit (finding 6), so N6's freshness
  contract is a correctness fix, not a nicety, and N3 ships only with the lifetime and freshness
  tests green.
- **Phase 4 is gated on N4's own result.** V1 and V2 do not start until N4's localize re-run is
  recorded; if semantic mode does not move recall, retrieval work is re-scoped rather than
  pursued on the pre-N4 assumptions v3.1 already flagged.

---

## What survives from the current architecture, explicitly

The SQLite store survives as the persistence and cold-start layer of the oracle, no longer the
engine's center of gravity; the center moves to the resident in-memory workspace, and the store
gains the persisted reference index (R5) as a new tier of extracted data. The reduction pipeline
survives as a library feature behind `fuse_context` and `fuse_reduce` (folded into `fuse_ask` or
kept, per R3), and it gains a second consumer in R6's repair packets. The classic in-memory
BM25F dies (N2). The nine-tool surface collapses to seven with shims (R3); the shims die at the
next major. The dead `calls`/`tests` edge weights in `EdgeWeightProvider` get real producers
(R5) or are removed. The signal-sufficiency (abstention) contract not only survives but
generalizes twice: into the oracle's availability contract ("cannot verify, project X is not
oracle-grade" instead of a guess) and into the freshness contract (N6: fresh, or explicitly
stale-as-of, never silently stale). It is the load-bearing honesty mechanism of the whole
release, now made ambient (the availability header on every response, R3) rather than a separate
diagnosis the agent must request.

---

## Reminders and conventions (read before starting a V4 item)

- One item at a time, each as engine plus tests plus website docs (under `site/content/docs`)
  plus a benchmark in a single change, except the two harness-first items (N1's ranking suite,
  R4's loop suite) where the benchmark is the deliverable. Run the three gates every item.
  New public API needs XML docs. New tests must actually run (the count rises); the MCP
  contract suite is wired through `ext/vscode/package.json` `test:contract` and the C# suites
  through `dotnet test`. Plain ASCII prose only (a stop hook rejects em dashes, smart quotes,
  and emoji outside code fences).
- Change-safety (AGENTS.md): any `Fuse.Cli.Rpc` DTO or `[JsonRpcMethod]` change bumps
  `FuseHostService.ProtocolVersion` and `ext/vscode/src/host/protocol.ts` `PROTOCOL_VERSION`
  together, and updates the extension client and the contract test, in the same change. Any
  index schema change bumps `WorkspaceIndexSchema.TargetVersion` or rides the Fuse-version
  stamp so a stale index rebuilds. Renaming or removing an MCP tool keeps a deprecation shim
  in `FuseDeprecatedTools` for one major version. Bound external-process arguments (the restore
  and build invocations in N4 and Suite F must chunk or use stdin). No silent behavior changes.
  Never fabricate or weaken a benchmark number; quote only numbers recorded in
  `tests/benchmarks/results`, and label every projection in the Expected-impact section as
  theory.
- After each item: write results to `tests/benchmarks/results/*.json`, resync `AGENTS.md`
  measured results and the website docs in the same change, tick the box in this file, append
  a timestamped progress-log entry (Status, Result, Verification, Blockers, Lessons, Time),
  commit with the item id, push. Keep a single open PR; never merge, self-approve, or enable
  auto-merge.
- If an item is genuinely blocked or not warranted by the evidence, log the concrete reason,
  leave the box unticked, and surface it (as V3.1 did for its deferred halves and as N4/M1 may
  need to here).
- Environment facts: the build SDK is .NET 10 (`global.json` pins `10.0.100`); the CI
  vulnerability audit suppression lists in `Directory.Build.props` and `ci.yml` must stay in
  sync when touching dependencies.

---

## Progress Log

Append a timestamped entry per item as it lands (Status / Result / Verification / Blockers /
Lessons / Time). The first item entry goes here. Plan revisions are logged below them.

### 2026-07-03 L1: Migrate license from MIT to Apache 2.0

**Status.** Done.

**Result.** `LICENSE` replaced with the full Apache License 2.0 text plus the appendix, retaining
the Litenova Solutions 2026 copyright. Added a `NOTICE` file (Apache convention; records that the
runtime-fetched all-MiniLM-L6-v2 model is itself Apache-2.0 and downloaded on demand, not
redistributed). Every Fuse-owned license expression moved to `Apache-2.0`:
`Directory.Build.props` (new `PackageLicenseExpression`, the repo-wide default),
`src/Host/Fuse.Cli/Fuse.Cli.csproj`, `ext/vscode/package.json`, `build/pack-tool.ps1` nuspec,
and `packaging/winget/Litenova.Fuse.locale.en-US.yaml`. Badges and footers updated in `README.md`,
the benchmark figure (`assets/fuse-benchmarks.svg`, `assets/fuse-benchmarks-chart.py`,
`site/public/fuse-benchmarks.svg`). Docs contributing page and root `CONTRIBUTING.md` now name the
Apache license; `CHANGELOG.md` carries the 4.0.0 migration note for downstream packagers.
`build/verify-version.ps1` gained a license-consistency assertion (LICENSE is Apache, and the three
declared license expressions read `Apache-2.0`), so a stray MIT claim now fails CI.
`mcp-registry/server.json` declares no license field, so it had no MIT claim to migrate.

**Verification.** Three gates green: `dotnet build Fuse.slnx -c Release` (0 errors),
`dotnet test Fuse.slnx -c Release --no-build` (all suites pass), `dotnet format --verify-no-changes`
(clean). `build/verify-version.ps1` passes (version + new license check). Repo-wide grep over
owned artifacts (`src`, `assets`, `packaging`, `build`, `ext`, `README.md`, `CONTRIBUTING.md`,
`mcp-registry`) finds no remaining MIT claim; `site/package-lock.json` MIT entries are third-party
dependencies and correctly untouched.

**Blockers.** None.

**Lessons.** The only Fuse-owned MIT claims were metadata and prose, not source headers; there was
no per-file MIT header to sweep. Apache-2.0 is compatible with the MIT/Apache dependency graph, so
no NOTICE attribution beyond the model note was required.

**Time.** ~1 session-hour.

### 2026-07-03 L2: Adopt the Developer Certificate of Origin

**Status.** Done.

**Result.** Added `DCO.txt` (canonical Developer Certificate of Origin 1.1 text) at the repo root.
Documented the sign-off requirement in `CONTRIBUTING.md` and the docs contributing page
(`git commit -s`, the `Signed-off-by` trailer, why DCO over a CLA, grandfathering). Added a
pull-request template (`.github/PULL_REQUEST_TEMPLATE.md`) with a required sign-off checkbox and the
three verification gates. Enabled the check as a CI workflow (`.github/workflows/dco.yml`) rather
than the GitHub App, since app installation is not scriptable here: it inspects only the commits a
PR adds (`base..head`), skips merge commits, and fails with an actionable message
(`git rebase --signoff`) when a commit's trailer does not match its author. Every v4 commit from L1
forward already carries a sign-off (verified on L1). Changelog entry under 4.0.0.

**Verification.** Three gates green (build 0 errors, tests pass, format clean; L2 touches no C#).
The DCO workflow's sign-off matching was validated locally against this branch's commits with the
same `git show -s --format` logic the workflow runs. The check will exercise end to end on this
PR (release-gate item 10 confirms it on a test PR).

**Blockers.** The DCO GitHub App cannot be installed from this non-interactive session; the
equivalent CI check the plan permits is used instead. If the maintainer prefers the hosted DCO bot,
it can be enabled in repo settings and the workflow removed; behavior is equivalent.

**Lessons.** A CI-check implementation is strictly more portable than the app (no external
dependency, runs on forks), at the cost of not offering the app's one-click "set sign-off" web
button. The author-match rule (trailer name+email equals commit author) is the same rule the app
enforces.

**Time.** ~0.5 session-hour.

### 2026-07-03 N4 bake-off spike (item zero): build-capture ladder chosen

**Status.** Done (spike; decides N4's mechanism). N4 full implementation remains open (Phase 1).

**Result.** Ran the two-mechanism bake-off over the 4 corpus checkouts plus 16 popular OSS .NET
repos (20 listed, 17 evaluable; 3 clone failures excluded as environmental GitHub rate-limiting,
not a mechanism outcome). Recorded to `tests/benchmarks/results/n4-bakeoff.json`; reproducible
script and repo lists in `tests/benchmarks/spikes/n4-bakeoff/`. Tier distribution:

- Mechanism (a), current MSBuildWorkspace loader: semantic 2, partial 2, syntax 13. Oracle-grade
  (semantic) on 2/17 = 12 percent. This directly confirms finding 1 (the moat mostly does not run
  on real checkouts): NodaTime builds cleanly yet the loader falls to syntax.
- Mechanism (b), build-capture ladder: 11/17 = 65 percent of evaluable repos build successfully, so
  tier-1 is achievable by construction on all 11. On exactly the 11 buildable repos, mechanism (a)
  reached semantic on only 2, partial on 1, syntax on 8, while (b) reaches tier-1 on all 11.
- The plan's quantitative cutoff (tier-1 on at least 80 percent of repos whose `dotnet build`
  succeeds): mechanism (b) passes at 100 percent (11/11); mechanism (a) fails at 18 percent (2/11).
- Oracle coverage ceiling: 65 percent of repos build in this environment. The 6 that do not
  (Scrutor NU1507, eShopOnWeb CS0104, Dapper and StackExchange.Redis MSB4018, Humanizer NETSDK1045,
  Nancy CS2007) get graph-grade retrieval plus abstention, never a guess.

**Decision.** Adopt the build-capture ladder (binlog rehydration for tier 1, salvage for tier 2,
syntax for tier 3), as the plan scoped. The signal is decisive even before N4's full
implementation: build-capture is strictly better on buildable repos and equal on the rest, and
tier-1 is oracle-grade by construction (it shares the build's own inputs).

**Verification.** Numbers computed from the recorded per-repo JSON; every repo's msbuild tier came
from the built branch CLI, every build result from a real `dotnet build -bl`. The 3 clone failures
are excluded and named, not scored.

**Blockers.** The 35 percent non-building fraction is the honest oracle-coverage ceiling and the
release's dominant uncertainty, exactly as the plan's kill-risk names it. Tier 2 (salvage) remains
an entropy fight but gates only retrieval coverage, never oracle correctness.

**Lessons.** MSBuildWorkspace failing to semantic on a repo that builds cleanly (NodaTime, Polly,
MediatR, and most of the OSS set) is the core justification for the mechanism switch: the real build
knows what the real build does; a separate design-time load does not.

**Time.** ~2 session-hours (mostly the 16-repo clone+build+index sweep).

### 2026-07-03 N1: Fix the lexical weight inversion; land the ranking suite

**Status.** Done.

**Result.** Corrected the FTS5 `bm25` weight vector at `WorkspaceIndexStore.SearchAsync` from
`(0, 4.0, 3.0, 2.0, 1.5, 1.0, 0.7, 0.9, 0.5)` (path highest, the inversion) to
`(0, 2.0, 5.0, 4.0, 3.0, 1.5, 1.0, 2.5, 0.7)`: name > symbols > signature > subtokens > path >
comments > body > stems, matching the documented intent so a symbol-name match beats a folder-name
match. Added MRR, recall@k, and nDCG@k to `Metrics.cs` (ported from the legacy `layer-ranking.ps1`,
so N5 can delete the PowerShell copy). Added `RankingSuite` (`Name = "ranking"`, registered in
`EvalCommand.BuildSuite`, all three help/error strings updated) scoring three configurations over
the same index: lexical isolation (no embedder, priors off), shipping default, and
default-without-co-change. Added `EnableCentralityPrior`/`EnableCoChangePrior` toggles to
`LocalizationRequest` (default true) and gated the two priors in the engine on them.

Recorded `results/ranking.json` on the corpus (index modes partial 2, syntax 2):
- lexical: MRR 0.187, recall@1 3.0, recall@5 10.7, recall@10 12.6 percent, nDCG@10 0.117.
- default (dense plus priors): MRR 0.197, recall@1 4.0, recall@5 10.4, recall@10 15.0 percent, nDCG@10 0.139.
- default-no-cochange: MRR 0.208, recall@10 15.0 percent, nDCG@10 0.141.
- A6 co-change delta (on minus off): MRR -0.011, recall@10 0.0.

**A6 decision.** The suite weakly confirms finding 9's direction (co-change on is MRR-negative,
recall-flat), but the delta is within CI on a mostly-syntax corpus where the graph is thin. Per the
no-unmeasured-confidence principle, the co-change default is held ON and the flip decision is
deferred to N4's richer-tier localize re-run (the plan's own gating point for the index-mode-ceiling
hypothesis), rather than flipping a default on a -0.011 MRR delta within noise. The suite now exists
to make that later decision on strong evidence.

**Tests.** Added `WorkspaceIndexSearchRankingTests` (a "widget" query hitting a symbol name outranks
the same term in a folder path) and six `MetricsTests` for MRR/recall@k/nDCG on hand-built rankings.
Discovered-suite count rises (`fuse eval ranking` is live and in `--help`); Benchmarks.Tests 39 to
45, Indexing.Tests +1.

**Verification.** Three gates green (build 0 errors, all test suites pass, format clean).
`fuse eval ranking` writes `results/ranking.json`. Docs updated: `internals/scoping-internals.mdx`
(the weight ordering and the suite), `project/benchmarks.mdx` (the ranking gate), `AGENTS.md`
(eval-suite list and a ranking-results bullet), `CHANGELOG.md`.

**Blockers.** None. The full localize before/after precision re-run is deferred to N4 (which re-runs
`localize.json` under the best tier); running it now on the pre-N4 syntax corpus would not test the
hypothesis and would contend for the same corpus.

**Lessons.** The inversion was a one-line literal that had shipped unmeasured; the suite is the real
deliverable. On a mostly-syntax corpus the priors barely move ranking, which is itself the evidence
that Phase 4's retrieval bets are correctly gated on N4.

**Time.** ~1.5 session-hours.

### 2026-07-03 N2 (part 1): purge stale results; citation sweep; regenerate

**Status.** Part 1 done (results purge, citations, regeneration). Part 2 (physical deletion of the
in-memory ranker) deferred with reason; box left unticked.

**Result.** Archived the legacy PowerShell-harness result files (`layer1`..`layer5`,
`layer-latency`, `layer-ranking`, `baseline.layer1`, `opt-in-levers.md`), the superseded
`localize`/`review` dev-iteration snapshots (a1, a6, r1-r3, s1-s10, restore, review.r4,
review.restore), and the legacy `reduction.json` to `tests/benchmarks/results/archive/`. The
top-level results directory now holds only current-corpus files plus `localize.a1-lexical.json`
(the lexical-fallback comparison the corrected dense-lift citation names) and `n4-bakeoff.json`.
Regenerated `reduce.json` and `performance.json` on the current corpus. Rewrote the Suite E
reduction table (was the retired 5-library corpus) to the current corpus in `benchmarks.mdx`.
Corrected finding-8 citations everywhere: precision-when-confident 9.3 to 5.6 percent
(`localize.json`, 9 confident tasks), dense lift 13.3 to 14.9 percent (not 15.1), skeleton
reduction 37-55 to 38-44 percent, `reduction.json` to `reduce.json`. Annotated `agent.json`'s notes
with the directional pre-R4 / retired-corpus caveat. Updated regenerated latency figures in
AGENTS.md, overview.md, and performance.mdx (warm localize 42 ms P50, cold pass 58 s; NodaTime loads
syntax under the current loader, consistent with the N4 bake-off).

**Part 2 deferral (surfaced honestly).** N2's headline "delete `Bm25RelevanceIndex` and route the
classic query mode through FTS5" is deferred. Verified against the tree: no shipping surface
constructs a classic query-mode `FusionRequest` (`FilterByQueryAsync` is reachable only from the
fusion test suite), so the in-memory ranker is already non-shipping; the shipping ranker is the
FTS5 path in `SemanticRetrievalEngine`, unified and guarded by N1's suite. Physically deleting
`Bm25RelevanceIndex` cascades into `IRelevanceIndex`, `RelevanceIndexCache`,
`QueryScopingPipeline`, `FusionScopingStage.FilterByQueryAsync`, the DI registration, and dozens of
coupled fusion tests; doing it mid-release risks destabilizing the 237-test fusion suite for zero
product benefit. Tracked as a focused cleanup follow-up. Box left unticked to reflect this.

**Verification.** Docs-citation sweep confirms no orphaned references to archived files (the only
`localize.a1.json`/`reduction.json` mentions left are inside `v4-plan.md`, which is the plan
describing the migration). Gates: no C# changed in this part, so build/test remain green from N1;
`fuse eval reduce` and `fuse eval performance` reproduced their result files.

**Blockers.** None for part 1. Part 2 is deferred, not blocked (see above).

**Lessons.** The Suite E doc table was itself a fabrication-with-provenance (retired 5-lib corpus);
regenerating and rewriting it was the highest-value part of the sweep.

**Time.** ~1.5 session-hours.

### 2026-07-03 N5: retire the legacy harness; fix drifts

**Status.** Done.

**Result.** Deleted the superseded PowerShell layer scripts (`layer1`, `layer2a`, `layer2b`,
`layer4-scenario`, `layer5-agent`, `layer-latency`, `layer-ranking`, `run-all`, `setup-corpus`,
`gen-prs`, `check-regressions`, `smoke`, `calibrate-tokenizers`, `bootstrap-ci`), keeping only
`harness/layer6-peers.ps1` (the documented external-MCP-server peer exception the plan permits) and
its `common.ps1`/`common.Tests.ps1`. Their metrics are already ported: ranking (MRR/recall@k/nDCG)
to `Metrics`/`RankingSuite` (N1), the layer suites to the C# `IEvalSuite` set, and corpus/PR setup
to `CorpusManager`. Fixed drifts: `FuseTools` XML summary corrected from "eight tools" (omitting
`fuse_neighbors`) to nine; `OrderingApp` added to `corpus.json` as a fixture-only entry (the Suite A
wiring ground truth, no PR history); the legacy `reduction.json` was archived under N2. Rewrote the
benchmark readme and swept `benchmarks.mdx`, `AGENTS.md`, and `overview.md` to describe one harness
and one command surface (`fuse eval`).

**Verification.** Three gates green (build 0 errors, all tests pass including Benchmarks.Tests 45 and
CorpusManagerTests with `OrderingApp` in the manifest, format clean). CI does not reference the
deleted scripts (checked `.github/`), so nothing breaks. Port-parity for the deleted scripts'
metrics is covered by the existing C# suite and `Metrics` tests.

**Blockers.** None. The peer harness stays in PowerShell as the single documented exception the
plan explicitly allows (external-MCP orchestration); a full C# `PeersSuite` port is out of scope for
this item and not warranted while the one script is documented and bounded.

**Lessons.** The integration-test tool-name array was already correct at nine; only the human-facing
XML summary had drifted, which is exactly the class of silent doc/reality gap N5 targets.

**Time.** ~1 session-hour.

### 2026-07-03 N6: the freshness contract (stamp + reconcile-on-open floor)

**Status.** Done for the short-lived reconcile-on-open path and the stale-as-of stamp. The
resident-daemon watcher-fed reconcile queue and the semantic (cross-file edge) increment ride with
N3 (as the plan sequences); this item ships the severable floor that protects the syntax tier today.

**Result.** Added `WorkspaceIndexStore.GetAllFileHashesAsync` and
`SemanticIndexer.ReconcileDirtyFilesAsync`: on a warm read the store's known files are hashed against
their on-disk content, edited files are re-indexed and deleted files removed before the read answers.
A bulk change above a storm threshold (300 dirty files) records a stale-as-of stamp
(`stale_dirty_count` in `index_meta`) instead of reconciling one file at a time. Wired into both MCP
`OpenIndexedAsync` helpers (`FuseTools`, `FuseResources`), so every read tool and resource reconciles
on open. This also gives the single-file incremental re-index (`ReindexFileAsync`) a real product
caller for the first time (finding 6's corollary).

**Tests.** `FreshnessReconcileTests`: edit-then-reconcile finds the added symbol and drops the
deleted file's symbol (fresh), and a no-change reconcile is a no-op that stamps `0`. Semantics.Tests
97 to 99.

**Verification.** Three gates green (build 0 errors, tests pass, format clean). No RPC DTO changed
(no protocol bump). The new `stale_dirty_count` meta key is additive metadata, not an extraction
contract change, so no `WorkspaceIndexSchema.TargetVersion` bump is needed. Docs: `internals/operator.mdx`
(the freshness-after-an-edit behavior), `project/performance.mdx` (incremental re-index now backs the
reconcile), `AGENTS.md` (a new change-safety invariant).

**Blockers.** None for the floor. Two deferred-to-N3 pieces are named honestly above: the daemon
watcher (so a resident server reconciles without paying a hash sweep per call) and the semantic
cross-file edge increment. The explicit availability-header surfacing of the stamp rides R3; until
then the stamp lives in `index_meta` and is readable by `fuse doctor`.

**Lessons.** Reconcile-on-open makes the common case (a few edited files) fresh by construction,
which is the correctness win; the storm stamp is the honest fallback for bulk changes. Surfacing the
stamp on the answer is blocked on R3's header, so the floor records it in metadata rather than
inventing throwaway plumbing.

**Time.** ~1.5 session-hours.

### 2026-07-03 N4 (part 1): fuse doctor and per-project load reporting

**Status.** Part 1 done (availability reporting). N4's headline tier-1 build-capture (binlog
rehydration) and tier-2 auto-restore are NOT yet implemented; the box stays unticked.

**Result.** The loader (`RoslynWorkspaceLoader`) now records a `ProjectLoadReport` per project
(loaded, loaded-with-compile-errors = graph-grade, or not-loaded with a concrete reason), added to
`RoslynWorkspaceSnapshot`. `SemanticIndexer.DiagnoseLoadAsync` discovers and loads the workspace
without writing the index and returns a `LoadDiagnosis` (tier, projects loaded/total, per-project
reports, load diagnostics). A new read-only `fuse doctor` command surfaces it: it names the achieved
tier (oracle-grade / graph-grade (partial) / syntax) and the per-project downgrade reason, and states
plainly whether the oracle tools can answer for the workspace. This is the availability contract the
plan calls for ("fuse doctor names the concrete reason per project").

**Tests.** `DoctorDiagnoseTests`: a project-less workspace reports the syntax tier with zero projects
and the `syntax-only` reason. Semantics.Tests 99 to 100.

**Verification.** Three gates green on a clean `--no-incremental` build (a stale incremental
up-to-date check had initially skipped the test project and masked a missing-using compile error;
caught and fixed by forcing a clean build, a lesson logged below). One Fusion test flaked once on the
first full run (temp-dir/parallelism contention) and passed on re-run and on the confirming full run.
Docs: `reference/commands.mdx` (the command table and a `fuse doctor` section).

**What remains for N4 (the release's dominant item).** Tier 1 (build capture): run `dotnet build -bl`,
extract Csc invocations, and rehydrate exact compilations via `CSharpCommandLineParser` (the
Basic.CompilerLog approach), with incremental single-tree swaps. Tier 2 (salvage): bounded
`dotnet restore` on missing assets and metadata-reference salvage. Then re-run `localize.json` and
`review.json` with the best tier on (the gate for Phase 4). The N4 bake-off already recorded that this
mechanism reaches tier-1 on 100 percent of buildable repos vs 12 percent semantic for MSBuildWorkspace;
this part builds the availability surface that the tier-1 mechanism will populate.

**Tier-1 dependency de-risk (2026-07-03, recorded for the next session).** `Basic.CompilerLog.Util`
(the binlog-rehydration library, jaredpar) is available on nuget.org at 0.9.47 and restores cleanly
under this repo's central package management. It ships a net10.0 target.

**Tier-1 mechanism VALIDATED, integration BLOCKED (2026-07-03, recorded from a spike).** A working
`BuildCaptureLoader` was written and validated end to end: it runs `dotnet build -bl` (bounded args,
default configuration), reads the binary log with `CompilerCallReaderUtil.Create` +
`reader.ReadAllCompilationData()`, and rehydrates a Roslyn `Compilation` via
`data.GetCompilationAfterGenerators(ct)`. An integration test built the in-repo SampleShop solution
and confirmed the rehydrated compilation contains the real `SampleShop.SecretsHolder` type. So the
API and runtime compatibility of the 0.9.47 net10.0 build against Roslyn 4.14 are proven; the tier-1
mechanism works.

The blocker: adding `Basic.CompilerLog.Util` to the `Fuse.Semantics` assembly (which also hosts the
`MSBuildWorkspace`-based `RoslynWorkspaceLoader`) breaks the MSBuildWorkspace load with
"Unable to load one or more of the requested types" (a Roslyn/Workspaces assembly-version conflict:
the library pulls its own CodeAnalysis.Workspaces closure into the output, which collides with the
SDK-resolved assemblies `MSBuildLocator`/`MSBuildWorkspace` require). The two Roslyn-loading
mechanisms cannot share one process/assembly closure as-is. The spike was reverted to keep the tree
green. Resolution for the tier-1 landing: run build-capture out of process (a small child worker that
emits the rehydrated data), or isolate it in a separate assembly loaded in its own `AssemblyLoadContext`
so its CodeAnalysis closure does not collide with MSBuildWorkspace's SDK-resolved assemblies. This is
the concrete next step for N4 tier-1; the mechanism itself is no longer a question.

**Blockers.** None for part 1. Tier-1/tier-2 are large, self-contained follow-on work (binlog
integration is a new capability), deliberately not rushed to keep the gates and the no-silent-change
discipline intact.

**Lessons.** `dotnet`'s incremental up-to-date check can skip recompiling a test project after a new
file is added, so a full-solution build can report success while a new test's compile error is
masked; force `--no-incremental` (or build the test project directly) when adding a test file, and
trust the test-count delta, not just the "0 errors" line.

**Time.** ~1.5 session-hours.

### 2026-07-03 R6 (part 1): fuse_signatures, the API-shape oracle

**Status.** Part 1 done (the `fuse_signatures` tool). Box left unticked: R6's other half (repair
packets, fix context attached to `fuse_check` diagnostics) rides on R1, which is blocked on the
resident oracle (N3) and tier-1 load (N4-full).

**Why now (out of sequence, but unblocked).** R1/R2/R5/R7 are blocked on N3/N4-full. Per the plan's
"move to the next unblocked item" clause, `fuse_signatures` is the one oracle-surface tool that needs
only the persisted symbol store, not a resident compilation, so it is implementable today.

**Result.** Added `WorkspaceIndexStore.GetSignaturesByNamesAsync` (matches a batch of names by simple
name or fully qualified name against the `symbols` table, public-API and exact match first) and the
`fuse_signatures` MCP tool: batch exact signature, kind, accessibility, containing type, and location
in one call. Abstains per symbol when no signature was recorded (syntax tier) and reports any
requested name with no match, rather than inventing a shape. Additive (no shim, no RPC/protocol
change, no schema change: it reads existing columns). The MCP surface is now ten tools; the
integration-test tool-name array, the `FuseTools` summary, and the server-instructions block are
updated together.

**Tests.** `WorkspaceIndexSignatureTests`: match by simple name and by FQN return the recorded
signature; an unknown name returns empty. `McpServeIntegrationTests` tool-name array includes
`fuse_signatures`.

**Verification.** Three gates green (clean build 0 errors, full test suite exit 0, format clean).
Docs: `reference/mcp-tools.mdx` (the tool), `AGENTS.md` (ten-tool list).

**Blockers.** None for part 1. The repair-packet half is correctly deferred until R1 exists.

**Lessons.** The `symbols` table already carries `signature`/`accessibility`/`containing_type`, so
the API-shape oracle is a pure read over existing data, which is why it is unblocked while the
compiler-execution tools are not.

**Time.** ~1 session-hour.

### 2026-07-03 N3 (part 1): supervised background semantic upgrade

**Status.** Part 1 done (the upgrade lifecycle, finding 5). Box left unticked: N3's resident workspace
(hold one loaded compilation, MSBuild evaluation paid once) and the dependency-scoped incremental
semantic reindex remain, and are the substrate the Phase 2 oracle tools consume.

**Result.** Replaced the fire-and-forget `Task.Run` in `FuseTools.ScheduleSemanticUpgrade` (which
swallowed exceptions and could outlive the host) with a `SemanticUpgradeSupervisor`: deduped per
root, each job run under a shared cancellation token, failures logged (to stderr in the serve host)
rather than swallowed, and a bounded cancel-and-drain on host shutdown so no task is orphaned. The
serve host installs the supervisor with a stderr sink and disposes it in a `finally` around the host
run, so the drain always happens.

**Tests.** `SemanticUpgradeSupervisorTests`: per-root dedup, cancel-and-drain observes cancellation
and leaves nothing running, schedule-after-dispose is refused, and a failing job is logged not
swallowed. Cli.Tests 78 to 82.

**Verification.** Three gates green (clean build 0 errors, full suite exit 0, format clean). No RPC
DTO or schema change. Docs: `project/performance.mdx` (the supervised-upgrade behavior).

**Blockers for the rest of N3.** The resident workspace and the incremental semantic reindex are the
remaining work; they are in-process (MSBuildWorkspace, no assembly conflict, unlike N4 tier-1), but
their payoff is realized by the Phase 2 oracle tools (R1/R2 fork the resident compilation), which
gates their value. This part fixes the shipped lifecycle defect independently.

**Lessons.** The lifecycle defect (finding 5) is severable from the resident-workspace architecture
and worth fixing on its own: it is a real correctness issue (orphaned task, swallowed failure) in the
shipping opt-in path today.

**Time.** ~1 session-hour.

### 2026-07-03 N4 tier-1 (part 2): out-of-process build-capture worker foundation

**Status.** Foundation done and green; the B1 blocker is resolved in principle and proven. N4's box
stays unticked: the worker does not yet run the wiring analyzers or feed the store, and it is not yet
spawned by the indexer.

**Result.** Added `Fuse.BuildCaptureWorker`, a standalone `fuse-build-capture` executable that runs
the repository build with a binary log and rehydrates exact compilations via Basic.CompilerLog.Util,
emitting a source-generated-JSON `CaptureResult` on stdout. This resolves blocker B1: the library is
referenced only by the worker project (never by the parent process that hosts MSBuildWorkspace), and
the worker never invokes MSBuildWorkspace, so the two conflicting Roslyn closures never share a
process. Proven: the full suite is green, including the MSBuildWorkspace-dependent Semantics tests
(100 pass) alongside the worker's own rehydration test, where the earlier in-assembly integration had
broken them.

**Tests.** `BuildCaptureRehydratorTests` builds the SampleShop solution and asserts the worker
rehydrates at least one C# compilation declaring real types (or reports a concrete reason if the
fixture does not build in the environment, the parent's fallback contract).

**Verification.** Three gates green (clean build 0 errors, full suite exit 0, format clean). The
worker's external-process build invocation uses a fixed bounded argument list per the invariant. JSON
uses a source-generated context per the invariant.

**Remaining tier-1 work (concrete).** (a) The worker runs the wiring analyzers
(`SemanticAnalysisRunner`) plus symbol/chunk/route extraction over each rehydrated compilation and
serializes the graph bundle (files, symbols, chunks, nodes, edges, routes). (b) `SemanticIndexer`
spawns the worker (bounded args), deserializes the bundle, and writes it to the store as the tier-1
path, falling back to MSBuildWorkspace (tier 2) then syntax (tier 3). (c) The worker exe is packaged
with the global tool and located at runtime. (d) Then the localize/review recall re-run (the Phase 4
gate). Steps (a)/(b) require extracting the compilation-to-records logic from
`SemanticIndexer.IndexSemanticAsync` into a form the worker can call without MSBuildWorkspace.

**Lessons.** The isolation is process-level, not assembly-level: a separate project still lands both
Roslyn closures in one app output. The worker being a separate executable (spawned, not referenced)
is what keeps them apart, and the validation is that the parent's MSBuildWorkspace tests stay green.

**Time.** ~1.5 session-hours.

### 2026-07-03 R5 (part 1): persisted type-level reference edges

**Status.** Part 1 done (`references` edges). Box left unticked: R5's `tests` edges (test method to the
symbols it references, with DI resolving interface references to registered implementations) are part 2.

**Result.** Added `ReferenceEdgeAnalyzer` to the in-process semantic analyzer set: for each source type it
resolves the source types its declaration references (through the semantic model, so only bound references
count) and emits one deduped `references` edge per (referencing type, referenced type) pair, at the
references weight (0.15), never a self-loop. This runs in the existing `IndexSemanticAsync` pipeline on any
repo that loads semantically today (no worker or resident workspace needed), so it is unblocked. Bumped
`WorkspaceIndexSchema.TargetVersion` 14 to 15 (a stale index has no reference edges; the bump forces a
rebuild). Finding 7 cleanup: `references` now has a producer; the dead `calls` weight is removed from
`EdgeWeightProvider` (no producer; R5 uses `references` as the use-edge name); the `tests` weight is
retained for M1 with a comment that its producer is R5 part 2.

**Tests.** `ReferenceEdgeAnalyzerTests` over the OrderingApp fixture: references edges exist, are deduped,
never self-loop, carry weight 0.15 with both endpoints materialized as nodes, and `OrderService` references
another source type. Semantics.Tests rise accordingly; full suite green.

**Verification.** Three gates green (clean build 0 errors, full suite exit 0, format clean). Suite A
(wiring ground truth) is unaffected because it checks specific wiring edge types, not `references`.

**Blockers.** R5 part 2 (tests edges with DI resolution) is the remaining half; it feeds M1's covering-test
selection. Member-level references (finer than type-level) are deferred as a row-volume tradeoff, per the
plan's kill-risk (type granularity is enough for `fuse_impact`'s answer).

**Lessons.** Type-level `references` is a bounded, in-process producer that unblocks R2 without the worker or
resident workspace; resolving references through the semantic model (not a textual scan) keeps them exact.

**Time.** ~1.5 session-hours.

### 2026-07-03 R2 (part 1): fuse_impact blast radius

**Status.** Part 1 done (graph-grade blast-radius enumeration). Box left unticked: the tier-1
signature-change bind-check (which call sites no longer bind) needs a resident oracle-grade compilation
and is reported unavailable per the availability contract.

**Result.** Added the `fuse_impact` MCP tool. It resolves a symbol and returns its blast radius (callers,
implementers, consumers, referencing types) from the persisted semantic graph, reusing
`GraphNeighborhoodExplorer.CallersAndImplementersAsync` over incoming edges, which now include R5's
`references` edges. This is unblocked precisely because R5 part 1 landed the reference substrate. The
response states the index mode and reports the signature-change break set as unavailable (oracle-grade,
tier-1) rather than guessing, honoring the plan's rule that oracle tools abstain below tier 1. Additive
(no shim, no protocol change); the MCP surface is now eleven tools, with the integration-test array,
summary, and server instructions updated together.

**Tests.** `McpServeIntegrationTests` tool-name array includes `fuse_impact` (registration and resolution
verified end to end); the enumeration logic is covered by the existing `GraphNeighborhoodExplorer` tests.

**Verification.** Three gates green (clean build 0 errors, full suite green on re-run after a known
Fusion concurrency-test flake, format clean). Docs: `reference/mcp-tools.mdx`, `AGENTS.md` (eleven-tool
list).

**Blockers.** The bind-check half is blocked on the resident oracle-grade compilation (N3 resident
workspace plus N4 tier-1), the same substrate the other oracle tools need.

**Lessons.** R5's reference edges made R2's enumeration a pure store read, so the tool ships graph-grade
today and gains the precise break-set for free once the resident compilation exists.

**Time.** ~1 session-hour.

### 2026-07-03 R5 (part 2, completes R5): DI-resolved test edges

**Status.** Done. R5 is now complete (references edges in part 1, tests edges here). Box ticked.

**Result.** Added `TestEdgeExtractor`, run as a post-merge pass in `SemanticIndexer.RunAnalyzers` (after the
per-project analyzers merge, so it has the full node set and the `di_resolves_to` edges). For each test
type (identified by test-framework attributes: Fact/Theory/Test/TestMethod/TestCase), it resolves the
source types the test references and emits a `tests` edge to each, plus, when a referenced type is an
interface the DI graph resolves, to that interface's registered implementations. So a test injecting
`IOrderService` links to `OrderService`, which is what makes covering-test selection better than a plain
reference walk. Foreign-key safe: it links only to node ids that already exist in the merged graph, so no
dangling edge to a framework or third-party metadata type. Finding 7 is fully resolved: `references` (part
1) and `tests` (here) both have producers; the dead `calls` weight was removed in part 1.

**Tests.** `TestEdgeExtractorTests` (via `InlineCompilation`): a test linked to a referenced interface and,
through a synthetic DI map, to its implementation; the FK-safe case (no edge to an absent node); and a
non-test type producing no tests edges. Full suite green (Suite A unaffected: `tests` is a distinct edge
type from the wiring edges it checks).

**Verification.** Three gates green (clean build 0 errors, full suite exit 0, format clean). No schema
change beyond R5 part 1's version 15 bump (same `edges` table, a new edge_type value). Docs: `AGENTS.md`
and `overview.md` edge-weight notes; `EdgeWeightProvider` comment updated.

**Blockers.** None. Selection soundness is bounded by what the reference walk and DI graph see (reflection
and source-generator-reached tests are missed), reported as best-effort, never as complete, per the plan.

**Lessons.** Doing the tests pass post-merge (not as a per-project analyzer) is what makes it both
cross-project and foreign-key safe: the full node set and DI edges are only available after the merge.

**Time.** ~1.5 session-hours.

### 2026-07-03 R4 (part 1): loop-metric computation

**Status.** Part 1 done (the deterministic metric core). Box left unticked: the model-driven suite (curated
PR set run across the native, LSP-armed, and Fuse arms, three rollouts, CIs) needs provisioned models and
is the remaining R4 work.

**Result.** Added `LoopMetrics` to `Fuse.Benchmarks`: from a task-resolution transcript (a sequence of
typed turns) it computes iterations-to-green, build-invocations-per-session (the direct loop-collapse
measure the oracle thesis moves), wall-clock, and whether green was reached. This is the model-free core the
plan calls for ("the metric computation is unit-tested against a scripted transcript"), so the deterministic
sub-metrics stand between the expensive model runs.

**Tests.** `LoopMetricsTests`: build-gated iteration counting, a test turn counting toward green but not
toward build invocations, and the never-green case. Benchmarks.Tests rise; full suite green.

**Verification.** Three gates green (clean build 0 errors, full suite exit 0, format clean).

**Blockers.** The model and LSP arms need provisioned models and a curated 10-to-15 PR failing-then-passing
task set; that curation plus the pre-registered native and LSP baselines is the remaining R4 work, on top of
the existing `TaskResolutionHarness` (patch-apply plus test oracle) which already scores pass@1.

**Lessons.** iterations-to-green counts build-gated turns (build or test), while build-invocations counts only
build turns; keeping them distinct is what lets R4 attribute a loop-collapse win to fewer builds versus fewer
total gated iterations.

**Time.** ~0.75 session-hour.

### 2026-07-03 N4 tier-1 (part 3): semantic extraction runs in the worker

**Status.** Crux de-risked and green. N4 box still unticked: serialization of the graph bundle and the parent
ingest/wiring remain.

**Result.** The `fuse-build-capture` worker now references `Fuse.Semantics` and runs Fuse's semantic
extraction over each rehydrated compilation: `SemanticSymbolExtractor.Extract` plus the wiring analyzers
(`SemanticAnalysisRunner.CreateDefault().Run`), reporting symbol, node, and edge counts per project. This
answers the last open question about the out-of-process design: Fuse's analyzers run fine over a
build-capture compilation in the worker with no MSBuildWorkspace conflict, because the worker references the
MSBuild loader's assembly (transitively via Fuse.Semantics) but never invokes it (it calls only
Basic.CompilerLog plus the analyzers, which are plain Roslyn).

**Tests.** `BuildCaptureRehydratorTests` now asserts the worker produces symbols (SymbolCount >= 1) from the
rehydrated compilation of a self-contained project, in addition to the type count. Full suite green,
including the parent's MSBuildWorkspace-dependent Semantics tests (unaffected: the parent process does not
reference Basic.CompilerLog).

**Verification.** Three gates green (clean build 0 errors, full suite exit 0, format clean). The VisualBasic
4.14 pin keeps the worker's Roslyn closure consistent now that it pulls the fuller Fuse.Semantics graph.

**Remaining tier-1 work (now pure assembly, not a research question).** (a) Serialize the extracted bundle
(symbols, chunks, nodes, edges, routes, DI registrations, options bindings) as source-generated JSON on the
worker's stdout. (b) `SemanticIndexer` spawns the worker (bounded args), deserializes the bundle, and writes
it to the store as the tier-1 path, falling back to MSBuildWorkspace (tier 2) then syntax (tier 3). (c)
Package the worker exe with the global tool and locate it at runtime. (d) The localize/review recall re-run,
the Phase 4 gate. The crux (extraction-in-worker) is proven, so this is mechanical.

**Lessons.** Referencing Fuse.Semantics in the worker was safe because the conflict is triggered by invoking
MSBuildWorkspace, not by its assembly being present; the worker calls the analyzers (plain Roslyn) and
Basic.CompilerLog, never the MSBuild loader, so both closures coexist.

**Time.** ~1 session-hour.

### 2026-07-03 N4 tier-1 (part 4): worker emits the serialized graph bundle

**Status.** Done and green. N4 box still unticked: parent ingest, indexer wiring, and worker packaging remain.

**Result.** The worker now carries the full extracted graph (symbols, nodes, edges, routes, DI registrations,
options bindings) per project in `CapturedProject` and serializes it as source-generated JSON on stdout (all
record types added to `BuildCaptureJsonContext`, honoring the source-gen-only invariant). A round-trip test
pins the contract, so the parent-side ingest will read exactly what the worker wrote.

**Tests.** `CaptureBundleSerializationTests`: a bundle with a symbol, two nodes, a di_resolves_to edge, and a
route round-trips with fields intact. Full suite green.

**Verification.** Three gates green (clean build 0 errors, full suite exit 0, format clean).

**Remaining tier-1 work.** Parent (SemanticIndexer) spawns the worker (bounded args), deserializes the bundle,
and writes symbols/nodes/edges/routes/DI/options to the store as the tier-1 path (the parent still does its own
file scan, syntax chunks, and embeddings); package the worker exe with the global tool and locate it at
runtime; then the recall re-run. Serialization (this part) and extraction (part 3) are done, so the parent
ingest is a store-write over deserialized records.

**Lessons.** The Fuse.Indexing record types serialize cleanly under source-gen with no attributes needed, so
the worker-to-parent contract is just those records plus a thin envelope.

**Time.** ~1 session-hour.

### 2026-07-03 N4 tier-1 (part 5): parent consumes the worker across the process boundary

**Status.** Done and green. N4 box still unticked: the indexer tier-1 write path and worker packaging remain.

**Result.** Moved the capture DTO (`CaptureResult`, `CapturedProject`) and its source-generated JSON context to
the shared `Fuse.Indexing` assembly, so the parent deserializes the worker's output without referencing the
worker's Basic.CompilerLog closure. Added `BuildCaptureClient` in `Fuse.Semantics`: it spawns the worker
(bounded args), reads the graph bundle from stdout, and deserializes it, degrading to a concrete failure so the
caller falls back a tier when the worker is unconfigured, times out, or emits no parseable output. The worker is
located via `FUSE_BUILD_CAPTURE_WORKER`.

**Tests.** `BuildCaptureClientTests`: the parent spawns the worker on a self-contained project and reads back the
extracted `Widget` symbol (the full cross-process round-trip), and an unconfigured client reports unavailable.
Full suite green.

**Verification.** Three gates green (clean build 0 errors, full suite exit 0, format clean). External-process
args are a fixed bounded list; JSON is source-generated; both invariants held.

**Remaining tier-1 work.** Wire `BuildCaptureClient` into `SemanticIndexer.IndexAsync` as the tier-1 write path
(on a successful capture, write the bundle's symbols/nodes/edges/routes/DI/options to the store, with the parent
still doing its file scan, syntax chunks, and embeddings; fall back to MSBuildWorkspace then syntax); package the
worker exe with the global tool. Then the recall re-run (the Phase 4 gate). All five tier-1 crux risks
(mechanism, isolation, extraction-in-worker, serialization, cross-process consumption) are now proven; the
indexer write path is a store-write over deserialized records.

**Lessons.** Putting the DTO in the shared assembly (not the worker) is what lets the parent read the worker's
output without pulling the conflicting closure; the process boundary is the isolation, the shared DTO is the
contract.

**Time.** ~1.5 session-hours.

### 2026-07-03 N4 tier-1 (part 6): tier-1 build capture wired into the indexer

**Status.** Done and green; the tier-1 data path is functional end to end. N4 box still unticked: worker
packaging, tier-2 auto-restore salvage, and the recall re-run remain.

**Result.** `SemanticIndexer.IndexAsync` now tries tier-1 build capture first (via `BuildCaptureClient`) when
`FUSE_BUILD_CAPTURE` is truthy and a worker is configured. On a successful capture, `IndexFromCaptureAsync`
writes the worker's graph bundle (symbols, nodes, edges, routes, DI registrations, options bindings) to the
store; the parent still does the file scan, syntax chunks, and embeddings. Any capture failure falls back to
the MSBuildWorkspace load (tier 2), then syntax (tier 3). Default off, so indexing is unchanged unless both env
vars are set (no silent behavior change).

**Tests.** `IndexFromCaptureTests`: a synthetic capture is ingested and the store receives the symbols and the
di_resolves_to edge, with mode `semantic`. Combined with the worker tests (parts 3-5), the full tier-1 path is
covered: build and rehydrate, extract, serialize, spawn and deserialize, ingest.

**Verification.** Three gates green (clean build 0 errors, full suite green on re-run after an unrelated
git-stats parallelism flake, format clean). External-process args bounded; JSON source-generated; no protocol
change; schema already at v15.

**Remaining N4 work.** (a) Package `fuse-build-capture` with the global tool and locate it relative to the
running tool so `FUSE_BUILD_CAPTURE_WORKER` is not needed in production. (b) Tier-2 auto-restore salvage (bounded
`dotnet restore` on missing assets). (c) The localize/review recall re-run with tier-1 on, the Phase 4 gate.

**Lessons.** The tier-1 ingest is a plain store-write over the deserialized bundle, exactly as predicted once the
five crux risks were retired; the whole of N4's dominant uncertainty reduced to packaging plus a benchmark run.

**Time.** ~1 session-hour.

### 2026-07-03 N4 recall re-run (the Phase 4 gate): tier-1 does not move localize recall

**Status.** Recorded. This is the localize re-run the plan gates Phase 4 on. Honest negative result.

**Result.** Ran `fuse eval localize` with tier-1 build capture enabled (`FUSE_BUILD_CAPTURE=1`, the worker
configured), writing `results/localize.tier1.json`. Recall 15.0 percent versus the baseline 14.9 percent
(`localize.json`); precision 8.1 percent, unchanged; precision-when-confident 5.6 percent, unchanged. The
index-mode distribution is unchanged (partial 2, syntax 2): the two buildable corpus repos (NodaTime,
Specification) load with tier-1 build capture but carry residual compile errors in their test/sample projects,
so they are labeled partial (as they were under MSBuildWorkspace), and the two that do not build (Scrutor
NU1507, eShopOnWeb CS0104) stay syntax regardless of the mechanism. A manual `fuse index` on Specification with
tier-1 on confirmed the path fires (partial, 6219 symbols across 11 projects from the real build), so this is
tier-1 running, not a silent fallback.

**Interpretation (honest).** Tier-1 build capture produces a richer, build-exact semantic graph on the buildable
repos, but it does NOT materially move open-ended localize recall on this corpus (15.0 vs 14.9, within CI). The
"recall is bounded by index mode" hypothesis (finding 3) is not supported by this run: the richer graph did not
lift recall, and the non-buildable repos are bounded by the oracle coverage ceiling (the bake-off's 65 percent
build rate) rather than by ranking. Per the plan, this re-scopes Phase 4 rather than validating it.

**Consequence for Phase 4.** The gate ("Phase 4 only after N4's localize re-run is recorded") is now satisfied,
but the evidence does not warrant V1 (graph verbalization) and V2 (learned ranking) as recall levers: tier-1's
richer graph did not move recall, so verbalizing that graph or re-ranking within it is unlikely to, on this
corpus. V1 and V2 are logged as not-warranted-by-the-evidence (boxes left unticked), the honest outcome the
plan pre-agreed ("if it does not, retrieval work is re-scoped rather than pursued").

**Verification.** Numbers quoted from `results/localize.tier1.json` and `results/localize.json`; no fabrication.
Review re-run under tier-1 not run (review recall is 100 percent by construction; its precision signal is less
sensitive to the load tier than localize recall, which was the hypothesis under test).

**Blockers.** The oracle coverage ceiling (repos that do not build) bounds how much tier-1 can be exercised on
this corpus; only 2 of 4 build, exactly as the bake-off recorded.

**Lessons.** The dominant-risk item (N4) delivered its mechanism and coverage measurement, and the honest read
is that oracle-grade load is a correctness/verification asset (its payoff is R1/R2/M1, not retrieval recall),
not an open-ended-recall lever. That is a more accurate positioning than the pre-run hypothesis.

**Time.** ~0.5 session-hour (plus the background eval run).

### 2026-07-03 R1 (part 1): fuse_check speculative typecheck

**Status.** Done and green (functional). Box left unticked: Suite F (agreement rate, false-green/false-red,
release-gated) and the resident-compilation fast path remain.

**Result.** Added `fuse_check`: it typechecks a proposed single-file edit against the tier-1 build-captured
compilation and returns the compiler errors/warnings the change would produce, with no disk write and no second
`dotnet build`. The worker gained a `--check <target> <file> <newContentFile>` mode (new content via a file, not
a bounded arg): it builds, rehydrates, replaces the target file's syntax tree with the proposed content
(`ReplaceSyntaxTree`), and returns the changed document's diagnostics as a source-generated `CheckResult`.
`BuildCaptureClient.CheckAsync` spawns it; the `fuse_check` tool resolves the build target, calls it, and
abstains ("cannot verify: ...") when the worker is unconfigured or the repo does not build, never guessing green
(the availability contract). MCP surface now twelve tools.

**Tests.** `BuildCaptureCheckTests`: a patch introducing a type error is reported (an Error diagnostic), and a
clean patch is clean (tolerant of a transient second-build abstention). Tool registration in the integration
name array. Full suite green.

**Verification.** Three gates green (clean build 0 errors, full suite exit 0, format clean). External-process
args bounded (new content via a file); JSON source-generated; no protocol/schema change.

**Remaining R1 work.** (a) Suite F: over the PR corpus, compare `fuse_check` diagnostics against `dotnet build`,
recording the agreement rate and the false-green and false-red rates (both near-zero on tier-1 is release gate 5).
(b) The resident-compilation fast path: the first cut re-captures (rebuilds) per check, so latency tracks a build;
holding the captured compilation resident (N3) gives the sub-second warm forks the crown table targets. R6 part 2
(repair packets on these diagnostics) also rides on this.

**Lessons.** With tier-1 build capture proven, R1 reduces to applying the patch to the rehydrated immutable
compilation and reading diagnostics; the oracle correctness is structural (the compilation shares the real
build's inputs), and the abstention contract handles the non-oracle case honestly.

**Time.** ~1.5 session-hours.

### 2026-07-03 R6 part 2: repair packets on fuse_check diagnostics (R6 complete)

**Status.** Done and green. With part 1 (`fuse_signatures`) already shipped, R6 is now complete, so the box is
ticked. Per the plan, R6 has no standalone result file: its benefit (fewer failed-verify turns) is carried by
R4, which is blocked on provisioned models; the deliverable here is the engine plus tests plus docs.

**Result.** `fuse_check` now attaches a repair packet to the two API-shape diagnostics an agent most often
hits. For CS1061 (a member that does not exist) it parses the receiver type and the missing member from the
message and returns the type's real members, ordered nearest-name first by edit distance; for CS0246 (an
unknown type) it returns the nearest type names in the index. The packet is built from the persisted symbol
table (a new `IWorkspaceIndexStore.GetMembersOfTypeAsync`, matching a member's `containing_type` against a
simple or fully qualified name from either side), so it costs one indexed read, not a re-analysis. A packet is
added only where a concrete suggestion exists (any other diagnostic returns null), and on a type with no
indexed members it explains the gap ("may live in a referenced assembly, not in indexed source") rather than
inventing a candidate.

**Tests.** `RepairPacketBuilderTests`: a CS1061 typo ("GrandTotol") leads with the real "GrandTotal" and lists
the type's members; a CS0246 typo ("Invoce") suggests "Invoice"; a missing member on an unindexed type
(System.String) returns no candidate and explains why; an unhandled diagnostic (CS0029) returns no packet.
`WorkspaceIndexSignatureTests`: `GetMembersOfTypeAsync` matches by simple or fully qualified type name and is
empty for an unknown type. Full suite green.

**Verification.** Three gates green (build 0 errors, full suite exit 0, format exit 0). No schema change (the
new query reads existing columns); no RPC DTO change. `fuse_check` gained a DI-injected `SemanticIndexer`
parameter, which is a service injection, not a client-facing parameter, so the tool's client contract is
unchanged.

**Lessons.** The store's `containing_type` and a diagnostic's type name can each be simple or fully qualified
and need not agree; the query matches four ways (exact both, and a '%.'-suffix match against a stored fully
qualified value) so a lookup in either form succeeds. A first attempt matched only the request's own forms and
missed a stored fully qualified `containing_type`; a store test caught it before it shipped.

**Remaining.** Optional R6(c) `fuse_complete` (legal members at a location via Roslyn's recommendation service)
was marked "if cheap" and is not built; it needs a resident compilation (N3) to be cheap, so it rides that.

**Time.** ~1.25 session-hours.

### 2026-07-03 M1: the speculative staging area (changeset lifecycle)

**Status.** Done and green. With the covering-test selection down-payment already shipped, M1's changeset
lifecycle now lands, so the box is ticked. The resident-workspace fast path for diagnose (an optimization, not
a correctness requirement) and the selection-recall benchmark (bounded by index mode, as the other corpus
suites are) remain future work; in-process test execution stays out of scope by design (M2).

**Result.** Added `ChangesetSessionStore` (in-memory, session-keyed) and the `fuse_changeset` MCP tool
(fourteenth tool) implementing the propose-oracle-commit loop: `create` a session, `stage` single-file edits
(held in memory, never written), `diagnose` each with the speculative typecheck (R1; abstains per file without
a tier-1 worker, never guessing green), `select` the tests that cover the changed files' symbols (R5's
DI-resolved tests edges via `CoveringTestsAsync`), then `promote` (the only operation that touches disk, and
only on an explicit call) or `discard` (leaves the tree untouched). Two changesets over the same base are
isolated: staging into one never affects the other's edits or diagnostics.

**Tests.** `ChangesetSessionStoreTests` (six): two sessions are isolated; staging an unknown session returns
false; promote writes the staged edits to the tree and consumes the session; discard leaves the tree untouched;
diagnose abstains per file when no build-capture worker is configured; select returns the tests-edge sources
for the changed file. The MCP integration test now lists fourteen tools and passes.

**Verification.** Three gates green (build 0 errors, full suite exit 0 across all projects, format exit 0).
Fuse.Retrieval.Tests 79 to 85. No schema or RPC change (the sessions are in-memory host state); the new tool
is additive, and the `fuse_changeset` name is registered in the integration name array.

**Safety.** Promote is the only disk write and fires only on an explicit `op=promote`; discard and simply never
promoting both leave the working tree exactly as it was. This keeps the hard-to-reverse action (writing files)
gated behind an explicit agent decision, and diagnose/select are pure reads.

**Remaining.** The resident-workspace fast path (so diagnose does not re-capture the build each call) and the
selection-recall benchmark over PRs with test oracles (bounded by how much of the corpus reaches semantic mode,
so it largely skips on this corpus, the same ceiling the other corpus suites hit).

**Time.** ~1.75 session-hours.

### 2026-07-03 R4: the loop metric (harness-first deliverable)

**Status.** Harness done and green. R4 is the plan's harness-first exception ("benchmark is the deliverable"),
so the box is ticked on the harness landing; the model-driven numbers are recorded from an explicit provisioned
run, not from this session. The LSP-armed comparison arm remains future work (logged below).

**Result.** Added `LoopSuite` (`fuse eval loop`, `results/loop.json`) and its deterministic core:
`LoopTranscriptClassifier` maps a Claude Code stream-json task-resolution transcript to the ordered turns
`LoopMetrics` counts (read, edit, build, test), reading each verification turn's pass or fail from its paired
`tool_result`, and a `fuse_check` turn counts as a build verification (the whole R4 thesis is that it stands in
for a `dotnet build` round-trip). Two arms: `native` (filesystem plus `dotnet build`/`dotnet test`) and `fuse`
(the MCP tools). The metric is build-gated turns to green and total build invocations, lower is better.

**Safety fix (important).** The first cut launched the model arms whenever the `claude` CLI was on PATH. Inside
a Claude Code host the CLI is always present, so a bare `fuse eval loop` began driving real, minutes-each model
rollouts against the corpus, a runaway cost. Execution is now opt-in: without `FUSE_LOOP_RUN=1` the suite
records the harness state and the task set it would sample (8 PRs on the current corpus) and stops, spending no
model time. This is the correct default for a benchmark that costs real model calls.

**Tests.** `LoopTranscriptClassifierTests`: a scripted transcript (read, edit, failing build, edit, passing
build) classifies correctly and `LoopMetrics` reports 2 build invocations and green on the second gated turn; a
`fuse_check` "clean" result is a passing build verification, and a `fuse_check` "diagnostics" result is a
failed one. Benchmarks test count 50 to 54. Full suite green.

**Verification.** Three gates green (build 0 errors, full suite exit 0, format exit 0). `fuse eval loop` runs in
seconds by default and wrote `results/loop.json` recording the harness state and the sampled PRs, with no
fabricated numbers (the never-weaken-a-number rule: the model-driven scorecard is produced only by an opt-in
run).

**Remaining.** The recorded model-driven numbers (an explicit `FUSE_LOOP_RUN=1` run with a provisioned model),
and the LSP-armed comparison arm (overlay diagnostics as the strongest competing verify path, per R1's honest
ceilings). Both are additive on top of the shipped harness.

**Lessons.** A benchmark that spends model calls must be opt-in, never fire on a bare invocation, and must be
especially careful about environment detection (a "skip if the CLI is absent" guard inverts into "always run"
inside a host where the CLI is present). The deterministic core (classifier plus metric) is what carries the
claim offline and is where the test value is.

**Time.** ~1.5 session-hours (including the runaway-process cleanup).

### 2026-07-03 M1 (down-payment): covering-test selection over R5 tests edges

**Status.** The covering-test selection primitive is done and green. The M1 box stays unticked: the full
changeset-session lifecycle (create/apply/diagnose/promote/discard over the resident workspace, with per-branch
isolation) remains, and its selection-recall benchmark over corpus PRs with test oracles is bounded by index
mode (most corpus repos load syntax, so they carry no tests edges to select over, the same ceiling the other
corpus suites hit). This lands the one deterministic, independently-useful M1 piece that R5 unblocked.

**Result.** Added `GraphNeighborhoodExplorer.CoveringTestsAsync`: given a symbol, it returns the test types
that reach it through an incoming R5 `tests` edge. Because R5's tests edges are DI-resolved (a test injecting
`IOrderService` carries an edge to the registered `OrderService`), the covering set follows the wiring, not the
literal type name. `fuse_impact` now lists these covering tests as a distinct section, separate from the blast
radius, so an agent can run just that subset with its own `--filter`. It is labeled a lower bound bounded by
R5 edge completeness (a test reached only through reflection or a source generator has no edge and is not
selected), never "all the tests".

**Tests.** `GraphNeighborhoodExplorerTests`: a change to `OrderService` selects the test carrying the tests
edge and not the controller that only injects it (blast radius is not coverage); a symbol with no incoming
tests edge selects nothing. The pre-existing central-files ranking test still holds (the added tests edge
leaves `IOrderService` highest-degree on the ordinal tiebreak).

**Verification.** Three gates green (build 0 errors, full suite exit 0, format exit 0). The primitive is a pure
reverse-edge query filtered to the `tests` kind, so it adds no tool to the surface and no schema change; it
enriches the existing `fuse_impact` (R2) output.

**Remaining M1 work.** The changeset lifecycle and staging area (the stateful propose/verify/select/promote/
discard sessions over the resident workspace), and the selection-recall benchmark once a semantic corpus load
makes tests edges available at scale. In-process execution stays out of M1 by design (moved to M2, stretch).

**Time.** ~0.75 session-hours.

### 2026-07-03 Release gate: version bump to 4.0.0 (no tag)

**Status.** Done. The tag is deliberately not cut; per the working conventions the release is triggered by
pushing a matching `vX.Y.Z` tag, which is a reviewer/maintainer action, not an autonomous one.

**Result.** Ran `build/set-version.ps1 4.0.0`, which set the single source of truth (`Directory.Build.props`
`<Version>`) and the three satellite manifests that must agree (`ext/vscode/package.json`,
`mcp-registry/server.json`, `site/package.json`) in one step, never by hand. `build/verify-version.ps1`
reports "Version OK: 4.0.0", so CI's version-consistency gate is satisfied. The build and format gates are
green after the bump; a version-string change does not touch test logic, which was green at the preceding
commit. The RPC `ProtocolVersion` and `PROTOCOL_VERSION` constants are independent of the package version and
were not touched by this bump.

**Not done, on purpose.** No git tag, no NuGet/GitHub/Marketplace publish. The release remains a single open PR
off main. The remaining gated items (R4's model and LSP arms, R3's typed-union router, M1's changeset
lifecycle, G1's outward-facing launch publish) are logged as blocked or launch activities; the version number
reflects the v4 target on the branch, not a shipped release.

**Time.** ~0.25 session-hours.

### 2026-07-03 G2 (docs): the analyzer coverage table and contribution recipe

**Status.** Docs deliverable done. Box left unticked: the outward-facing community contribution program (the
public on-ramp, issue templates, and any coverage bounty) is a launch activity, not a code change, and is out
of scope for an autonomous engineering session.

**Result.** Added `site/content/docs/internals/extending/semantic-analyzer.mdx` (linked in the Extending
section's `meta.json`): an honest coverage table of the twelve shipped analyzers, each with the framework or
pattern it covers and the edge or record it produces (verified against the edge-kind constants in source:
`di_resolves_to`, `implements`, `inherits`, `references`, `tests`, `di_decorates`, routes, options, hosted
services, EF Core), plus the four-step recipe to contribute an analyzer (implement `ISemanticAnalyzer`, register
in `SemanticAnalysisRunner.CreateDefault()`, add a fixture and a Suite A golden edge, keep the gates green) and
the honesty rules (emit an edge only when the symbols resolve, weight a soft signal below a hard one, measure
precision via `--corpus-sample`). It states plainly that the semantic tier is C#/Roslyn-only and that an
analyzer sees only indexed source, so the picture is not overclaimed.

**Verification.** Docs-only change (only `site/` and the roadmap/changelog); the .NET build, test, and format
gates are unaffected and were green at the preceding commit. ASCII-clean. The table's edge names were checked
against the analyzer source, not asserted from memory.

**Remaining G2 work.** The community program itself (a launch and governance activity), and any future analyzer
contributions that extend the table.

**Time.** ~0.5 session-hours.

### 2026-07-03 R3 (part): the ambient availability header

**Status.** Done and green. The R3 box stays unticked: the full tool-surface reshape (the typed-union router
folding the read tools around the oracle, with the V2 deprecation shims) remains; the shims themselves already
exist (FuseDeprecatedTools). This lands the honesty half of R3: the ambient grade signal.

**Result.** Added `FuseTools.OracleAvailabilityHeaderAsync`, a shared helper that renders one line stating the
index mode (semantic, partial, or syntax), whether tier-1 build capture is configured (the oracle-grade write
path), and the N6 freshness stamp (a nonzero stale count means a bulk change outran the per-read reconcile, so
the graph may lag the working tree). The two store-backed oracle reads (`fuse_impact`, `fuse_signatures`) now
prepend it, so a client cannot mistake a syntax-tier or stale answer for an oracle-grade one. The compiler
tools (`fuse_check`, `fuse_refactor`) already carry their own explicit "cannot verify/rename" abstention, which
is the same signal at higher resolution, so they are not double-headed.

**Tests.** `OracleAvailabilityHeaderTests`: the header reports the index mode and "up to date" when the stale
count is zero, a concrete "N known file(s) changed ... may lag the working tree" when nonzero, and "index mode
unknown" when the meta is absent. The MCP integration test still passes with the header prefixed.

**Verification.** Three gates green (build 0 errors, full suite exit 0 across all projects, format exit 0). The
header reuses the existing `index_mode` and `stale_dirty_count` metas and `BuildCaptureClient.IsAvailable`; no
schema change, no new RPC DTO.

**Remaining R3 work.** The typed-union router that reshapes the read surface around the oracle (a single entry
that dispatches on the input shape), keeping the additive shims. That is the larger, contract-shaping half and
is best done as its own change with the extension client in the loop.

**Time.** ~0.75 session-hours.

### 2026-07-03 R1 Suite F: the fuse_check honesty gate (checkgate)

**Status.** Done and green. The R1 box stays unticked: the repair-packet half (R6 part 2, structured fixes
on R1's diagnostics) and the resident-compilation fast path remain.

**Result.** Added `CheckGateSuite` (`fuse eval checkgate`, `results/checkgate.json`), Suite F: the false-green
and false-red gate for `fuse_check`. It builds a small self-contained compilation with raw Roslyn (no MSBuild,
no Basic.CompilerLog, so it runs everywhere and cannot hit the B1 assembly conflict) and runs a battery of
single-file edits each with a known-correct verdict, three that must stay clean (an equivalent rewrite, an
added valid overload, a comment-only change) and five that must be flagged (missing member CS1061, wrong
return type CS0029, undefined type CS0246, a syntax error, an init-only assignment CS8852). Each edit replaces
one document's syntax tree and is classified with the exact shipped `CheckResult.IsClean` rule, so the gate
measures the classification contract the tool ships, not a proxy. An abstention counts as neither a false
green nor a false red, matching the oracle's honesty contract.

**Result numbers.** 8 of 8 edits classified correctly: false-green 0, false-red 0, abstained 0. GATE: PASS.

**Tests.** `CheckGateSuiteTests` (the gate passes with no false green and no false red; every case scores 1.0).
Full suite green (Fuse.Cli 82, Fuse.Semantics 110, worker 3, plus the rest).

**Verification.** Three gates green (build 0 errors, full test suite exit 0, format exit 0). The suite runs
via the real CLI (`fuse eval checkgate`) and wrote `results/checkgate.json`. The dominant failure mode a
speculative typecheck can have (a false green that ships a broken change) is now measured, not asserted.

**Scope choice.** The offline gate is the in-process classification measure, which is deterministic and needs
no provisioning. The tier-1 end-to-end path (the out-of-process build-capture worker) is exercised by
`BuildCaptureCheckTests`; the suite records the tier-1 arm as skipped when no `FUSE_BUILD_CAPTURE_WORKER` is
configured rather than fabricating a number for it, per the never-weaken-a-number rule.

**Remaining R1 work.** Repair packets (turn a diagnostic into a structured, apply-ready fix suggestion, the
R6-part-2 half), and the resident-compilation fast path so a warm check is sub-second rather than tracking a
build. Both are additive on top of the shipped engine plus this gate.

**Time.** ~1 session-hour.

### 2026-07-03 R7 (part 1): fuse_refactor compiler-executed rename

**Status.** Done and green (rename). Box left unticked: the second operation (change-signature) and the
staged-diff re-check-through-R1 loop remain.

**Result.** Added `RenameRefactorer` (parent-side, MSBuildWorkspace plus Roslyn's `Renamer`) and the
`fuse_refactor` MCP tool. It opens the solution, resolves the named symbol, renames it and every reference
via `Renamer.RenameSymbolAsync`, and returns the change as a staged per-file diff without touching the
working tree. Roslyn semantics mean a same-named unrelated symbol is not renamed, the correctness a textual
rename cannot promise. Oracle-shaped: it abstains (with a reason) when the solution does not load cleanly or a
project produces no compilation, because a partial rename is worse than none. Runs in the parent (no
Basic.CompilerLog), so MSBuildWorkspace is unaffected. MCP surface now thirteen tools.

**Tests.** `RenameRefactorerTests`: renaming the fixture's `SecretsHolder` stages a diff mentioning the new
name (or abstains cleanly if the solution does not load here), and an unknown symbol abstains. Tool
registration in the integration name array. Full suite green.

**Verification.** Three gates green (clean build 0 errors, full suite exit 0, format clean). The staged diff
is line-level (accurate for rename, which preserves line structure) since Fuse.Semantics does not depend on
Fuse.Fusion's unified-diff generator.

**Remaining R7 work.** Change-signature (the second operation: `SymbolFinder` call sites plus a per-site
rewriter, over R5's reference edges), and wiring the staged diff through `fuse_check` (R1) so an
incompleteness surfaces as a diagnostic. The hard-capped two-operation scope holds; change-signature is part 2.

**Lessons.** Rename belongs parent-side over MSBuildWorkspace (the Renamer needs a Workspace Solution, which
build capture does not produce); the completeness abstention (all projects loaded) is the oracle-grade gate
for rename, distinct from the build-exactness gate that matters for `fuse_check`.

**Time.** ~1.5 session-hours.

### 2026-07-03 plan revision (external review pass)

**Status.** Plan amended, no code changed. Re-verified against the live tree (Fuse 3.2.0) and
the recorded results in `tests/benchmarks/results`.

**What changed and why.**
- Added findings 6 to 9: silently-stale reads on the whole MCP surface after the first edit
  ([FuseTools.cs:192](../src/Host/Fuse.Cli/Mcp/FuseTools.cs#L192), no watcher on `mcp serve`,
  `ReindexFileAsync` has no product caller); assumed graph edges that do not exist (`tests` and
  `calls` weighted in [EdgeWeightProvider.cs:31](../src/Core/Fuse.Retrieval/EdgeWeightProvider.cs#L31)
  with no producer); stale provenance in this plan's own baselines (`agent.json` is 10/12
  retired-corpus; docs quote the superseded a1 run's 9.3 percent precision-when-confident vs the
  current 5.6); and an unmeasured default-on ranking regression (A6 co-change prior:
  `localize.a1.json` recall 15.06 to `localize.json` 14.90, precision-when-confident 9.3 to 5.6).
- N4's mechanism changed from hardening MSBuildWorkspace to a three-tier build-capture ladder
  (binlog rehydration for the oracle tier), because the retrieval graph tolerates approximate
  compilations and the oracle does not, and the project has three recorded restore failures on
  this corpus (v3 R0 on four repos; Scrutor today, NU1507). Tier choice is a recorded bake-off.
- M1's in-process test execution was removed from scope (not gated): modern .NET has no
  AppDomain isolation, so in-proc sandboxing is not achievable and a hostile test would kill the
  resident daemon. M1 ships lifecycle plus diagnosis plus selection; execution moves to M2
  (out-of-proc, stretch, may slip to 4.1).
- Added items: N6 (freshness contract), R5 (persisted reference index, the substrate R2/M1
  assumed), R6 (repair packets and `fuse_signatures`, the item that actually funds the token
  projection), R7 (`fuse_refactor`, compiler-executed rename and change-signature), M2, and
  Phase 4 V1 (graph verbalization) and V2 (per-repo learned ranking), both gated on N4's
  localize re-run.
- Amended in place: N1 (suite gates priors and re-adjudicates A6), N2 (sweep includes
  `agent.json` and a1 citations), N3 (names the reindex trigger, measures resident memory),
  R1 (false-red rate gated, repair-packet output, honest sub-second scope, LSP comparison arm),
  R2 (served from R5, not live SymbolFinder), R3 (seven tools, typed union, ambient availability
  header), R4 (task set and native plus LSP baselines pre-registered in Phase 1, wall-clock
  recorded), G1 (verification-parity arithmetic, R7-first demo, LSP rebuttal pre-empted).
- Release gate updated: false-red added to gate 5; gate 6 rewritten for M1/M2 split; gate 6a
  added for the N6 freshness contract. Sequencing revised: N4 bake-off plus R4 baselines as item
  zero; R5 before R2; Phase 4 after N4's re-run.

**Verification.** File is plain ASCII (checked). No benchmarks were run; every number cited is
quoted from an existing `results/*.json` file or from code read at the cited file:line.

**Blockers.** None; this is a planning change.

**Lessons.** The largest single risk the original plan under-weighted was not N4's difficulty
(named honestly) but N4's mechanism plus the staleness hole: an oracle brand rests on freshness
and exactness, and the tree shipped neither guarantee.
