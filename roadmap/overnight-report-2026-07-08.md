# Overnight autonomous run report - 2026-07-08

Program: Fuse v4.1 (the resident verified-edit runtime). Branch: `feature/v4-compiler-oracle`.
All work committed with DCO sign-off and pushed after each item. No PRs, merges, tags, version
bumps, or publishing. Tree is green and pushed at HEAD `041eb33` (T0 landed at `32f4450`, the S1
design checkpoint at `519a2d3`/`9d576bb`, S1 resident-engine primitives at `4bd7bd6`/`deb5594`/`041eb33`).

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
