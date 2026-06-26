# Fuse V3 Plan: the path to the crown

The first V3 wave shipped a Roslyn-backed .NET semantic context engine with a warm persistent index, an 8-tool MCP surface, a five-suite C# benchmark, and a rewritten docs site. Its plan, execution record, and full progress log are archived in [Fuse-V3-Overhaul-History.md](Fuse-V3-Overhaul-History.md) (and PR #20).

This plan is the next V3 wave: the path to the most performant .NET context engine available. Dominate the axes that are the moat (semantic wiring resolution, change review with a base), reach parity or better where Fuse is currently weak (open-ended localization), and prove it head to head and end to end. Items are ordered by leverage: result per item, not effort. The honest ceilings where "best of everything" genuinely trades off are stated at the end.

This file holds the forward plan. Each item carries its engine work plus the tests, docs, and benchmark changes that land with it. Tick a box when its definition of done is met; record the measured result in `tests/benchmarks/results` and resync `AGENTS.md` and the benchmarks page in the same change.

## Current baseline (V3, recorded 2026-06-26)

All from `tests/benchmarks/results`, counted with `o200k_base`. The corpus loads mostly in syntax/partial index mode in the current environment, so the semantic suites sit below their semantic ceiling (R0 addresses this).

- Suite A, semantic resolution: 100 percent edge recall and precision (10/10) on the wiring fixture.
- Suite B, change/review: 100 percent changed-file recall, 89.6 percent precision, median 874 tokens over 48 PRs (recall is high by construction; review seeds changed files as must-keep).
- Suite C, open-ended localization: 27.3 percent recall over 108 PRs from a title alone (the weakest mode); low-signal detection F1 0.11.
- Suite D, agent (sonnet-4-6, 6 PRs): fuse 21 percent recall at 135K median tokens, native 27 percent at 212K.
- Suite E, reduction/fidelity: skeleton keeps 100 percent of public types and 99 to 100 percent of methods; removes 37 to 55 percent of tokens.

## The crown, defined (measurable target per axis)

| Axis | Today | Crown target |
|------|-------|--------------|
| Semantic resolution (A) | 100/100 on one fixture | P@1 >= 0.95 MediatR, >= 0.85 DI and route, >= 0.80 options, across the real corpus by sampled adjudication, plus EF Core, minimal-API groups, gRPC/SignalR, open generics |
| Change/review (B) | 100/90 over changed-files truth | Recall of an adjudicated support set >= 0.85 at >= 0.60 precision at a 25K budget, beating changed-files-only and grep |
| Open-ended localize (C) | 27 percent, title only | Beat the retired 49 percent lexical floor on identifier-rich titles, competitive on natural-language titles via dense retrieval, low-signal detection F1 >= 0.80 |
| Agent (D) | fuse about equal to native, small N | Higher recall at fewer tokens and calls than every peer toolbox, on 50 to 100 tasks x 3 rollouts, in both Fuse-first and agent-driven modes |
| Reduction/fidelity (E) | about 100 percent fidelity | Hold 100 percent type and 99 to 100 percent method fidelity; report reduction per level |
| Performance | not measured on V3 | Warm resolve P95 < 100 ms, localize < 500 ms, review plan < 2 s; single-file incremental re-index < 1 s |
| Peers (layer 6) | not re-run on V3 | Top recall against CodeGraph, Serena, and coa-codesearch on the same ground truth |
| Task resolution | not built | Measurably higher patch pass@1 with Fuse than without, on a test-oracle harness |

## Execution checklist

- [x] R0 Semantic-mode corpus: restore MSBuild workspaces in the bench harness so the corpus indexes semantically
- [x] R1 Reconnect the lexical ranker (BM25F + PRF + centrality) as a candidate generator
- [x] R2 Hybrid retrieval with a warm dense reranker for natural-language queries
- [x] R3 Low-signal detection and abstention
- [x] R4 Adjudicated support-set ground truth for review (Suite B)
- [x] R5 Wider semantic analyzer set (EF Core, minimal-API groups, gRPC/SignalR, open generics, decorator/factory DI, pipeline behaviors, IHostedService)
- [x] R6 Suite A on the real wiring zoo with sampled adjudication
- [x] R7 Warm host as the runtime plus a latency suite
- [ ] R8 Peer comparison and agent suite at scale, in semantic mode, with hybrid retrieval
- [ ] R9 Full task-resolution harness (patch plus test oracle)

---

## R0. Semantic-mode corpus (the multiplier)

The corpus loads in syntax or partial mode here because the harness does not provision a restored MSBuild workspace per repository. Every semantic suite reads a thin graph as a result. Restoring the workspaces lifts Suites A-on-corpus, B, C, and D toward the semantic ceiling with no engine change, so this gates the value of R1 through R8.

