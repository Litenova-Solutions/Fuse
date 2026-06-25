# Fuse Improvement and Implementation Playbook

A working implementation guide for raising Fuse's scoping recall, precision, token efficiency, and
round-trip story, plus a plan for more honest benchmarking. Written so a fresh session can pick up any
item and implement it without re-deriving the context. Not part of the published docs.

Branch: continue on `feature/roslyn-mandatory-sqlite-cache` (do not branch off, do not merge or push;
commit per feature when build, test, and format are green).

## Working principles for this effort (read first)

1. **Breaking changes are fine.** No backward compatibility, no migration shims, no deprecation
   windows. The CLI surface, MCP tool shapes, the options model (`FusionRequest`, `ReductionOptions`,
   `QueryOptions`, env flags), result JSON shapes, and on-disk cache schema may all change freely.
   If a clean redesign is better than an additive patch, do the redesign. Update the docs and the
   benchmark harness to match in the same change; do not keep a compatibility path alive.
2. **Tests and thorough documentation on every feature.** Definition of done for any item here:
   - Unit tests for the new logic and a harness regression where retrieval or packing changed.
   - XML docs on every new or changed public and protected member in `src/**/Fuse.*`.
   - User-facing MDX docs updated under `site/content/docs` when a flag, tool, option, or behavior an
     agent or operator sees changes; `CHANGELOG.md` `[Unreleased]` updated; benchmark numbers in
     `benchmarks.mdx` and `AGENTS.md` regenerated and synced.
   - `dotnet build` (0 warnings), `dotnet test`, and `dotnet format --verify-no-changes` all green.