- Work: in `CorpusManager`, run `dotnet restore` (and resolve the SDK) per pinned checkout before indexing; record the achieved index mode per repo; fail loudly if a repo cannot reach semantic mode rather than silently scoring the fallback.
- Tests: a `CorpusManager` test that a restored fixture repo reaches `semantic` mode; the existing offline suites must still skip gracefully when restore is unavailable.
- Docs: update the index-mode caveat in `benchmarks.mdx` and the AGENTS.md Measured Results once the corpus runs semantically; note the restore prerequisite in the benchmark reproduce section.
- Benchmark: re-run B, C, D in semantic mode and record the lift; this becomes the headline corpus result instead of the syntax-mode floor.

## R1. Reconnect the lexical ranker as a candidate generator

The tuned BM25F ranker, pseudo-relevance-feedback expansion, and centrality prior still exist in `Fuse.Fusion.Scoping`, unplugged. Fused with the semantic candidates through the existing noisy-or combiner, they recover and exceed the retired 49 percent localize floor while keeping the semantic moat. Lexical finds the file a title names; the graph finds what it wires to.

- Work: add a `LexicalCandidateGenerator` in `Fuse.Retrieval` that runs the BM25F ranker over the persistent index (or persist a BM25F field index alongside FTS5) and emits scored candidates; blend via the existing scorer; keep the `FUSE_*` levers meaningful again or replace them with typed options.
- Tests: unit tests that the lexical generator ranks an identifier-rich query above unrelated files; a blend test that lexical and semantic candidates merge by noisy-or; a regression test that a known query localizes its file.
- Docs: update `concepts/scoping.mdx` and `internals/scoping-internals.mdx` to describe lexical plus semantic candidate fusion; update `reference/options.mdx` if new flags appear.
- Benchmark: Suite C recall by bucket, expecting identifier-rich to recover past the old floor; report at a token budget comparable to the old layer-4 operating point so the comparison is apples to apples.

## R2. Hybrid retrieval with a warm dense reranker

The natural-language-domain bucket (the largest and weakest in Suite C) fails lexical matching because prose titles do not share tokens with code. The ONNX reranker already exists as a plugin (`FUSE_RERANK`); make it warm by persisting an embedding index, and rank prose queries by semantic similarity over the candidate pool.

- Work: persist a per-chunk embedding index in the store; add a `DenseCandidateGenerator` or reranker stage gated behind a model presence check; keep the lexical and graph path the default when no model is present (the no-model-download-by-default invariant holds).
- Tests: a rerank test on a prose query that lexical ranks poorly; a no-model fallback test that the pipeline stays lexical and offline; an index-persistence test that embeddings survive disposal.
- Docs: `concepts/scoping.mdx` (the optional dense stage), `start/connect-your-ai.mdx` and `reference/commands.mdx` (the model download flow), and the operator page (the embedding index size).
- Benchmark: Suite C natural-language bucket recall with and without the reranker (an ablation: FTS only, FTS plus graph, FTS plus graph plus dense).

## R3. Low-signal detection and abstention

A no-signal title ("Merge branch", "Apply suggestions from code review") names no code, so recall is bounded by the input, not the engine. The honest crown there is to detect low signal and ask for a base, route, or symbol, rather than return overconfident junk. This is the Section 18.13 north star.

- Work: a signal classifier on the localize path (reuse the `SignalBucket` logic in the engine) that, below a confidence threshold, returns a low-signal verdict and a suggested next input instead of candidates.
- Tests: unit tests that no-signal and dependency-bump titles are flagged and identifier-rich titles are not; a localize test asserting the abstention payload shape.
- Docs: `scenarios/ask-one-question.mdx` and `reference/mcp-tools.mdx` (the low-signal response), `concepts/scoping.mdx` (why abstention beats junk).
- Benchmark: Suite C low-signal detection F1 (true positive on no-signal flagged, false positive on a solvable query downgraded), targeting F1 >= 0.80, up from 0.11.

## R4. Adjudicated support-set ground truth for review

Suite B recall is 100 percent by construction because changed files are must-keep, so the current number does not test blast radius. The crown claim needs a human or strong-model adjudicated set of files a developer must read to review a PR, then recall of that set.

- Work: a curation pass that adds an optional `reading_set` (interfaces, callers, tests, config) per PR in `prs.json`; `ChangeImpactSuite` scores recall and precision against the changed set unioned with the reading set when present, labeled as adjudicated.
- Tests: a `CorpusManager` test that a PR with a reading set lifts the scored truth; the suite still scores the changed-files truth when no label is present.
- Docs: `benchmarks.mdx` Suite B section documents the two ground truths and which is adjudicated; the honest-reporting section notes the adjudication method and confidence intervals.
- Benchmark: Suite B recall and precision against the adjudicated set at 25K and 50K budgets, versus changed-files-only and a grep baseline.

## R5. Wider semantic analyzer set (moat width)

Each new wiring kind is a resolution the crown requires and that a tree-sitter graph or a Lucene index structurally cannot follow. This is the durable lead over CodeGraph and Serena.

- Work: add analyzers in `Fuse.Semantics` for EF Core (DbContext to entity to configuration), minimal-API route groups, gRPC and SignalR endpoints, open and closed generic DI, decorator and factory registrations, MediatR and ASP.NET pipeline behaviors, and IHostedService; emit new typed edge kinds.
- Tests: a fixture per wiring kind with an `expected-edges.json`, asserted by the existing fixture-edge harness; one analyzer unit test per kind.
- Docs: `concepts/glossary.mdx` and `internals/pipeline.mdx` (the new edge kinds), `reference/mcp-tools.mdx` (any new resolve targets).
- Benchmark: Suite A extended to the new fixtures (edge recall and precision per kind), and the resolve P@1 metrics added to the scorecard.

## R6. Suite A on the real wiring zoo with sampled adjudication

100 percent on one fixture is a toy claim. Expand fixtures to the full Section 18.3 catalog and add sampled adjudication of predicted edges over the OSS corpus with confidence intervals, so the moat headline is "P@1 across real .NET wiring", not "100 percent on a fixture".

- Work: build the fixture catalog (TryAdd, typeof pairs, factory lambdas, multiple-implementation ambiguity, open generics, route groups); add a sampled-adjudication mode to the semantics suite that samples N predicted edges per type for human or LLM verification and reports precision with CIs.
- Tests: edge-gold tests for each new fixture; an adjudication-sampling unit test on a tiny set.
- Docs: `benchmarks.mdx` Suite A section reports per-edge-type P@1 with stated ground truth and CIs; `concepts/precision-tier.mdx` notes the wiring coverage.
- Benchmark: per-resolver P@1, P@3, MRR, ambiguous-case handling, false-positive rate; corpus edge precision by sampled adjudication.

## R7. Warm host as the runtime plus a latency suite

"Warm and fast from a persistent index" is part of the crown and is unmeasured. Promote the host to the measured runtime and add a performance suite hitting the Section 18.7 targets.

- Work: drive resolve, localize, and review through the warm host in the latency suite; measure cold index, warm operation P50/P95, and single-file incremental re-index; ensure incremental indexing updates only the changed file's rows.
- Tests: an incremental-index test (edit one file, assert only its rows change); a warm-versus-cold timing assertion with a generous bound to avoid flakiness.
- Docs: rewrite `performance.mdx` with the measured warm latency numbers (replacing the current qualitative-only page); the operator page notes the host lifecycle.
- Benchmark: a `performance` suite writing `performance.json` with cold index time, warm P50/P95 per operation, and incremental update time per size tier.

## R8. Peer comparison and agent suite at scale

Win the head-to-heads once the engine is strong (R0 through R6). Run the peer scopers and the agent suite at scale, in semantic mode, with hybrid retrieval, against CodeGraph, Serena, and coa-codesearch.

- Work: re-run layer 6 (peers) and Suite D at 50 to 100 tasks x 3 rollouts, two agent variants (Fuse-first one-shot for PR review, agent-driven iterative for ambiguous edits), one pinned model; report distributions and per-repo splits, not just means.
- Tests: the suites already exist; add a small smoke that each peer toolbox is reachable and skips gracefully when absent.
- Docs: `benchmarks.mdx` peer and agent sections with the at-scale numbers, model id, run date, and confidence intervals; keep the model-dependent label.
- Benchmark: Suite D recall, tokens, tool calls, and a 0/1/2 sufficiency verdict per arm; layer 6 recall by repo with return-shape-aware precision.

## R9. Full task-resolution harness (the prize)

The only claim users care about: not "did it find the files" but "did the agent's patch pass the repo's tests with Fuse versus without". This is the real crown and the highest-credibility number.

- Work: a SWE-bench-style harness that, per task, runs an agent with and without the Fuse toolbox, applies the patch in an isolated worktree, and scores it against the repo's test oracle; record patch-applies, tests-pass, and pass@1.
- Tests: a harness unit test on a trivial fixture task with a known-good and known-bad patch; isolation tests that a failed task cannot corrupt the next.
- Docs: a new `benchmarks.mdx` section for task resolution, clearly labeled model-dependent and compute-heavy, with the oracle methodology.
- Benchmark: patch pass@1 with Fuse versus the bare baseline, per repo, with confidence intervals.

---

## Sequencing

R0 first; it multiplies the value of everything semantic. Then R1, R2, R3 in parallel (retrieval: recover and exceed the localize floor, attack the prose bucket, and abstain on no-signal). Then R4 with R5 and R6 (a credible review claim plus a wider, adjudicated moat). Then R7 (warm performance). Then R8 (head-to-heads at scale, once the engine is strong). R9 (the task-resolution prize) runs last or in parallel as a separate track, since it is the most compute-heavy.

## Honest ceilings (where "best of everything" trades off)

Two axes cannot be maxed simultaneously, so the crown there is the best frontier, not a single number:

- Review precision versus recall. A tighter return is higher precision and lower support-file recall; a wider blast radius is the reverse. Report review at multiple budgets along that curve rather than claiming both ends.
- No-signal localization. The recall ceiling is set by the input, not the engine. The crown is correct abstention (R3), not a recall number the title cannot support.

Everywhere else, the combination of semantic-mode indexing (R0), hybrid retrieval (R1, R2), a wider analyzer set (R5, R6), warm performance (R7), and a task-resolution oracle (R9) is a defensible path to being the most performant .NET context engine and beating the structural and lexical peers on the axes that are theirs to lose.

---

## Progress Log

Append a timestamped entry per item as it lands (Status / Result / Verification / Blockers / Lessons / Time), mirroring the archived history log.

### R0 Semantic-mode corpus (2026-06-26)

- Status: Done (engine and harness work complete; corpus-wide benchmark lift bounded by a corpus-pinning limit, reported straight).
- Result: Added `CorpusManager.RestoreAsync` (with `DotnetCli` and a `RestoreResult` record), wired restore into the review, localize, and agent suites behind a new `--restore` flag, and added `--require-semantic` to skip rather than silently score a checkout below semantic mode. The suites already recorded the achieved index mode; restore now lifts it where a checkout restores. Measured: on the corpus, restore lifts NewtonsoftJson and eShopOnWeb to partial mode (`localize.restore.json`, index modes partial 2, syntax 4); the other four repositories fail to restore on the installed SDK and stay in syntax. On the eShopOnWeb application, restoring each PR worktree lifts review to 15 of 18 PRs in partial mode (`review.restore.json`): changed-file recall 1.0, precision 0.64, median 587 returned tokens. Corpus-wide localization recall is unchanged at 27.3 percent (only two repositories lift; localization is still lexical, which R1 and R2 address).
- Verification: `dotnet build Fuse.slnx -c Release` 0 errors; `dotnet test Fuse.slnx -c Release --no-build` all green (Fuse.Benchmarks.Tests 24 -> 26, two new restore-to-semantic tests); `dotnet format --verify-no-changes` clean. `--require-semantic` smoke confirmed a syntax-mode repo is skipped loudly, not scored at the fallback.
- Blockers: AutoMapper, FluentValidation, MediatR, and Serilog do not restore on SDK 10.0.109 (central package management with an inline-versioned SourceLink, or an older target framework, against the newer SDK). This is a corpus-pinning limit, not an engine limit; fully unblocking it needs per-repo SDK or package pinning (corpus maintenance), tracked as follow-up. The mechanism is in place and `--require-semantic` makes the gap loud.
- Lessons: The DotMake.CommandLine source generator needs a non-incremental rebuild to pick up new `[CliOption]` properties; an incremental build silently omitted them from the parser. Restore success on a pinned OSS checkout is partial by nature on a newer SDK, so `RestoreResult` reports restored and failed project counts rather than a single boolean.
- Time: about 1 session.

### R1 Reconnect the lexical ranker (2026-06-26)