3. **Nothing here is sealed.** This plan is a starting point, not a contract. If there is a better way
   to make agents work better and faster, change it: rewrite, replace, or delete code, restructure the
   pipeline, drop an item, or add one not listed. The item numbers and order are guidance. The only
   fixed constraints are the hard invariants below (plain ASCII docs, no fabricated benchmark numbers,
   the lexical path always works with no model present, `JsonSerializerContext` for JSON, redaction
   correctness, Roslyn for structural C#). Everything else is open to redesign in service of agent
   speed and answer quality.

---

## 0. Orientation (read first)

### What Fuse is

A .NET codebase context optimizer. It collects source files, reduces them for token efficiency, and
emits one structured payload. Scoping modes that matter here:
- `--query "<text>"`: BM25F lexical ranking of files, expanded along the dependency graph. Most items
  below improve this mode.
- `--changed-since <ref>`: git-diff scoping (already strong, 88 percent recall).
- `--focus <type|file>`: dependency neighbourhood of a named seed.

### Repository layout

- `src/Core/Fuse.Collection`: file collection.
- `src/Core/Fuse.Reduction`: reduction stages (`ContentReductionPipeline.cs`), redaction
  (`Security/`), reduction cache (`Caching/ReductionHasher.cs`).
- `src/Core/Fuse.Emission`: emission and writers.
- `src/Core/Fuse.Fusion`: the orchestrator and all scoping logic. Most work lands here.
- `src/Plugins/Fuse.Plugins.Languages.CSharp` and `...CSharp.Roslyn`: skeleton, chunk, dependency,
  type-name extractors (capabilities). Roslyn is the structural tier for `.cs`.
- `src/Host/Fuse.Cli`: CLI and MCP server (`Mcp/FuseTools.cs`).
- `tests/Fuse.Fusion.Tests`: unit tests (scoping tests under `Scoping/`). `tests/Fuse.GoldenOutput.Tests`:
  golden output.
- `tests/benchmarks/harness`: benchmark scripts. `tests/benchmarks/results`: committed results.
- Solution: `Fuse.slnx`.

### Build, test, format, benchmark (exact commands)

```
dotnet build Fuse.slnx -c Release
dotnet test Fuse.slnx -c Release --no-build
dotnet format Fuse.slnx --verify-no-changes
# Harness uses the built CLI exe at src/Host/Fuse.Cli/bin/Release/net10.0/fuse.exe; rebuild it before
# benchmarking after a code change:
dotnet build src/Host/Fuse.Cli/Fuse.Cli.csproj -c Release
# Layers (PowerShell):
pwsh -File tests/benchmarks/harness/layer2a.ps1          # scoping recall@budget (no network)
pwsh -File tests/benchmarks/harness/layer2b.ps1          # single-turn localization (no network)
pwsh -File tests/benchmarks/harness/layer4-scenario.ps1  # one-call context vs no-fuse/Repomix (needs npx)
pwsh -File tests/benchmarks/harness/run-all.ps1          # everything (layer1 fidelity, etc.)
# Fast query-mode A/B (24 PRs, query only, ~5 min):
pwsh -File tests/benchmarks/harness/spike-query-expansion.ps1
```

Corpus: `tests/benchmarks/.corpus` with `tests/benchmarks/prs.json` (24 PRs; ground truth is the .cs
files each PR changed) and `tests/benchmarks/corpus.json`. Layer 2A and 4 reconstruct each PR head in a
git worktree and measure recall at budgets 10k, 25k, 50k (50k is the headline).

### Design invariants (do not break)

- Roslyn syntax for structural C# (skeleton, dependency, type-name, chunk, slice). Regex only for
  lexer-level reduction and non-structural paths.
- The lexical BM25F plus syntax graph path must always work with no model present and no network
  (offline, CI, air-gapped, CLI without a downloaded model). This is the floor and never regresses.
- Embedding model policy (revised): a local in-process ONNX embedding model MAY be the DEFAULT for the
  MCP server, which auto-downloads and caches it on first use or at serve start, because MCP users run
  long-lived on resourceful .NET dev machines. The CLI does NOT silently download: it prompts on first
  use of a model-backed feature, waits for the download, and in non-interactive runs (CI) fails with a
  clear message or requires an explicit opt-in flag. When the model is absent or download is declined,
  every mode degrades gracefully to the lexical path. Compiled reference graph and external LLM rewrite
  stay opt-in. No network on the lexical path itself.
- Persistent cache and index in a single SQLite file at `.fuse/fuse.db` (WAL).
- JSON via source-generated `JsonSerializerContext` only (no reflection serialization).
- Public and protected API in `src/**/Fuse.*` needs XML docs.
- All prose and docs are plain ASCII: no em dash, no arrow glyphs, no emoji. Non-ASCII only inside
  fenced code blocks. A stop hook enforces this.
- Never fabricate or weaken a benchmark number. Regenerate results and sync the numbers in
  `site/content/docs/project/benchmarks.mdx` and the headline figures in `AGENTS.md`.
- Redaction (`ISecretRedactor.Redact`) runs INSIDE `ContentReductionPipeline.ReduceAsync` (lines 147 to
  154). Any transformation that reads original source AFTER reduction therefore bypasses redaction.
  This is a load-bearing invariant for several items below.

### The query scoping pipeline (current code path)

In `src/Core/Fuse.Fusion/FusionOrchestrator.cs`:

1. `FilterByQueryAsync` builds `IndexedDocument`s (content, path, declared symbols) for every collected
   file, indexes them in a per-run `Bm25RelevanceIndex`, and calls `RankScored(query, TopFiles)`
   (TopFiles default 10).
2. Pseudo-relevance feedback (shipped): `PseudoRelevanceExpander.Expand(...)` harvests recurring
   declared-symbol terms from the top files, `RankScored(weightedTerms, TopFiles)` re-ranks, and
   `MergePreservingSeeds` unions the two rankings. Off via `FUSE_QUERY_EXPANSION=0`.
3. `SelectQueryMembers` picks, per matched file, the members the query is about (for thin skeleton).
4. `BuildGraphAsync` builds the dependency graph; `_focusSeedResolver.Expand` does a best-first,
   rank-decayed, budget-aware traversal (forward edges only for query) producing `PathExpansionResult`
   with `IncludedPaths`, `ProvenanceChains` (hop sequence per file; a seed's chain is `[path]`, length
   1; a neighbour's chain length is greater than 1), and `Scores`.
5. Returns `FilteredFileSet(filtered, ProvenanceChains, Scores, SelectedMembers)`.

Then in `FuseAsync`:

6. `_reductionPipeline.ReduceAsync(...)` reduces the filtered files (one global `ReductionOptions`).
7. `ApplyThinSkeletonAsync` (line ~394, re-reads source at line 417, replaces at 426) rewrites matched
   files to a thin skeleton using `SelectedMembers` and `ThinSkeletonAssembler.Assemble(source, chunks,
   members)`. `ApplySymbolSliceAsync` similarly re-reads source.
8. `PostReductionContext` is built (carries `Provenance`, `Scores`, `SelectedMembers`).
9. `PostReductionEnrichmentPipeline.ProcessAsync` attaches provenance/relevance, then for scoped runs
   with a `MaxTokens` budget calls `ReductionAwarePacker.Pack(reducedContent, maxTokens, overhead)`, a
   greedy relevance-per-token knapsack, then emits. Manifest, route/project maps, review preamble,
   session note, redaction report, and pattern summary are prepended/appended AFTER packing.

Key APIs:
- Relevance index (`Scoping/IRelevanceIndex.cs`, impl `Bm25RelevanceIndex.cs`): `Rank(query, topN)`,
  `RankScored(query, topN)`, `RankScored(IReadOnlyDictionary<string,double> weightedTerms, topN)`,
  `InverseDocumentFrequency(term)`. BM25F field boosts are `[body 1.0, symbols 5.0, path 3.0]`,
  `K1 = 1.2`.
- Expansion (`Scoping/ExpansionOptions.cs`): `Depth`, `FollowReferences`, `FollowDependents`,
  `HopDecay` (0.5), `TokenBudget`, `TokenCosts`, `Centrality`, `CentralityWeight` (env
  `FUSE_CENTRALITY_WEIGHT`, default 0.15). `GraphCentrality.Compute` is normalized in-degree.
- DI in `Extensions/ServiceCollectionExtensions.cs`: capabilities register as
  `AddSingleton(sp => new CapabilityRegistry<T>(sp.GetServices<T>()))`; index is
  `AddSingleton<Func<IRelevanceIndex>>(_ => () => new Bm25RelevanceIndex())`.

---

## 1. Measured baselines (real, from the harness)

| Metric | Value |
|--------|-------|
| Layer 2A query recall at 50k | 51 percent mean (MediatR 94, FluentValidation 51, AutoMapper 29, Newtonsoft.Json 30) |
| Layer 2A query recall at 10k / 25k | 34 percent / 43 percent |
| Layer 2A changes recall / precision at 50k | 87 percent / 50 percent (C2 strict budget: emission stops before the entry that would cross the cap) |
| Layer 2A focus recall at 50k | 71 percent |
| Layer 2A grep baseline at 50k | 38 percent |
| Layer 2B localization (20k) | query 42 percent, focus 42 percent, grep 58 percent |
| Layer 4 one-call context | Fuse 53 percent recall at about 44,700 tokens; no-fuse and Repomix 100 percent by construction at about 494,000 to 512,000 tokens |
| Layer 1 reduction at full API fidelity | default 7 to 10 percent, aggressive 21 to 40 percent, skeleton 66 to 93 percent; 99 to 100 percent of public types and methods kept |

Stretch targets: query at 50k 60 to 68 percent-plus; Layer 4 58 to 65 percent-plus at 38k tokens or
fewer; changes 90 to 94 percent at 55 percent-plus precision; Layer 2B query at least 58 percent; hard
repos (AutoMapper, Newtonsoft.Json) query 40 percent-plus.

## 2. Why the numbers are where they are (the two walls)

1. Vocabulary wall: BM25 and grep only find files that share words with the query. A title that shares
   no vocabulary with the changed files is unreachable lexically.
2. Budget and graph wall: the dependency graph is Roslyn syntax, not a compiled model, so it misses
   edges through generics, extension methods, reflection, and cross-assembly calls. On large change
   sets (Newtonsoft.Json PRs touch 20 to 25 files) a 50k budget cannot hold them, so truth files
   truncate.

AutoMapper (29) and Newtonsoft.Json (30) drag the mean; MediatR (94) is near the ceiling.

Product framing decision: treat Fuse as an interactive, MCP-routed retrieval loop, not one-shot
context. That makes routing (item 13), session delta (item 14), agentic breadcrumbs (item 30), and the
Layer 4 arm changes (item 13) more valuable than squeezing single-call query recall. Dense rerank
(item 9) is now in scope as the MCP default rather than a deferred opt-in, since MCP users have the
machine and the time; the heavier items (8 compiled tier, 10, 11) stay later bets. The eventual
headline metric is tokens-and-round-trips-to-correct-localization (B1); one-shot query recall stays as
the honest cold-start fallback.

## 3. Work item index

Effort: S = 2 to 5 days, M = 1 to 2 weeks, L = 2 to 4 weeks, XL = 1 to 3-plus months. Path: default
(lexical, no model) or opt-in. Detail sections follow.

Phase 0, correctness and safety (section 4):

| # | Item | Effort | Value |
|---|------|--------|-------|
| C1 | Re-run redaction after post-reduction source rewrites (or move rewrites into reduction) | S | High (security) |
| C2 | Strict total-token accounting for MaxTokens | M | Medium |
| C3 | Verify and fix DI lifetime/concurrency under MCP | M | High (correctness) |
| C4 | Verify and fix SQLite pending-write race; pool the store in serve | S to M | Medium |
| C5 | Stable symbol identity (StableId) for chunk operations | M | Medium (precision) |
| C6 | Whole-PEM-block and cloud-key redaction patterns | S | Medium (security) |

Phase 1, architecture enablers (section 5):

| # | Item | Effort | Value |
|---|------|--------|-------|
| A1 | First-class `ContextPlan` (role + tier per file) | M | High (enabler) |
| A2 | Extract `QueryScopingPipeline` from the orchestrator | M | Medium (testability) |
| A3 | `TokenCostModel` (unify pre-reduce estimate and post-reduce count) | S to M | Medium (enabler) |
| A4 | Separate candidate / seed / emit counts | S | Medium (enabler) |

Phase 2, default-path retrieval (section 6):

| # | Item | Path | Effort | Value | Theoretical lift (illustrative) |
|---|------|------|--------|-------|---------------------------------|
| Q1 | Query-path single-pass and parallel indexing | default | M | Medium (latency) | latency only |
| 4 | Budget-aware expansion with tiered cost estimates | default | M | Medium | recovers 10k/25k dip; +1 to 3 pts |
| 1 | Tiered emission (skeletonize neighbours, via per-file levels) | default | M | High | +3 to 6 pts, most on large-change repos |
| 2 | Multi-query fusion (RRF of title/identifier/symbol variants) | default | S to M | Medium | +1 to 3 pts |
| Q2 | Fielded comments/doc-comments and explicit field weights | default | S to M | Medium | +1 to 3 pts on prose queries |
| Q3 | Exact symbol/path boosts | default | S | Low to Med | precision and prose queries |
| 3 | Identifier-aware tokenization upgrade | default | S | Low to Med | +1 to 2 pts |
| Q4 | Local distributional thesaurus (PMI identifier co-occurrence) | default | M | Medium | crosses vocabulary wall, no model |
| Q5 | Member-level retrieval (chunk-fielded docs, roll up to file) | default | M | Medium | large single-file truth sets |
| Q6 | Git churn / recency prior (see leakage caveat) | default | S | Medium (production) | benchmark-leaky; real-world only |
| 7 | Graph edge weights, edge kinds, soft proximity edges | default | M | Medium | precision and budget-bounded recall |
| Q7 | PageRank / personalized-PageRank centrality and expansion | default | M | Low to Med | bounded by centrality weight |
| 6 | False-edge fixes (conditional compilation, nested types) | default | M | Medium | mainly Newtonsoft changes/focus |
| 5 | Scalar admission tuning (only with B2 + B5) | default | M | Low to Med | +1 to 3 pts, high overfit risk |
| 24 | Persistent relevance stats / repo-scoped index cache | perf | M | Medium | warm-call latency |

Phase 3, packing and emission (section 7):

| # | Item | Path | Effort | Value |
|---|------|------|--------|-------|
| P1 | Tiered, role-aware knapsack with per-chunk admission and downgrade-before-drop | default | M to L | High |
| 16 | Deterministic file sketches for huge files | default | M | Medium |
| 30 | Agentic next-best-action breadcrumbs | default | S | High (agent UX) |
| P2 | Structural-map prepend to first part (multipart fix) | default | S | Low |

Phase 4, MCP, routing, interactive loop (section 8):

| # | Item | Path | Effort | Value |
|---|------|------|--------|-------|
| 13 | Auto-mode routing (prefer change mode with a git base) | default | M | High |
| 31 | CLI `ask` command mirroring `fuse_ask` (harness enabler) | default | S | High (enabler) |
| 32 | Change-anchored / hybrid change+query (spike) | default | S to M | Medium to High |
| 14 | Session-delta improvements with diff overlays | default | L | High (multi-turn) |
| 33 | `fuse_find` (symbol/text/path) and `fuse_explain` MCP tools | default | M | Medium |

Phase 5, opt-in heavy levers (section 9):

| # | Item | Path | Effort | Value |
|---|------|------|--------|-------|
| 9 | Dense rerank on BM25 top-K (ONNX MiniLM; MCP default if it pays off, CLI prompted) | default (MCP) / prompted (CLI) | L | High on hard repos |
| 8 | Reference graph: `.csproj` coarse first, then compiled tier | opt-in, cached | L to XL | High on hard repos |
| 10 | Learned sparse retrieval (SPLADE-style) | opt-in | XL | High |
| 11 | Cross-encoder rerank | opt-in | L to XL | High on hard repos |
| 23 | Persistent vector cache (when 9 is on) | opt-in | M | depends on 9 |
| 12 | LLM query rewrite / HyDE (heuristic stays default; LLM opt-in) | mixed | M | Medium |
| F1 | SQLite FTS5 BM25 backend (only if profiling demands it) | default | L | architectural |

Benchmarking (section 10): B1 task-success eval, B2 larger/cleaner corpus, B3 ranking metrics
(nDCG/MRR/recall@k), B4 decouple recall@k from recall@budget, B5 held-out dev/test split, B6 bootstrap
confidence intervals, B7 adversarial-case reporting, B8 precision and wasted tokens, B9 per-repo CI
gate, B10 stratify by change-set size, B11 cost-adjusted recall, B12 title-only vs title+body, B13
latency layer, plus reading-set ground truth and experimental-flags-recorded-in-results.

---

## 4. Phase 0: correctness and safety (do before feature work)

These can distort A/B results or cause real defects once MCP usage is concurrent.

### C1: Re-run redaction after post-reduction source rewrites (verified defect)

`ApplyThinSkeletonAsync` (orchestrator line 417) and `ApplySymbolSliceAsync` re-read raw source after
`ReduceAsync` has already run, and redaction runs inside `ReduceAsync`. The assembled skeleton or slice
content therefore never passes the redactor, so a kept member body, field initializer, attribute
argument, or const literal can re-introduce a secret the normal path would have removed. Tiered
emission (item 1) would add a third instance.

Fix, in order of preference: move slicing, thin skeleton, and neighbour skeletonization into the
reduction stage so a per-file level decides the tier before redaction (this is item 1 done through item
A1/21); or, short term, run `ISecretRedactor.Redact` again on any rewritten content before
`WithReducedContent`. Add the hard invariant to the code and tests. This is a standalone bug fix for
the shipped thin-skeleton and slice paths, not only a prerequisite for item 1.

Test: a file with a secret in a kept member body emerges redacted after thin skeleton.

### C2: Strict total-token accounting

`ReductionAwarePacker.Pack` bounds packed file bodies to `MaxTokens`, but the manifest, route/project
maps, review preamble, session note, redaction report, and pattern summary are added after packing and
are not charged against `MaxTokens`, so the returned payload can exceed it. Add an exact accounting
mode: format entries, count formatted tokens plus all prefixes and suffixes, and downgrade or drop
before writing so an MCP payload truly fits the requested budget. Also verify whether
`TokenBudget.Consume` can mark exhausted after an entry is already written in the emission writer and
fix if so. The harness measures actual output tokens, so existing layer numbers are not fabricated;
this is about a hard MCP guarantee.

### C3: DI lifetime and concurrency under MCP (verify, then fix)

`FusionOrchestrator` is a singleton. Verify in `ServiceCollectionExtensions.cs`: a transient
`FileCollectionPipeline` injected into a singleton becomes effectively singleton, and if
`GitIgnoreFilter.SetPatterns` mutates shared filter state, concurrent MCP calls against different
repositories can race on gitignore patterns. Pattern detectors registered as singletons whose state
`PatternDetectionBatch` resets and accumulates can corrupt pattern summaries under concurrency. If
real, make the orchestrator or collection pipeline scoped or stateless, pass gitignore patterns without
mutating shared instances, and instantiate a fresh detector batch per run. Assume concurrent tool calls
in serve mode.

### C4: SQLite pending-write race and per-call store (verify, then fix)

Verify `SqliteKeyValueStore.FlushAsync`: it reportedly snapshots `_pending`, writes, then removes the
snapshot keys, which could drop a concurrent update to the same key during flush. If real,
remove-only-if-unchanged or lock the snapshot and removal. Also `FuseStoreFactory.Open` reportedly
opens a new store per `FuseAsync` call, so the serve path does not share the pending cache; a
repo-root-keyed pooled store with its own flush lifecycle is the right shape, and pairs with item 24.

### C5: Stable symbol identity

`SymbolChunk.QualifiedName` is `ParentType is null ? SymbolName : $"{ParentType}.{SymbolName}"`, which
collides for overloads, nested types, the same name across namespaces, and partial classes. This
affects `SelectQueryMembers`, `ThinSkeletonAssembler`, `BodyDeduplicator`, slicing, and tiered
emission. Add a `StableId` (namespace plus containing types plus member plus arity plus parameter
types) and key member operations on it.

### C6: Redaction pattern hardening

Verify `Fuse.Reduction.Security`: redact whole PEM blocks (BEGIN through END PRIVATE KEY), not only the
BEGIN line, and audit against a current set of cloud and provider key patterns. This matters more once
skeletons and slices are redaction-correct (C1) and output goes straight to an agent.

---

## 5. Phase 1: architecture enablers

### A1: First-class `ContextPlan`

Replace the implicit `FilteredFileSet` (where seed vs neighbour is inferred from provenance chain
length) with an explicit plan: `PlannedFile` carrying `Role` (Seed, Dependency, Dependent, Changed,
Caller, Support, Overview), `Tier` (Full, Standard, Aggressive, ThinSkeleton, Skeleton, PublicApi,
Sketch), score, provenance, selected members, and a `MustKeep` flag. The pipeline becomes: collect,
build `ContextPlan`, reduce each file at its planned tier, pack by role and real per-tier cost, emit.
This single refactor unlocks items 1, 16, P1 (role-aware packing), item 33 (fuse_explain), and cleaner
benchmarking. It is the backbone for tiered emission done correctly.

### A2: Extract `QueryScopingPipeline`

Pull index, PRF, fusion, member selection, and graph expansion out of `FusionOrchestrator` into a
testable unit. The orchestrator already does collection, scoping, reduction, thin skeleton, TOC,
enrichment, and packing; items 1, 2, 4 make it harder to test in isolation.

### A3: `TokenCostModel`

One interface that estimates tokens pre-reduce and reconciles post-reduce. Today expansion can use
`TokenCosts`, reduction produces real counts, and the packer uses post-reduction counts; unifying them
keeps items 1, 4, and P1 from drifting and is the foundation for budget-aware expansion.

### A4: Separate candidate, seed, and emit counts

Query scoping uses `TopFiles` (default 10) as the seed set, too narrow for reranking or fusion. Split
`CandidateTopK` (50 to 200, what BM25 returns for rerank/fusion to operate on), `SeedTopK`
(budget-dependent, the files expanded from), and `EmitTopK` (the packer decides). Items 2 and 9 need a
wider candidate pool.

---

## 6. Phase 2: default-path retrieval

### Q1: Query-path single-pass and parallel indexing

`FilterByQueryAsync` builds documents in a sequential `foreach`, awaiting `GetContentAsync` and running
`DependencyGraphBuilder.Analyze` per file, then `BuildGraphAsync` analyzes the same files again. Build
the graph first and derive declared symbols from it (analyze once), and parallelize the document loop
like the reduction and graph stages. Warm MCP path; prerequisite to item 1, which adds another source
read. Subsumes the latency half of item 24.

### Item 4: Budget-aware expansion

`ExpansionOptions` in `FilterByQueryAsync` (line ~600) is built WITHOUT `TokenBudget`, so the graph can
admit a large neighbourhood, every file is fully reduced, and the packer cuts to budget afterward: CPU
wasted reducing files that never emit, and recall lost when truth files lose the knapsack. Pass
`TokenBudget` into expansion using tiered per-file cost estimates (full reduced, thin skeleton,
signature skeleton) from the `TokenCostModel`, not raw bytes. Scale `SeedTopK` and depth to the budget
so tight budgets are not over-seeded (recovers the 10k/25k dip). Optional follow-on: defer reduction
for low-priority neighbours until the packer admits them (lazy reduce on pack). Validate across all
three budgets.

### Item 1: Tiered emission

Emit non-seed neighbour files as skeletons (signatures only) so each costs fewer tokens and the packer
fits more files under a fixed budget. Recall counts file presence, so fitting more truth files raises
recall. Implement through per-file reduction levels (A1 plus threading a `ReductionLevel` per file into
`ContentReductionPipeline.ReduceAsync` and into the `ReductionHasher.HashReductionOptions` cache key),
NOT by re-reading source in the orchestrator, so it stays single-pass and redaction-correct (C1).
Seeds reduce at Standard or Aggressive, selected-member seed files at ThinSkeleton, neighbours at
Skeleton, huge low-score files at Sketch (item 16). Apply only to query and focus, not changes. Gate
with a flag during A/B. Tests: a query run keeps seeds full and emits a neighbour as signatures only;
golden-output tests will shift, update deliberately. Validate on layer2a and layer4; watch precision
and the 10k/25k rows.

### Item 2: Multi-query fusion

Run a few query variants (raw query, identifier-only subset, the PRF-expanded weighted query, and a
path/filename variant) and combine with reciprocal rank fusion: RRF score of a path is the sum over
rankings of `1 / (k + rank)`, k around 60. New `Scoping/RankFusion.cs` with unit tests. This can subsume
the current single-pass plus PRF merge. Keep to 3 to 5 high-signal variants; too many dilute precision.

### Q2: Fielded comments and explicit field weights

`FilterByQueryAsync` indexes raw content, so comments and `///` docs are in the body field by accident;
they carry the natural-language vocabulary prose queries match. Promote comments and doc-comments to
their own weighted field, and make the field set and weights explicit in `Bm25RelevanceIndex`: filename
and declared type very high, member name high, namespace and path medium, referenced type medium, body
normal, comments lower but useful. Re-validate field weights as a scorer change.

### Q3: Exact symbol/path boosts

When a query token looks like an identifier, type name, filename, or path segment, boost files that
DECLARE that exact symbol or match that path far above files that merely mention the words. Distinguish
"contains the words" from "declares the type".

### Item 3: Identifier-aware tokenization upgrade

Extend `Scoping/RelevanceTokenizer.cs` (`IdentifierSplitter`, `Stem`). Handle digit boundaries and
all-caps acronyms, and keep both the whole token and subwords. Test cases: `HTTPClientFactory`,
`IPAddress`, `OAuth2Token`, `Json.NET`, `snake_case`, `kebab-case`, `XMLReader`. Keep both stemmed and
unstemmed forms for identifiers, since stemming can wrongly equate `Option`, `Options`, and `Optional`
in code. Same tokenizer for index and query.

### Q4: Local distributional thesaurus

PRF only harvests terms from the top-K feedback set. Build a corpus-global alternative: mine identifier
co-occurrence (PMI between identifiers in the same file or symbol chunk) offline, cache the association
table in `.fuse/fuse.db`, and expand a query with its top statistically-associated identifiers. This
bridges a query term to a related-but-different vocabulary the feedback docs never contained, fully
lexically, on the default path. Prototype before the opt-in dense stack: if it buys a few points it
does so with no model and no isolation-assembly cost.

### Q5: Member-level retrieval

BM25 ranks at file granularity; member level is used only for emission. Middle path: index symbol
chunks as separate fielded documents and roll up to a file score (max or weighted sum), or two-stage
retrieve (top files by BM25, then re-rank by best member match). Helps when the query names a method
but the file body is dominated by unrelated members.

### Q6: Git churn / recency prior (with a benchmark-leakage guard)

Recently and frequently changed files correlate with where work happens; blend a churn and recency
multiplier into the rank score (stats are collected via `IGitStatsProvider`), as an additive prior
alongside centrality, behind a flag with a conservative weight. Cache churn per content hash in
`.fuse/fuse.db` (a naive `git log` per file is slow).

Benchmark-leakage guard (important): Layer 2A and 4 reconstruct the PR HEAD, where the truth files are
by construction the most recently changed, so a recency signal computed at HEAD ranks the answer set
high for the wrong reason and inflates measured recall. Compute churn over a window that EXCLUDES the
commits introduced by the PR under test (or evaluate at `pr.base`), and report churn-on vs churn-off
A/B with the caveat front and center. Treat any large jump as suspect until leakage is ruled out. This
lever is for production routing quality, not for chasing the benchmark.

### Item 7: Graph edge weights, kinds, and soft edges

Weight edges by reference kind (base type and interface and constructor parameter are stronger than an
incidental local variable type or a generic argument) and frequency; have `FocusSeedResolver.Expand`
multiply the per-hop decay by edge weight. Add low-weight structural proximity edges: same directory,
sibling file with the same stem, `.Tests` counterpart, same `.csproj`, same namespace prefix. These
help when type references are incomplete. Affects all graph modes; validate broadly. Prefer this over
scalar tuning (item 5).

### Q7: PageRank and personalized-PageRank

Replace the in-degree centrality with a few PageRank iterations (cheap array multiplication; an
interface used transitively by many files inherits weight), bounded in impact by the small centrality
weight. The higher-value variant is personalized PageRank from the query seeds as an alternative to
fixed-depth BFS in `FocusSeedResolver.Expand`: it diffuses relevance from the seeds and weights
closeness. Spike PPR-from-seeds against fixed-depth on layer2a.

### Item 6: False-edge fixes

Conditional compilation and deeply nested types still break or pollute the Newtonsoft.Json syntax
graph. Fix in the Roslyn dependency and type-name extractors under `...CSharp.Roslyn`; emit fully
qualified names plus simple aliases and prefer same-namespace resolution. Validate on Newtonsoft
changes and focus.

### Item 5: Scalar admission tuning

Sweep `HopDecay`, `CentralityWeight`, `query-top`, PRF `ExpansionWeight`. Do NOT schedule until B2
(larger corpus) and B5 (held-out split) exist; tuning on 24 PRs overfits and can regress the per-repo
table. Report only on the held-out test split.

### Item 24: Persistent relevance stats

Postings tokenization is already cached in SQLite, but the per-run BM25 index rebuilds document
frequency and length stats every call. Persist those keyed by content hash, or keep a repo-scoped
in-memory index cache keyed by source dir plus collection-options hash plus file hashes, invalidated on
watch, for a large warm-query latency win.

---

## 7. Phase 3: packing and emission

### P1: Tiered, role-aware knapsack

Teach `ReductionAwarePacker` to choose `(file, tier)` pairs, and at the finest granularity
`(chunk, tier)`, in one optimization pass with real per-tier and per-chunk costs from the
`TokenCostModel`. Role constraints: always keep changed files in change mode, keep seeds unless
impossible, prefer selected-member bodies over neighbour bodies, keep at least one file per
high-confidence provenance branch. Downgrade-before-drop: for a large relevant file try a cheaper
representation (aggressive, thin, skeleton, sketch) before dropping it entirely. This is the clean
long-term shape of item 1 and is likely more valuable than micro-tuning BM25 weights. Needs A1 and A3.

### Item 16: Deterministic file sketches

For a file too large to fit even reduced, emit a non-LLM sketch: namespace, type declarations, public
members, top doc comments, route attributes, DI registrations, config keys, TODO and error strings.
Gives presence and navigation. Pull earlier than its nominal priority if benchmarks show single-giant-
file pack-outs; the current corpus failure mode is mostly multi-file truncation, so it is below items
1, 4, 13 otherwise.

### Item 30: Agentic next-best-action breadcrumbs

Inject machine-readable hints into the output driven by the `ContextPlan`: if a file was skeletonized
for budget, append a hint that the body was omitted and how to expand it (call `fuse_focus "Type"`); if
a file has an interface but no implementation in the payload, append a hint to call `fuse_search`. This
turns the budget wall from a silent loss into a navigable next step and makes the no-silent-caps
honesty principle real at the product level. Cheap, deterministic, high agent value. Pull forward with
item 1 (tiered emission is exactly when "body omitted" hints become necessary). Pays off under the
interactive (B1, round-trip) metric, not one-shot recall.

### P2: Structural-map prepend target

`ApplyStructuralMapsAsync` prepends route and project maps to the LAST generated path; for multipart
output they should go in the FIRST part. Small fix.

---

## 8. Phase 4: MCP, routing, and the interactive loop

The biggest real-world gap is not "make `--query` smarter", it is "make the agent entry point pick the
right mode and measure that path honestly". Layer 4's Fuse arm is always CLI `--query` with the PR
title, the worst realistic MCP path, so the published 53 percent understates a well-routed agent and
overstates what `fuse_ask` delivers today.

### Item 13: Auto-mode routing

Extend `AskStrategySelector` (and `fuse_ask` in `Mcp/FuseTools.cs`) with a `Changes` mode. Heuristic:
(1) if `git diff` against the base yields changed `.cs` files, route to Changes mode (88 percent
recall); (2) else if the prompt contains PascalCase tokens that resolve in the analysis index to known
types or files, route to Focus; (3) else Query. Add a low-confidence retry: if the chosen strategy
returns few or no files, fall back to the next. Wire the same intent into `fuse_dotnet` and CLI
defaults (many agents pass a `query` param, not `fuse_ask`) and into the MCP tool descriptions
(branch/PR/fix work to `fuse_changes`; explore a type to `fuse_focus`; broad survey to `fuse_toc` then
a scoped fetch). Routing agents with git context to change mode beats pushing query from 51 to 55.

### Item 31: CLI `ask` command

`fuse_ask` is MCP-only. Add `fuse ask --task "..." --max-tokens N --directory ...` calling the same
selector and orchestrator path, so the harness and CI can measure the real agent surface without an MCP
client. Then wire a `fuse-ask` arm into Layer 3 and 4.

### Item 32: Change-anchored / hybrid change+query

When a git base exists, seed BM25 with the changed-file set at a boosted prior, then expand along the
graph: keep change mode's recall on the moved files and let lexical and graph pull in the unchanged
interfaces, callers, and tests a diff never shows. This attacks change mode's 47 percent precision
without giving up recall, reusing `FocusSeedResolver.Expand` with changed paths as seed scores. Cheap
to spike on the 24 PRs (you have `pr.base` and `pr.title`); report separately from pure changes and
pure query before merging into routing.

### Item 14: Session-delta improvements with diff overlays

Extend `PostReductionEnrichmentPipeline.ApplySessionDelta` and the session tracker to omit more
cross-turn material. When a file already sent has changed, emit a unified diff of the change rather than
the whole file. The tracker stores an `xxHash` per path today; to diff it must also retain the prior
content (or a compressed form), so cap that memory and fall back to whole-file on eviction, and offer
whole-file on request (an agent about to edit may want full context). High value for long multi-turn
MCP sessions.

### Item 33: `fuse_find` and `fuse_explain`

`fuse_find(path, symbolOrText, mode=symbol|text|path)`: a cheap Fuse-native exact lookup (symbol
definition via the type locator, filename/path match, or exact text with context lines), so agents
have one coherent interface instead of broad grep. `fuse_explain(path, mode, maxTokens)`: return ranked
seeds, included neighbours, scores, provenance chains, planned tier, estimated token cost, and which
high-rank files were omitted and why. Explainability makes the tools easier for agents to use well and
falls out of the `ContextPlan`.

### Layer 4 arms (measure the real agent path)

Add arms beyond the current `fuse-query`: `fuse-changes` (`--changed-since pr.base`), `fuse-ask` (once
item 31 exists), and `fuse-guided` (`fuse_toc` then `fuse_search`, two calls). Make the headline the
routed arm (ask, or changes when a base exists); keep `fuse-query` as a labeled stress floor ("only a
sentence, picked search"). Add a tokens-to-target-recall metric (tokens to reach, say, 80 percent
recall): query may need 50k for 51 percent while changes hits 88 percent at 25k. If the headline moves
to change mode, the story strengthens from "Fuse beats Repomix on tokens at lower recall" to "with git,
Fuse matches the task files at a fraction of Repomix tokens".

---

## 9. Phase 5: opt-in heavy levers

Default build stays byte-identical with any optional model or assembly absent. Register in DI only when
present.

### Item 9: Dense rerank on BM25 top-K (MCP default if it pays off, CLI prompted)

Rerank the candidate pool (A4) with a small code embedder, blending normalized BM25 with cosine
similarity. Literature reports recall in the low 70s percent at small K; the most plausible route to 60
to 68 percent on the hard repos because embeddings cross the vocabulary wall. This is the lever most
likely to make the MCP path materially better, and MCP users run on resourceful machines, so the policy
is: prototype it, measure it on AutoMapper and Newtonsoft, and if it pays off (clears the bar, for
example 60 percent-plus on the hard repos with acceptable latency), make it the DEFAULT for the MCP
server rather than an opt-in flag.

Implementation:
- `IReranker` (or `IEmbeddingModel`) capability, ONNX Runtime (`Microsoft.ML.OnnxRuntime`) with a small
  quantized code embedder (for example all-MiniLM-L6-v2, roughly 20 to 90 MB) in-process, no Python and
  no HTTP at query time. The removed hybrid stack was exactly an ONNX path, so this is a
  re-introduction; the implementation may live in the main package now (breaking changes are fine),
  but the lexical path must still run with no model present.
- Embed the query and the BM25 top-K file representations (declared symbols, path, a content sketch);
  re-order seeds by the blended score. Cache vectors by content hash in `.fuse/fuse.db` (item 23).
- Model management. Download-on-demand to a cache under `FUSE_USER_DATA` (default `~/.fuse/models`) with
  an integrity check, plus a `fuse models download|status|remove` command.
  - MCP server: ensure the model at serve start or first rerank call (background fetch acceptable since
    the server is long-lived); default-on once it pays off. First call may be slower while the model
    loads; subsequent calls are warm.
  - CLI: never silently download mid-run. On first use of a model-backed feature, prompt with the size
    and wait for confirmation, then download and proceed. In non-interactive runs (CI, piped), fail with
    a clear message that names the `fuse models download` command, or require an explicit opt-in flag.
  - Absent or declined model, or offline: degrade gracefully to the lexical BM25F path; never error out
    of scoping because a model is missing.
- Benchmarks must report two arms honestly: rerank-on (the MCP default, model present) and rerank-off
  (the lexical floor, the CLI cold-start and offline path). Do not let the MCP headline hide the
  no-model number.

### Item 8: Reference graph beyond syntax

First cut, cheap: parse `.csproj` `<ProjectReference>` and `<PackageReference>` for a coarse
inter-project assembly graph and keep deep syntax expansion intra-project. Fuse already has
project-graph machinery (`IProjectGraphGenerator`, `--project-graph`); reuse it. This is most of the
cross-assembly value at a fraction of the compile cost and avoids MSBuild on the hot path. Later tier:
a real Roslyn `Compilation` with metadata references, lazy and cached in `.fuse/fuse.db` keyed by
content hash plus analyzer tier, opt-in behind a flag and the persistent index. Validate on AutoMapper
and Newtonsoft.

### Items 10, 11: Learned sparse and cross-encoder

Larger opt-in rewrites, same isolation rules as item 9. Learned sparse (SPLADE-style) keeps an
inverted-index runtime with learned term weights; cross-encoder gives the highest-precision top-K
reorder at a per-query latency cost. Pursue only after item 9 proves the dense signal pays off on the
hard repos.

### Item 23: Persistent vector cache

When item 9 is on, cache vectors by content hash in `.fuse/fuse.db` so warm runs skip re-embedding.

### Item 12: Query rewrite / HyDE

A rules-based query expansion (extract PascalCase tokens, `Controller` to `*Controller` path boost,
test-name suffixes) is cheap and default-safe. An LLM-backed rewrite of a prose task into identifiers
and type hints helps prose prompts but adds policy and cost: opt-in only.

### Item F1: SQLite FTS5 BM25 backend (only if profiling demands it)

Option to replace the per-run in-memory index with an FTS5 virtual table for persistent, low-memory,
incremental querying (would subsume item 24). FTS5 `bm25()` supports per-column weights, so BM25F-style
weighting is expressible, but its `unicode61` tokenizer does not do camelCase splitting, code
stopwords, or the custom stemmer, and a managed custom tokenizer is not available through
`Microsoft.Data.Sqlite`. The workable design is to pre-tokenize with `RelevanceTokenizer` and store the
token stream as the FTS column. This changes the scorer, so every benchmark number shifts: treat it as
a scorer change and re-validate with no per-repo regression. Do the lighter item 24 first; reach for
FTS5 only if profiling shows the in-memory index is the real bottleneck at scale.

---

## 10. Better and more honest benchmarking

The current headline (recall at a token budget over 24 PRs) is useful but conflates ranking with
packing and is noisy on a small, partly-adversarial corpus. These live under
`tests/benchmarks/harness` and write to `tests/benchmarks/results`. Never fabricate; regenerate and
sync the docs.

- **B1 Task-success eval (gold standard).** Per task (a real issue with a known patch), run a
  programmatic agent across arms (no-fuse, fuse query, fuse changes when a base exists, Repomix). Score
  localization success (correct file identified) and optionally fix success (patch passes tests).
  Record input tokens and round-trips to first correct localization. Output
  `results/layer5-task.{json,md}`. Start localization-only with deterministic file-equality scoring;
  fix success needs flaky-test infrastructure and dominates cost. This becomes the eventual headline.
- **B2 Larger, cleaner corpus.** Grow `prs.json` to 80 to 150 PRs across more repos and at least one
  more language. Record a change-set-size stratum per PR. Tag adversarial or merge-noise titles. Keep
  commit-pinned (`gen-prs.ps1`, `setup-corpus.ps1`).
- **B3 Ranking metrics.** In `layer2a.ps1` or `common.ps1`, emit recall@k for k in {1,3,5,10,20}, MRR,
  and nDCG@k alongside recall@budget. They isolate retrieval quality from packing.
- **B4 Decouple recall@k from recall@budget.** Emit both: recall@k on the ranked seed list before the
  budget cut, recall@budget after emission. Tells you whether to invest in ranking (items 2, 3, 9) or
  packing (items 1, 16, P1).
- **B5 Held-out dev/test split.** Split by repo or a fixed hash of the PR id, recorded as a `split`
  field in `prs.json`. Tune only on dev; publish only test-set numbers. Required before any scalar
  tuning (item 5).
- **B6 Bootstrap confidence intervals.** Resample per-PR recalls (about 1000 times) and emit a 95
  percent interval per mean. A change is a win only if the interval clears the baseline.
- **B7 Adversarial-case reporting.** Using B2 tags, report the mean both including and excluding
  adversarial or merge-noise titles, labeled. Never drop them silently.
- **B8 Precision and wasted tokens.** Emit precision and wasted tokens (tokens spent on emitted files
  not in the truth set) at the headline budget. A recall gain that triples wasted tokens is not a clean
  win.
- **B9 Per-repo CI gate.** A lightweight query-only 2A gate (like `spike-query-expansion.ps1`) plus the
  2B gate, failing if any repo regresses beyond tolerance. Extend the `Compare-Results` helper in
  `common.ps1`.
- **B10 Stratify by change-set size.** Report recall per stratum (small 1 to 3, medium, large 10-plus)
  on the existing 24 PRs now; the budget wall mostly hits the large stratum and the mean hides it.
- **B11 Cost-adjusted recall.** One number that punishes "emit the whole repo as skeleton": recall over
  wasted tokens, or recall times precision-at-budget.
- **B12 Title-only vs title-plus-body.** Run the same PRs with just the title and with title plus body,
  to separate the vocabulary wall from "the agent had more text" without a full agent eval.
- **B13 Latency layer.** Measure wall-clock p50/p95 of a scoped call and cold-vs-warm cache, with
  reduction-time columns. For an MCP tool an agent waits on, latency is a product metric, and Q1's
  double-analysis would show up here. Stand this up before the allocation work in P1/perf.
- **Reading-set ground truth.** The corpus truth is "files each PR changed", which under-counts files
  an agent must read but not edit (interfaces, callers, tests) and over-counts trivial edits. Label a
  per-PR reading set distinct from the editing set so retrieval is scored against what is genuinely
  needed. Part of the MediatR-94 vs AutoMapper-29 gap is label quality, not tool quality.
- **Experimental flags recorded in results.** Fold experimental flags into a typed experimental-options
  record threaded through `FusionRequest` (env as override only) and record the resolved options into
  the result JSON, so every committed number is self-describing and reproducible. Ambient env-var flags
  make a result depend on invisible state, which is a direct honesty risk.

Honest-reporting checklist: always pair a recall number with its token cost and round-trip count;
regenerate results and sync `benchmarks.mdx` and `AGENTS.md`; never hand-edit a number without a
regenerated result behind it; label any LLM-in-the-loop or theory-grounded figure as illustrative;
report per-repo, never just the mean.

---

## 11. Honest ceiling

- Default fast path, no model: realistically mid-to-high 50s percent query recall at 50k with budget-
  aware expansion, tiered emission, fielded comments, fusion, and tokenizer work combined. Reaching
  60-plus on the default path alone is unlikely; the vocabulary wall is real.
- With dense rerank (item 9) as the MCP default: 60 to 68 percent-plus is plausible per the literature,
  concentrated on the hard repos. This becomes the MCP query headline, with the lexical number as the
  CLI cold-start and offline floor. Combined with the reference graph (item 8) it could go further.
- Product framing: when a git base exists, change mode already reaches 88 percent. Query mode is the
  cold-start fallback and the hardest case; routing tasks to the right mode (item 13), supporting an
  interactive loop (items 14, 30, 33), and the MCP-default reranker (item 9) together buy more
  real-world value than squeezing the lexical query path alone.

---

## 12. Execution order

1. Phase 0 correctness and safety: C1 (redaction after rewrite, also fixes shipped paths and unblocks
   item 1), C2 (strict token accounting), C3 and C4 (verify and fix concurrency hazards), C5 (symbol
   identity), C6 (redaction patterns). Plus experimental-flags-in-results.
2. Cheap benchmark diagnostics, in parallel with phase 0: B3, B4, B8, B10, B11, and the B13 latency
   layer, so any later move is trustworthy and precision/cost are visible before celebrating a recall
   bump.
3. Architecture enablers: A1 (`ContextPlan`), A2 (`QueryScopingPipeline`), A3 (`TokenCostModel`), A4
   (candidate/seed/emit). Then Q1 (single-pass parallel) and item 4 (budget-aware expansion).
4. Routing and the agent surface (cheapest large real-world gain): item 13, item 31 (CLI ask), the
   Layer 4 arms, and item 32 (change+query spike). Add item 30 (breadcrumbs) and the item 33 tools.
5. Item 1 (tiered emission) through per-file reduction levels with the `ContextPlan` backbone, then P1
   (tiered role-aware knapsack) and item 16 (sketches).
6. Default-path vocabulary work: Q2 (fielded comments and weights), Q3 (exact boosts), item 3
   (tokenizer), then Q4 (distributional thesaurus) and item 2 (multi-query fusion). Q5 (member-level)
   as a follow-on.
7. B2 and B5 (larger corpus, held-out split). Only then item 5 (scalar tuning), item 7 (edge weights
   and soft edges), Q6 (git churn, with the leakage guard), and Q7 (PageRank / PPR), all A/B'd on the
   held-out split. Item 6 (false-edge fixes) alongside.
8. Item 24 / persistent relevance stats. Then opt-in: item 9 (ONNX dense rerank) only after the default
   path plateaus; item 8 (`.csproj` coarse graph first, compiled tier later) if the hard repos still
   trail. Items 10, 11, 23, 12, and F1 as warranted by data.
9. B1 task-success with round-trips as the eventual headline, retiring single-call recall@budget as the
   primary target.

---

## 13. What is already shipped on this branch

Three scoping commits plus research-notes commits landed:
- Pseudo-relevance feedback query expansion (default, lexical): Layer 2A query at 50k 49 to 51 percent,
  Layer 4 52 to 53 percent, no per-repo regression. Files: `Scoping/PseudoRelevanceExpander.cs`,
  `Scoping/QueryExpansionOptions.cs`, weighted `RankScored` and `InverseDocumentFrequency` on the
  index, wired in `FusionOrchestrator.FilterByQueryAsync`. Toggle `FUSE_QUERY_EXPANSION=0`. Unit tests
  in `tests/Fuse.Fusion.Tests/Scoping/PseudoRelevanceExpanderTests.cs` cover term selection, the IDF
  gate, the multi-document-frequency guard, and the seed-preserving merge.
- Reverse-hop expansion for query was evaluated and rejected (it dropped recall 51 to 45 percent;
  Newtonsoft.Json 30 to 13) and is documented in the CHANGELOG research notes and an orchestrator
  comment. Do not re-add without a confidence-scored or seed-restricted variant (see item 32 for a
  better hybrid).
- Per-repo recall table added to `layer2a.md`.
- `CHANGELOG.md` `[Unreleased]` has the research notes and the scoped tiered-emission plan.

A throwaway A/B helper exists at `tests/benchmarks/harness/spike-query-expansion.ps1` (toggles
`FUSE_QUERY_EXPANSION`, query mode only, ~5 min) and is the fastest way to sanity-check a query-path
change before a full layer run.

---

## 14. Implementation status report

A verified accounting of this plan against the code on `feature/roslyn-mandatory-sqlite-cache`, checked
by inspecting the source, tests, harness, and `CHANGELOG.md` (not by assertion). Roughly 50 of about 60
items are implemented; the rest are runtime-blocked, gated by the plan's own conditions, or genuinely
not yet started. Every implemented retrieval or packing change was measured from the harness, kept
opt-in when it did not clear its bar, and recorded in `tests/benchmarks/results/opt-in-levers.md` and
the `benchmarks.mdx` Findings; the default-path numbers (layer 2A/2B/4) are unchanged by the opt-in
levers and the B9 per-repo gate passes.

### Implemented

- **Phase 0, correctness and safety (C1-C6): all done.** C1 redaction re-run after post-reduction
  rewrites (`RewriteRedacted` on every slice, thin-skeleton, sketch, and downgrade path); C2 strict
  total-token accounting; C3 DI lifetime and concurrency under MCP; C4 SQLite pending-write race, plus
  the corrupt-database recovery race fixed here (private cache and missing-table self-heal); C5 stable
  symbol identity (`SymbolChunk.Identity`); C6 whole-PEM-block and cloud-key redaction.
- **Phase 1, architecture enablers (A1-A4): all done.** A1 first-class `ContextPlan` (role and tier per
  file, replacing provenance-length inference); A2 extracted `QueryScopingPipeline`; A3 `TokenCostModel`;
  A4 separated candidate, seed, and emit counts.
- **Phase 2, default-path retrieval (most):** item 4 budget-aware expansion; item 1 tiered emission;
  item 2 multi-query fusion; item 3 identifier-aware tokenization; Q3 exact symbol and path boosts; Q4
  distributional thesaurus (opt-in); Q5 member-level retrieval (opt-in, measured query 61 to 68 at 50k);
  Q6 git churn prior (opt-in, leak-safe, a no-op on the pinned benchmark by construction); Q7 PageRank
  centrality (real power iteration, not in-degree); item 6 false-edge fixes; item 7 proximity edges
  (opt-in, measured neutral); item 24 process-lifetime relevance index cache.
- **Phase 3, packing and emission: done.** P1 downgrade-before-drop and role-aware packing (the largest
  focus and tight-budget lift); item 16 deterministic huge-file sketches (opt-in); item 30 agentic
  breadcrumbs; P2 structural map prepended to the first part.
- **Phase 4, MCP and the interactive loop: all done.** item 13 auto-mode routing (`AskStrategySelector`);
  item 31 CLI `ask` command; item 32 change-plus-query hybrid (spiked); item 14 session-delta diff
  overlays; item 33 `fuse_find` and `fuse_explain` tools.
- **Phase 5, opt-in heavy levers (the measurable ones):** item 9 dense bi-encoder rerank (measured,
  below the 60 percent bar, opt-in); item 11 cross-encoder rerank (measured 33 percent on hard repos,
  opt-in); item 8 coarse `.csproj` project-reference graph (measured query 61 to 62, opt-in); item 23
  rerank embedding cache.
- **Benchmarking:** B3 ranking metrics (recall@k, MRR, nDCG, `layer-ranking.ps1`); B4 recall@k versus
  recall@budget; B5 held-out dev/test split; B6 bootstrap confidence intervals; B8 precision and wasted
  tokens; B9 per-repo CI regression gate (`check-regressions.ps1`); B10 change-set-size strata; B11
  cost-adjusted recall; B12 title-only versus title-plus-body; B13 latency layer; experimental flags
  recorded in results.

### Not implemented

Runtime- or data-blocked (cannot be completed in the current environment):

- **B1, task-success eval with round-trips:** needs a programmatic agent runner that drives the arms and
  scores patches. No such runtime is present.
- **item 12, query rewrite / HyDE:** the rules-based, no-model half is now built and measured (an opt-in
  PascalCase-emphasis query rewrite, `FUSE_QUERY_REWRITE`; neutral on the corpus, kept opt-in with a research
  note). The LLM-backed half needs a language model at query time; the hard invariant bars a model and network
  from the default path, so it stays opt-in and its runtime is absent here.
- **B2, larger and cleaner corpus:** the code and harness for B2 are already complete: `gen-prs.ps1`
  reproducibly curates per-PR ground truth (base/head/merge SHAs and changed `.cs` files) from a pinned clone's
  merge history, `setup-corpus.ps1` clones each repo at its pinned commit, the layer harnesses iterate
  `prs.json` over whatever repos it lists, and Layer 2A already scores recall against an optional reading set.
  What remains is purely (a) the data the plan calls for, 80 to 150 PRs across more repositories and at least
  one more language (one fifth repository, Serilog with six git-verified PRs, is staged in
  `tests/benchmarks/corpus-candidates/serilog.json`), and (b) the atomic rebaseline of every published number.
  A trial wiring of just Serilog moved every headline mean (query 61 to 64, focus 92 to 90, changes 87 to 89,
  grep 38 to 37) and the benchmarks page carries about fifteen per-feature A/B deltas each measured over the
  pinned 24-PR corpus, so a faithful B2 re-runs every layer and every feature spike and rewrites all coupled
  prose in one consistent pass. Doing that hurriedly or partially would leave the page's prose contradicting its
  tables, which is exactly the "never weaken a benchmark number" invariant; so the rebaseline is deliberate
  work, not the tooling. The tooling to perform it is in place.

Gated by the plan's own conditions:

- **item 5, scalar admission tuning:** the plan forbids tuning until B2 and B5 exist (it overfits the 24
  PRs otherwise). Blocked on B2.
- **F1, SQLite FTS5 backend:** the plan says "only if profiling demands it"; the B13 latency layer shows
  the in-memory index is not the bottleneck, so it is not warranted.
- **item 10, learned-sparse (SPLADE):** an XL opt-in rewrite the plan gates on dense rerank (item 9)
  paying off, which it did not on this corpus.

Completed since this report was first written (all measured, gated, or behavior-neutral as noted):

- **Q1, single-pass parallel document indexing:** the query pipeline now reads content, derives symbols, and
  extracts the comment field in parallel, folding results into the index in stable file order so the built
  index and its cache key are byte-identical (a latency optimization). All scoping tests unchanged; B9 passes.
- **Q2, fielded comments and doc-comments:** built as an opt-in BM25F comment field (`FUSE_FIELDED_COMMENTS`),
  measured neutral on the corpus (no headline gain), so kept off by default with a research note.
- **B7, adversarial-case reporting:** Layer 2A now reports query recall all-PRs, adversarial-only, and
  excluding the merge-noise titles (61, 100, 57 percent), so the adversarial cases cannot silently inflate.

Partly done (infrastructure in place; labels are deliberate curation):

- **Reading-set ground truth:** the Layer 2A harness now scores recall against an optional per-PR
  `reading_set` (the files an agent must read but not edit) unioned with the editing set, falling back to the
  editing set when no label exists, so the current corpus is unchanged. The remaining work is the labels
  themselves: hand-curating per-PR reading sets is a benchmark-honesty task that shifts the published numbers,
  so it pairs with the B2 rebaseline and is done deliberately, not fabricated at the tail of a session (bad
  labels would weaken the benchmark, the opposite of the item's intent).

### Suggested next order for the remaining unblocked work

The reading-set labeling is the last unblocked default-path item; it pairs with the B2 rebaseline (both are
benchmark-honesty curation, best done together in a fresh session). The remaining items beyond it are the
runtime-blocked (B1, item 12) and gated (item 5, F1, item 10) ones above, plus the B2 rebaseline.