- Status: Done (BM25F rank preservation and PRF landed and measured; the centrality prior is graph-gated, see below).
- Result: Added `LexicalCandidateGenerator` in `Fuse.Retrieval` and made it the default lexical channel (replacing the flat `FtsCandidateGenerator` in `CreateDefault`). The persistent index already ranks chunk hits with field-weighted BM25; the prior generator discarded that rank, giving every hit a flat per-source weight, so the lexical order was lost at the noisy-or merge and the 20-candidate truncation. The new generator collapses hits to one candidate per file, carries a rank-decayed score (band ceiling for a name-field match, decaying to a floor with rank), and runs one capped round of pseudo-relevance feedback (top files' distinctive symbol names seed an expanded query; up to 8 new vocabulary-related files are added at a discounted weight). Measured on Suite C (`localize.r1.json` vs `localize.json`): overall changed-file recall 27.3 -> 30.3 percent, natural-language domain 23 -> 27 percent, at a precision cost (8.4 -> 6.6 percent) and a higher median return (about 2,658 -> 4,000 tokens) from the broader pool. Identifier-rich holds at 41 percent, short of the retired 49 percent floor.
- Verification: `dotnet build` 0 errors; `dotnet test` green (Fuse.Retrieval.Tests 30 -> 33: rank-preservation, PRF surfacing, empty-query). The `v3-localize` golden was regenerated (same candidates and order; OrderService's score reflects the new rank-decay, IOrderService stays 0.925 from exact resolution). `dotnet format` clean.
- Blockers: Identifier-rich did not recover past the retired 49 percent floor (41 percent). Two honest reasons: the corpus is mostly syntax mode here (R0), so there is no semantic blend to add to lexical, and Suite C recall is averaged over multi-file PRs at a 20-candidate cap, so naming one file does not lift the bucket past the floor. The graph-centrality prior is a no-op in syntax mode (no edges), so it was not wired into the title-only candidate stage; it remains in the graph-expansion path used by review and context. The prose bucket (the largest) needs the dense reranker, which is R2.
- Lessons: Uncapped PRF flooded the candidate pool and hurt precision and token count for a negligible recall gain; capping expansion-only files to the highest-ranked few kept the recall lift while limiting the precision cost. Recall-at-K over multi-file ground truth is insensitive to single-file rank improvements, so the lexical lift shows up more in the prose bucket than in identifier-rich.
- Time: about 1 session.

### R2 Hybrid retrieval with a warm dense reranker (2026-06-26)

- Status: Done (persisted per-chunk embedding index plus a dense candidate channel, model-gated, with a measured natural-language-bucket lift).
- Result: Added an `ITextEmbedder` abstraction (Fuse.Plugins.Abstractions) and a public `OnnxTextEmbedder` over the existing all-MiniLM-L6-v2 model; a `chunk_embeddings` table (schema v10 -> v11) with store `UpsertEmbeddingsAsync`/`GetEmbeddingsAsync`; the indexer persists a vector per chunk when an embedder is available; and a `DenseCandidateGenerator` in Fuse.Retrieval that embeds the query and ranks files by cosine over the persisted vectors, blended through the existing noisy-or scorer. Wired into the host (localize command, fuse_localize MCP tool) and the eval suite, gated on an explicit `FUSE_DENSE` opt-in plus model presence (embedding is an eager index-time cost, unlike the lazy reranker, so it is not enabled by model presence alone). Measured ablation (`localize.r2.json` vs `localize.r1.json`): the natural-language-domain bucket (the largest and weakest) rose from 27 to 34 percent, overall recall from 30.3 to 34.6 percent, route/API recovered from 0 to 12 percent, precision rose to 7.6 percent, median return about 5,000 tokens. Identifier-rich held at 41 percent (dense does not help where the title names the symbol). With no model, indexing writes no vectors and retrieval is byte-identical to lexical.
- Verification: `dotnet build` 0 errors; `dotnet test` all 15 projects green (Fuse.Retrieval.Tests 33 -> 36 dense rank/no-model/unavailable; Fuse.Indexing.Tests 20 -> 21 embedding persistence across disposal). `dotnet format` clean. The model is present in this environment, so the dense run exercised real ONNX inference over the corpus.
- Blockers: None. The dense channel is opt-in by design (model download is never automatic), so the default and CI paths are unchanged.
- Lessons: Gating eager index-time embedding on model presence alone would silently slow indexing for anyone who downloaded the model for the lazy reranker, so dense indexing needs its own explicit opt-in. Cosine over unit vectors is a dot product, so persisting L2-normalized embeddings keeps query-time scoring to a single pass with no per-query normalization. Loading the workspace's vectors once and caching them on the generator keeps repeated queries against one index warm.
- Time: about 1 session.

### R3 Low-signal detection and abstention (2026-06-26)

- Status: Done (low-signal classifier on the localize path with abstention; detection F1 0.11 -> 1.0, above the 0.80 target).
- Result: Added `QuerySignalClassifier` in Fuse.Retrieval, porting the benchmark's no-signal definition (merge, dependency-bump, and CI noise, plus an empty query with no structured input) so the engine is measured against the same ground truth. `LocalizeAsync` classifies first and, on low signal, returns a `LowSignal` verdict with a `SuggestedInput` (a base, route, or symbol) and no candidates, rather than the full-text junk a title cannot support. A request carrying any structured signal (route, symbol, service, request, config, base, or selected paths) is never downgraded. `LocalizationResult` gained `LowSignal` and `SuggestedInput`; the localize command, the fuse_localize MCP tool, and the localization suite surface and score the explicit verdict. Measured (`localize.r3.json`, 108 PRs, full hybrid): low-signal detection F1 1.0 (9 true positives, 0 false positives, 0 false negatives), up from 0.11. The no-signal bucket recall is 0 by design (abstention beats junk, the honest ceiling), so overall recall is 31.0 percent with the solvable buckets unchanged from R2.
- Verification: `dotnet build` 0 errors; `dotnet test` all 15 projects green (Fuse.Retrieval.Tests 36 -> 49: classifier theory cases for low and high signal, structured-signal rescue, and an engine abstention-payload test). `dotnet format` clean. The pre-existing zero-match warning path (a high-signal query that finds nothing) is preserved and distinct from abstention.
- Blockers: None. This is the honest ceiling the plan names: no-signal recall is bounded by the input, so the crown is correct abstention, achieved here at F1 1.0.
- Lessons: Scoring detection on the engine's explicit verdict rather than the incidental "zero candidates or any warning" heuristic measures the classifier itself; the old heuristic conflated a solvable query that happened to miss with a genuine no-signal title, which is why the baseline F1 was 0.11 despite the buckets being well defined. Keeping the noise patterns identical to the benchmark's `SignalBucket` avoids a train/test definition gap.
- Time: about 1 session.

### R4 Adjudicated support-set ground truth for review (2026-06-26)

- Status: Done (machinery: adjudicated reading set, grep baseline, two budgets; plus a curated pilot. The crown recall target is partly met, reported straight).
- Result: Added an optional `reading_set` to the PR ground truth (`prs.json` and `PrRecord`), lifted into the task as files with a `reading` role deduplicated against the changed set. `ChangeImpactSuite` now separates the changed-files truth from the adjudicated (changed-plus-reading) truth, runs every budget, and scores a grep baseline (rank the repo's C# files by title-token matches, admit to budget) alongside fuse review. Curated a single-adjudicator pilot of 5 PRs (MediatR #1171, #1159, #1136, #1058; FluentValidation #1823) whose support files (the interface a changed type implements and its consumer) were read from each diff and verified to exist at the PR head. Measured (`review.r4.json`, 108 PRs, budgets 25,000 and 50,000; index modes partial 36, semantic 14, syntax 58): fuse vs changed 100 percent recall at 78 percent precision in a median 1,108 tokens, identical at both budgets; grep vs changed 51 percent recall at 9 percent precision (59 percent at 8 percent at 50,000); fuse vs adjudicated (5-PR pilot) 60 percent recall at 74 percent precision. Review beats grep decisively on both axes.
- Verification: `dotnet build` 0 errors; `dotnet test` all 15 projects green (Fuse.Benchmarks.Tests 26 -> 27: a reading-set lifts the adjudicated truth with the reading role, distinct from changed files; a PR without a label carries only changed/test roles). `dotnet format` clean.
- Blockers: The crown target (adjudicated support recall at least 0.85 at at least 0.60 precision, beating changed-files-only and grep) is met on precision (74 percent) and on beating grep, but adjudicated recall is 60 percent, short of 0.85. Two honest reasons: the blast radius is bounded in this mostly-syntax corpus (R0 restores only two repos here), and the adjudicated set is a 5-PR single-adjudicator pilot. Scaling the adjudication (more PRs, a second adjudicator or a strong model) and the semantic-mode coverage is the path to the target.
- Lessons: The two budgets returned identical numbers because review returns compact scoped context (a median 1,108 tokens) far under both ceilings, so the budget is not the binding constraint and the precision-recall frontier is not exercised on this corpus; the frontier shows up only when the returned set approaches the budget. Adding reading-role files to the same ground-truth list meant the existing changed-files scoring had to be made role-aware, or it would have silently started scoring against the adjudicated set.
- Time: about 1 session.

### R5 Wider semantic analyzer set (2026-06-26)

- Status: Done. Part 1 landed hosted services, MediatR pipeline behaviors, and EF Core; part 2 added decorator and factory-lambda DI edges and an endpoint analyzer for minimal-API, gRPC, and SignalR. Open and closed generic DI was already covered by the typeof-pair path. Suite A now scores 19 of 19 edges at 100 percent recall and precision (up from 10).
- Result: Added `HostedServiceAnalyzer` (`service:IHostedService -> worker : hosted_service`), `PipelineBehaviorAnalyzer` (`service:IPipelineBehavior -> behavior : pipeline_behavior`), and `EfCoreAnalyzer` (`context -> entity : ef_entity` and `entity -> configuration : ef_configures`), with a `SyntheticNodes.Service` helper for the named-contract endpoints, registered in `SemanticAnalysisRunner.CreateDefault`. Extended the OrderingApp fixture with a background worker, a pipeline behavior, and an EF Core context/entity/configuration (self-contained stubs in `Framework.cs`, no package references) and added their expected edges. Open and closed generic DI is resolved through the existing `typeof`-pair path in `DiRegistrationAnalyzer`, so it needed no new analyzer. Suite A now scores 14 of 14 edges at 100 percent recall and precision (`semantics.json`), up from 10.
- Verification: `dotnet build` 0 errors; `dotnet test` all 15 projects green (Fuse.Semantics.Tests 63 -> 70: three analyzer unit tests plus four fixture-edge cases). `dotnet format` clean. `fuse eval semantics` records 14 of 14.
- Part 2 result: Extended `DiRegistrationAnalyzer` to emit `di_decorates` for Scrutor `Decorate<TService, TDecorator>()` and to recover a factory's implementation from the lambda body (`AddSingleton<IClock>(sp => new SystemClock())` now emits `di_resolves_to`). Added `EndpointAnalyzer` for minimal-API method-group handlers (`MapGet`/`MapPost` -> `route_handles`), gRPC (`MapGrpcService<T>` -> `grpc_endpoint`), and SignalR (`MapHub<T>` -> `signalr_endpoint`), with stubs and fixture wiring for each. The factory change updated an existing DI test expectation (the impl is now recovered rather than null), which is the intended behavior improvement.
- Verification: `dotnet build` 0 errors; `dotnet test` all 15 projects green (Fuse.Semantics.Tests 63 -> 75 across both parts). `dotnet format` clean. `fuse eval semantics` records 19 of 19 edges, 100 percent recall and precision.
- Blockers: None.
- Lessons: Compiling the fixture in-memory for unit tests (`OrderingAppFixture.Load`) means a new wiring kind needs only a stub type in `Framework.cs` and a source file, with no MSBuild restore, so analyzers are cheap to test; Suite A then exercises the same fixture through the real MSBuild load. Synthetic named-contract endpoints (a `service:` node) keep an edge's endpoints concrete when the "from" side is a framework interface or a synthetic contract (gRPC, SignalR), which the every-endpoint-has-a-node invariant requires. A method group in a delegate-conversion position can resolve into `CandidateSymbols` rather than `Symbol`, so the endpoint analyzer checks both.
- Time: about 1 session.

### R6 Suite A on the wiring zoo (2026-06-26)

- Status: Done. Part 1 expanded the fixture catalog to the Section 18.3 edge-case set (Suite A 22 of 22 edges, 100 percent recall and precision, including the ambiguity false-positive check). Part 2 added the corpus sampled-adjudication mode and a self-adjudicated sample. The corpus-wide per-resolver ranking metrics with tight intervals stay bounded by the sparse semantic-mode corpus (R0), which is an environment limit, not an engine gap.
- Part 2 result: Added a deterministic `EdgeSampler` (seeded shuffle, N per edge type) and a `--corpus-sample N` mode on the semantics suite that indexes the corpus, extracts the predicted edges, samples per type, and writes `semantics-corpus-sample.json` for adjudication. Run with `--restore`, FluentValidation and Serilog reached partial mode and produced 261 edges; the sample was 24 (8 each of di_injects, implements, inherits). A single-adjudicator pass against the corpus source found all 24 structurally correct (Wilson 95 percent lower bound about 86 percent), with one honest nuance: di_injects captures any constructor-parameter dependency, so it includes value and enum parameters (a LogEventLevel), not only DI-container services.
- Verification: `dotnet build` 0 errors; `dotnet test` 15 projects green (Fuse.Benchmarks.Tests 33 -> 36: EdgeSampler cap, reproducibility, under-cap). One transient flake in Fuse.Fusion.Tests under full-parallel execution did not reproduce in isolation (237 of 237) or on re-run and is unrelated to these changes (no Fuse.Fusion code was touched). `dotnet format` clean. `fuse eval semantics --corpus-sample 8 --restore` produces the sample.
- Result: Extended the OrderingApp fixture with the edge-case wiring catalog: an explicit open generic registration (`AddScoped(typeof(IRepository<>), typeof(Repository<>))`, with the unbound-generic node id normalized to its `<T>` definition so the resolve edge connects), a TryAdd registration (`TryAddSingleton<ICache, MemoryCache>`), and a multiple-implementation ambiguity (`IShipping` with two implementations, only `FastShipping` registered). Suite A now scores 22 edges; the ambiguity case is the precision side of the moat, verified by both Suite A precision (no false positive to `SlowShipping`) and a dedicated unit test.
- Verification: `dotnet build` 0 errors; `dotnet test` all 15 projects green (Fuse.Semantics.Tests 75 -> 79: three new fixture-edge cases plus an ambiguity precision test). `dotnet format` clean. `fuse eval semantics` records 22 of 22.
- Blockers: A larger, tighter corpus-wide precision number is bounded by the sparse semantic-mode corpus (R0: most repos load syntax, which emits no graph edges to sample), so the adjudicated sample is 24 edges over two partial-mode repos. The mechanism is reproducible and will produce a larger sample once more of the corpus restores; the engine work is complete.
- Lessons: An unbound generic from `typeof(IRepository<>)` displays as `<>` and would not match the type's declaration node id `<T>`; normalizing to `OriginalDefinition` keeps the open-generic resolve edge connected to the type node. Testing ambiguity needs both a positive assertion (the registered impl resolves) and a negative one (the unregistered impl does not), which is the precision half the recall-only fixtures did not exercise.
- Time: about 1 session.

### R7 Warm host and latency suite (2026-06-26)

- Status: Done. A `performance` suite measures cold index, warm resolve/localize/review latency, and single-file incremental re-index; every warm operation is inside its crown target. The warm numbers run through an in-process warm store, which is the same store the host holds resident.
- Result: Added `PerformanceSuite` (`fuse eval performance`, `performance.json`) and a `SemanticIndexer.ReindexFileAsync` incremental path that clears and re-extracts one file's syntax rows (symbols, chunks, full-text, routes). Measured on AutoMapper (518 files, 17,924 symbols, syntax mode): resolve sub-millisecond (target under 100 ms), localize 20 ms P50 / 22 ms P95 (under 500 ms), review plan 98 ms P50 / 103 ms P95 (under 2 s), incremental re-index 177 ms P50 / 186 ms P95 (under 1 s); cold index about 64 seconds including the MSBuild load attempt. Corrected the performance docs, which had claimed incremental re-index already worked when a re-index in fact ran a full pass.
- Verification: `dotnet build` 0 errors; `dotnet test` all 15 projects green (Fuse.Benchmarks.Tests 27 -> 33 percentile helper; Fuse.Semantics.Tests 79 -> 80 incremental re-index updates only the changed file). `dotnet format` clean. `fuse eval performance` records all four latencies.
- Blockers: None. The incremental path updates the file's syntax rows, not cross-file semantic edges (DI, route, MediatR, EF), which a full re-index refreshes; this is the correct scope for an edit-driven warm session and is documented. Driving operations through the host RPC process rather than an in-process store would add the process round-trip, but the warm store and its latencies are the same.
- Lessons: A single-file change can invalidate cross-file semantic edges, so an incremental path that recomputed them would need whole-compilation analysis; scoping incremental to the file's own syntax rows keeps it under the 1 s target while a full re-index remains the way to refresh edges. Cold index is dominated by the MSBuild workspace load attempt even when the result falls back to syntax mode, so cold and warm are different regimes and only warm is the steady state.
- Time: about 1 session.

### R8 Peer comparison and agent suite at scale (2026-06-26, partial)

- Status: Agent suite (Suite D) re-run at a doubled sample and recorded; the peer head-to-head and the full 50-to-100-task by 3-rollout scale remain. Box unticked until peers run.
- Result: Re-ran Suite D through the real `fuse.exe` (so the fuse arm's MCP server is correctly wired, fixing a path bug where running via `dotnet fuse.dll` pointed the MCP command at `dotnet.exe`) with `--restore`, over 12 PRs (two per repo) and one rollout per arm (24 rollouts, claude-sonnet-4-6). Recorded `agent.json`: fuse mean recall 30 percent at a median 211,502 cumulative tokens, native 26 percent at 209,182; precision 44 versus 43 percent. Fuse edges out native on recall at comparable token cost on this small, single-rollout, model-dependent sample, doubling the prior 6-PR sample.
- Verification: build, test (15 projects), and format green (no engine code changed; the run is a benchmark plus a docs resync). `fuse.exe eval agent --restore --limit 2` produced the run; a single-PR validation first confirmed the fuse MCP server starts (no `dotnet.exe` path warning, fuse tools used).
- Blockers (concrete): (1) the peer tools (CodeGraph, Serena, coa-codesearch) are not installed on this branch, so the head-to-head needs an external install; (2) the full 50-to-100-task by 3-rollout scale is 300 to 600 real rollouts (hours of compute); (3) semantic mode for the fuse arm is bounded by R0 (only some repos restore). These are resource limits, not engine gaps.
- Lessons: Suite D must run through `fuse.exe`, not `dotnet fuse.dll`: the suite derives the fuse MCP server command from `Environment.ProcessPath`, which is the dotnet host under `dotnet <dll>`, so the fuse arm silently degrades. Always validate one rollout (warning-free, fuse tools used) before a long run.

### R9 Full task-resolution harness (2026-06-26, partial)

- Status: In progress. The deterministic core of the SWE-bench-style harness is built and tested; the agent-driven, at-scale pass@1 comparison (with and without Fuse) is compute-bounded and not run. The box stays unticked until that run lands.
- Result: Added `TaskResolutionHarness` (with `GitCli.RunWithStdinAsync` for `git apply -`, a generic `ProcessRunner` for the oracle, and `OracleCommand`/`TaskResolutionResult` records). Per task it materializes an isolated git worktree at the base commit, applies a candidate patch via stdin (bounded args), runs an external test oracle, and reports patch-applied and tests-passed; the worktree is always removed so a failed or destructive patch cannot corrupt the next task. This is the engine that scores patch pass@1.
- Verification: `dotnet build` 0 errors; `dotnet test` 15 projects green (Fuse.Benchmarks.Tests 36 -> 39: a trivial git fixture proves the oracle, with a known-good patch passing, a known-bad patch failing, and a failing task not corrupting the next). `dotnet format` clean.
- Blockers (concrete): the at-scale run drives a real agent (the Claude Code CLI) per task across two arms (with and without the Fuse toolbox) to produce the patch, then applies and tests it; that is hours of model compute over many tasks and needs provisioned credentials and budget. The test oracle also needs the corpus repository to build and run its tests, which is bounded by R0 (most repos do not restore on this SDK). The deterministic core is complete and trusted; the prize number needs the compute and a buildable corpus.
- Lessons: Separating the oracle core (apply plus test plus pass@1, deterministically testable with a trivial fixture) from the agent-driven patch generation (compute-heavy, model-dependent) lets the harness be trusted before any expensive run; passing the patch via stdin keeps the apply step bounded regardless of patch size, matching the AGENTS.md external-process invariant.
- Time: about 1 session.
