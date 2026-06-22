# Fuse Improvement Roadmap - Remaining Work

A self-contained implementation plan for the work that remains after the first wave of the v2 roadmap
landed. It is written for a fresh Claude Code session: read this top to bottom before editing. It was
validated against the codebase on branch `feature/v2-roadmap` (the branch that carries the completed
items). Every "Problem" below was confirmed against current source; file paths are inlined.

Backward compatibility and breaking changes are explicitly out of scope; prefer the cleanest design over
preservation of existing surface. The only hard constraint that remains is the `PublishAot` build path:
the regex-based C# tier and the hashing-embeddings fallback must keep working without Roslyn or ONNX.

## What is already done (do not redo)

Branch `feature/v2-roadmap`, draft PR #12 against `main`. Eleven items landed, one commit each, with
build, `dotnet test` (all projects), and `dotnet format` green at every commit, and benchmark layer 1
refreshed over the corpus with a committed baseline. Completed:

- 0.1 `ApiSurfaceAnalyzerFactory` tests.
- 0.2 Shared `CSharpStringScanner` (`src/Plugins/Fuse.Plugins.Languages.CSharp/Lexing/CSharpStringScanner.cs`);
  `CSharpReducer` masks every string and char literal (including raw strings) behind `__FUSE_STR_n__`
  placeholders before any transform, then restores them.
- 0.3 Connection-string redaction requires a quoted literal with at least two `key=value` pairs and a
  connection keyword (`DefaultSecretRedactor`).
- 0.5 `ReductionLevel` enum (`None | Standard | Aggressive | Skeleton | PublicApi`) replaces the C#
  reduction boolean cluster. See "Conventions changed by the completed work" below; this affects how all
  new reduction code is written.
- 1 Per-run state refactor: `FusionOrchestrator` holds no process-wide gate; the content provider and the
  BM25 index are constructed per run via injected factories. See conventions below.
- 1b Benchmark harness extension: `BodyIntegrity` tool, a `--compare` gate, multi-budget recall@budget in
  layer 2a, an illustrative round-trip model (`layer3.ps1`), and a SampleShop smoke (`smoke.ps1`).
  Baseline committed at `tests/benchmarks/results/baseline.layer1.json`.
- 2.3 Graph-centrality ranking prior (`GraphCentrality`, `FUSE_CENTRALITY_WEIGHT`, default 0.15).
- 3.2 Persist BM25 body tokenization by content hash (`IRelevancePostingsStore`,
  `DiskRelevancePostingsStore`); `IRelevanceIndex.Index` takes an optional postings store.
- 4.2 Redaction code-literal vs config reporting (`SecretRedactionResult.CodeLiteralRedactions`).
- 4.3 MCP behavior-parity test.
- 4.4 Scoped MCP tools default `level` to `standard`.

## Environment (this machine) - read before you start

You have internet access. Download and test real assets; do not stub what you can verify for real.

- Native AOT publish WORKS locally once `vswhere.exe` is reachable. Prepend
  `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH (or use a "Developer PowerShell for
  VS"), then from PowerShell (not Git Bash, which mangles `/p:`):
  `dotnet publish src/Host/Fuse.Cli/Fuse.Cli.csproj -c Release /p:PublishProfile=aot-win-x64`. It produces
  `artifacts/aot/win-x64/fuse.exe` (~18 MB) that reports `"backend":"regex"`. Use this to verify the AOT
  invariant for item 2.1.
- The pinned corpus is already cloned under `tests/benchmarks/.corpus/` (MediatR, FluentValidation,
  AutoMapper, NewtonsoftJson) plus the in-repo SampleShop. `run-all.ps1` and the layer scripts run offline.
- The Repomix arm shells out through `npx repomix`. If it emits a ~380-token stub (network or version
  issue), do not commit the broken Repomix rows; carry the prior committed rows (see how the layer 1
  refresh did it). The Fuse arms are the meaningful, machine-independent numbers.
- Internet uses for the remaining items: download the ONNX sentence-encoder model and runtime for 2.1;
  call the Anthropic token-counting API (with a key) and/or run the providers' tokenizers for 4.1.

## Conventions changed by the completed work (write new code to match)

- Target `net10.0`, nullable enabled, file-scoped namespaces. Add a capability by implementing the
  relevant interface and registering it in `ServiceCollectionExtensions`; do not branch on language in
  `FusionOrchestrator`.
- Reduction options. `ReductionOptions`
  (`src/Plugins/Fuse.Plugins.Abstractions/Options/ReductionOptions.cs`) now has a single
  `ReductionLevel Level` plus orthogonal flags (`TrimContent`, `UseCondensing`, `MinifyXmlFiles`,
  `MinifyHtmlAndRazor`, `IncludeSemanticMarkers`, `IncludePatternSummary`, `EnableRedaction`,
  `IncludeRedactReport`, `IncludeRouteMap`, `IncludeProjectGraph`, `CollapseGeneratedCode`). The seven C#
  transform flags (`RemoveCSharpComments`, `RemoveCSharpUsings`, `RemoveCSharpNamespaces`,
  `RemoveCSharpRegions`, `AggressiveCSharpReduction`, `SkeletonMode`, `PublicApiMode`) are read-only
  properties derived from `Level`. Construct with `new ReductionOptions(level: ReductionLevel.X, ...)`.
  `ReductionHasher.HashReductionOptions` hashes `(int)Level` plus the orthogonal flags.
- Per-run state. `FusionOrchestrator` takes `Func<ISourceContentProvider>` and `Func<IRelevanceIndex>`
  factories and constructs both per run; the per-run `contentProvider` is threaded through its private
  helpers (`FilterByFocusAsync`, `FilterByQueryAsync`, `FilterByChangesAsync`, `BuildGraphAsync`,
  `ApplySymbolSliceAsync`, `EmitTableOfContentsAsync`, `ApplyStructuralMapsAsync`). There is no `_runGate`.
  `ContentReductionPipeline.ReduceAsync(sourceFiles, options, contentProvider, [parallelism, cache,
  tokenCounterOverride], ct)` takes the run-scoped provider as a parameter.
- Relevance index. `IRelevanceIndex.Index(documents, IRelevancePostingsStore? postingsStore = null)`;
  `Bm25RelevanceIndex` caches body tokenization through the store when one is passed.
- Expansion. `FocusSeedResolver.Expand(graph, seedScores, ExpansionOptions)`; `ExpansionOptions` carries
  `Centrality` and `CentralityWeight`. The rank score is `relevance + weight*centrality`, additive and
  never propagated to neighbours (so a zero weight reproduces the prior order). `GraphCentrality.Compute`
  gives normalized in-degree.
- Build, test, format gates (also in AGENTS.md / CLAUDE.md):
  ```
  dotnet build Fuse.slnx -c Release
  dotnet test Fuse.slnx -c Release --no-build
  dotnet format Fuse.slnx --verify-no-changes
  ```
  Build first, then test with `--no-build`. CI also verifies the Native AOT publish (win-x64, linux-x64);
  verify it locally for 2.1 using the PATH fix above. New public API without XML docs is incomplete
  (see the Code Documentation Standard in AGENTS.md).
- Branch off `feature/v2-roadmap` (so you build on the completed items) unless PR #12 has merged, then
  branch off `main`. Land each item as its own commit. Do not merge, self-approve, or enable auto-merge.
- Writing style for all prose (docs, comments, PR text): plain ASCII only, no em dashes (U+2014), no en
  dashes (U+2013), no emoji. A stop hook rejects non-ASCII punctuation. Measured numbers are exact and
  sourced from `tests/benchmarks/results`; never fabricate or weaken a number, and label any illustrative
  claim as illustrative.

### Test and harness map (verified)

```
tests/Fuse.Cli.Tests, tests/Fuse.Collection.Tests, tests/Fuse.Emission.Tests, tests/Fuse.Fusion.Tests,
tests/Fuse.GoldenOutput.Tests (UPDATE_GOLDEN_FILES=1 regenerates snapshots), tests/Fuse.Reduction.Tests,
tests/Fuse.Plugins.Formats.Web.Tests, tests/Fuse.Plugins.Languages.CSharp.Tests,
tests/Fuse.Plugins.Languages.CSharp.Roslyn.Tests, tests/fixtures/SampleShop

tests/benchmarks/harness/{run-all,layer1,layer2a,layer2b,layer3,smoke,common,setup-corpus,gen-prs}.ps1
tests/benchmarks/tools/{TokenCount,Fidelity,BodyIntegrity}     # measurement exes (built by run-all.ps1)
tests/benchmarks/results/{layer1.{json,csv,md},layer2a.json,layer2b.json,layer3.json,baseline.layer1.json}
```

Docs live under `site/content/docs` (Diataxis: start, scenarios, concepts, reference, internals, project).

---

## 2.1 Real local embeddings for rerank

**Goal.** Replace the lexical `HashingEmbeddingModel`
(`src/Core/Fuse.Fusion/Retrieval/HashingEmbeddingModel.cs`) with a genuine semantic encoder, turning
rerank from marginal re-weighting into real semantic retrieval.

**Problem context (verified).** `IEmbeddingModel`, `IVectorStore`, `DiskVectorStore`, and `VectorReranker`
live under `src/Core/Fuse.Fusion/Retrieval`. Rerank is controlled by `QueryOptions.Rerank` (a boolean) and
the `--rerank` CLI flag on `DotNetCommand` plus the `rerank` MCP parameter on `fuse_search`. The embedding
model is registered once in `src/Core/Fuse.Fusion/Extensions/ServiceCollectionExtensions.cs`:
`services.AddSingleton<Retrieval.IEmbeddingModel>(_ => new Retrieval.HashingEmbeddingModel());`.
`RerankCandidates` in `FusionOrchestrator` builds candidate text and a cache key
`AnalysisHasher.Key(text, "vec:" + _embeddingModel.Dimensions)`, so vectors of different dimensionality do
not collide. `--semantic` / `FUSE_SEMANTIC` gates the Roslyn precision tier (a different concern,
`CommandBase.Semantic`, `McpServeCommand`); do NOT overload it. There is zero ONNX usage in the repo today.

**Settled decisions (keep).**
- Model distribution: download on first use, do not bundle. The ~100 MB asset would dominate package size
  for an opt-in, non-AOT-only feature, and the AOT build cannot use ONNX.
- Default: semantic rerank becomes the non-AOT default, but only after the 1b measurement confirms a
  recall@budget win and bounded cold-call latency. Land opt-in first.

**Approach.**
1. Add an ONNX-backed `IEmbeddingModel` in `src/Core/Fuse.Fusion/Retrieval` using
   `Microsoft.ML.OnnxRuntime`, loading a small sentence encoder (`bge-small-en-v1.5` or
   `all-MiniLM-L6-v2`). You have internet: download a real model, run real inference, and assert real
   cosine similarities in tests (do not mock the encoder where you can run it).
2. Download on first use into a per-machine cache under the user profile (for example
   `~/.fuse/models/<model>/`, NOT the per-repo `.fuse/`), keyed by model name and a content hash. Print a
   one-line stderr notice ("fuse: downloading embedding model <name> (~NN MB), one time"). If the model is
   absent and cannot be fetched (offline), fall back to `HashingEmbeddingModel` rather than failing.
   - Sideload: honor `FUSE_EMBEDDINGS_MODEL_PATH`; when set, load from that path and skip all network.
   - Pin the download URL and expected SHA-256 in config; verify the hash after download and refuse a
     mismatch. Make the downloader an injectable seam so tests can spy on it.
3. Keep `HashingEmbeddingModel` as the AOT and no-dependency fallback. Select via DI on a NEW dedicated
   switch `--embeddings` / `FUSE_EMBEDDINGS` (do not reuse `--semantic`). Tri-state resolution: explicit
   flag/env wins; else the build default; a forced `--embeddings false` / `FUSE_EMBEDDINGS=0` always
   selects hashing, even on non-AOT.
4. Reuse `DiskVectorStore` and `VectorReranker` unchanged; only the model behind `IEmbeddingModel.Embed`
   changes. Cache keys already vary by `Dimensions`.
5. Exclude ONNX from the AOT package the same way Roslyn is excluded
   (`Condition="'$(PublishAot)' != 'true'"` on the project/package reference plus a `BeforeCompile` target
   that defines a guard constant; see how `FUSE_ROSLYN` is wired in `src/Host/Fuse.Cli/Fuse.Cli.csproj`).
   The AOT build default stays hashing unconditionally.

**Tests** (`tests/Fuse.Fusion.Tests`).
- Ranking-stability: a repeated identical query yields identical order.
- DI tri-state: `FUSE_EMBEDDINGS` unset yields the build default; `=0` always yields hashing; `=1` yields
  ONNX when available.
- Sideload: with `FUSE_EMBEDDINGS_MODEL_PATH` set to a fixture model, it loads from that path and no
  network call is attempted (assert via the downloader spy).
- Offline fallback: ONNX requested, model absent, download disabled -> the run completes using hashing.
- Hash verification: a downloaded file whose SHA-256 mismatches the pinned value is rejected.
- Guard the real-inference test to skip when the model asset is absent (CI without the download).
- AOT path: confirm the hashing fallback is selected and the AOT publish stays clean (run the local AOT
  publish per the Environment section).

**Docs.** `reference/configuration-keys.mdx` and `reference/commands.mdx` (the `--embeddings` /
`FUSE_EMBEDDINGS` tri-state, `FUSE_EMBEDDINGS_MODEL_PATH`, cache location, pinned URL + SHA-256, AOT
fallback); `concepts/scoping.mdx` and/or `concepts/precision-tier.mdx` (semantic vs lexical rerank);
`start/install.mdx` (first semantic query downloads the model once); `project/changelog.mdx`.

**Benchmarks.** Add a rerank arm to layer 2a/2b (or a dedicated layer) comparing hashing vs ONNX on
recall@budget for natural-language tasks; record cold-call vs warm-call latency. `project/benchmarks.mdx`
currently notes a "lexical ceiling" for `--rerank`; this is the item that should move that number. Do not
publish a figure until the harness produces it.

**Acceptance.** Recall@budget on natural-language tasks improves measurably versus hashing. Cold-call
latency increase is bounded; warm calls neutral (vectors cached). Ranking-stability test passes. The AOT
build still uses the hashing fallback and the local AOT publish succeeds.

**Risk.** Medium; ONNX dependency and AOT exclusion. Mitigated by opt-in landing, intact fallback, and the
now-working local AOT verification.

---

## 2.2 Symbol-level retrieval and packing (new default)

**Goal.** Move the unit of selection from file to member. A focused question pulls relevant methods plus a
thin skeleton of their host file rather than whole files. This is the headline retrieval item and the
largest; it also unblocks 3.1 and 3.3.

**Problem context (verified).** `Bm25RelevanceIndex` (`src/Core/Fuse.Fusion/Scoping/Bm25RelevanceIndex.cs`)
indexes at FILE granularity with three fields: body, declared symbol names, path. `InclusionChain`
(`src/Core/Fuse.Reduction/Models/FusedContent.cs`) is a file-path chain only. Outline and slice extractors
exist: `RoslynOutlineExtractor` and `RoslynSymbolSliceExtractor`
(`src/Plugins/Fuse.Plugins.Languages.CSharp.Roslyn/`), the regex fallback `CSharpOutlineExtractor`
(`src/Plugins/Fuse.Plugins.Languages.CSharp/Outline/`), and the capability interfaces
`ISymbolOutlineExtractor` / `ISymbolSliceExtractor`
(`src/Plugins/Fuse.Plugins.Abstractions/Outline/`). Note: `OutlineSymbol` exposes type kind, name, and
member NAMES only, not body spans; and `ISymbolSliceExtractor.ExtractSlice(content, memberName)` slices to
one named member. A whole-file symbol slice path already exists for focus seeds of the form `Type.Member`
(`FusionOrchestrator.ApplySymbolSliceAsync`, gated on a registered `ISymbolSliceExtractor`).

**Approach.** Land in two reviewable steps.
1. Define a chunk model (for example `SymbolChunk { Path, SymbolKind, SymbolName, ParentType, Content,
   StartLine, EndLine }`) in `Fuse.Reduction` or `Fuse.Fusion`. Build chunks from `RoslynOutlineExtractor`
   (boundaries) and `RoslynSymbolSliceExtractor` (bodies); the regex fallback uses `CSharpOutlineExtractor`
   and produces a coarser but coherent chunking. You will likely need to extend the outline/slice
   capability surface to expose member body spans (the current abstraction does not); design that
   addition here, since 3.3 also needs it.
2. Extend or wrap `Bm25RelevanceIndex` to index at chunk granularity (symbol name, signature, body map onto
   the existing three fields). Keep the file-granular path working for non-C# until the chunkers exist for
   a language.
3. In `FilterByQueryAsync`, rank chunks, select to budget, then emit per file a thin host-type skeleton
   with the selected members inlined and non-selected members collapsed to signatures.
4. Extend `InclusionChain` provenance to reference a symbol, not just a file.
5. Remove the file-granular selection path for C# once chunking is the default (the regex fallback still
   produces chunks). Step (a) is the chunk model plus chunk indexing; step (b) is the emission change.

**Tests** (`tests/Fuse.Fusion.Tests`, `tests/Fuse.GoldenOutput.Tests`).
- Chunk extraction correctness: Roslyn and regex fallback produce the same boundaries for a known fixture.
- Body-integrity (the Phase 1b `BodyIntegrity` tool): selected members parse standalone.
- Provenance: `InclusionChain` references the selected symbol.
- New golden snapshots for symbol-level emission on SampleShop (regenerate with `UPDATE_GOLDEN_FILES=1`).
- AOT/regex-fallback path produces coherent (coarser) chunking when Roslyn is absent.

**Docs.** Rewrite `concepts/scoping.mdx` and `concepts/how-fuse-works.mdx` for member-level selection and
thin host skeletons; `reference/output-specification.mdx` for the new emission shape (inlined members plus
collapsed signatures); `internals/scoping-internals.mdx` for chunk indexing; `scenarios/ask-one-question.mdx`
and `scenarios/context-for-an-agent.mdx`; `project/changelog.mdx`.

**Benchmarks.** Measure precision (relevant tokens / total tokens) at fixed budget vs the file-granular
baseline using layer 2a/2b multi-budget recall (the budgets are already wired). Recall must hold or improve;
the layer-3 round-trip count must not increase. Update `project/benchmarks.mdx` and the AGENTS.md "Measured
Results" once recorded.

**Acceptance.** On focused tasks at fixed budget, precision rises versus the old file-granular baseline,
recall held or improved. Body-integrity passes. Round-trip count does not increase.

**Risk.** High complexity; the largest item. Two-step landing; measured against the committed baseline.

---

## 3.1 Reduction-aware single-pass packing

**Goal.** Make the include/exclude decision on the most accurate cost signal (real reduced token count)
rather than the byte heuristic, eliminating the dual-budget mismatch.

**Problem (verified).** `FusionOrchestrator.BuildTokenCosts` (the helper feeding `ExpansionOptions.TokenCosts`)
estimates cost as `(int)Math.Max(1, file.FileInfo.Length / 4)` (raw bytes), while `EmissionPipeline`
enforces `MaxTokens` on REDUCED content with the real tokenizer (`TokenBudget`, consuming
`tokenCounter.Count(content)`). Reduction cuts 40 to 70 percent, so expansion's byte estimate over-counts
and the run under-fills the post-reduction budget.

**Approach.** Reorder the focus/query path so candidate chunks (from 2.2) are reduced first, their real
token cost measured with the configured tokenizer, then a greedy budgeted selection (relevance-per-token,
approximating 0/1 knapsack) picks the final set ordered by `RelevanceScore`. Reuse the reduction cache
(keyed via `ReductionHasher`, opt-in via `WithReductionCache`) so reducing later-dropped candidates is
cheap on repeat runs. This is a clean rewrite of the pack path; remove the byte heuristic in
`BuildTokenCosts`. Depends on 2.2 (chunks are the cost unit).

**Tests** (`tests/Fuse.Fusion.Tests`).
- Emitted tokens land within 85 to 100 percent of `MaxTokens` across several budgets on a fixture.
- Greedy selection is deterministic and orders output by `RelevanceScore`.
- Cache: reducing a candidate that is later dropped is served from cache on a second run.

**Docs.** `internals/pipeline.mdx` and `internals/scoping-internals.mdx` (single-pass reduction-aware
packing; remove the dual-budget description); `concepts/scoping.mdx` if it mentions the byte heuristic.

**Benchmarks.** Layer 2a/2b: emitted tokens vs budget (target band 85 to 100 percent) across the corpus,
and precision improvement from spending budget on the densest chunks. Compare against the baseline.

**Acceptance.** Emitted tokens land tightly under budget (85 to 100 percent of `MaxTokens`); precision
improves; no fidelity regression.

---

## 3.3 Near-duplicate body deduplication

**Goal.** Extend `BoilerplateDeduplicator` beyond identical comment headers to near-identical member bodies
(generated or templated members, EF scaffolding beyond `GeneratedCodeCollapser`).

**Problem context (verified).** `BoilerplateDeduplicator`
(`src/Core/Fuse.Fusion/Enrichment/BoilerplateDeduplicator.cs`) only considers leading comment blocks (its
`SplitHeader` scans from the top until the first non-comment line; code is never touched).
`GeneratedCodeCollapser` (`src/Plugins/Fuse.Plugins.Languages.CSharp/Reducers/GeneratedCodeCollapser.cs`)
collapses EF-style generated method bodies only. There is no capability today that yields member body spans;
2.2 should add one, so do 3.3 after 2.2 and reuse its chunk model.

**Approach.** Add a normalized-hash bucketing pass over member bodies (from 2.2's chunks). Members whose
normalized form collides across files are emitted once with markers referencing the canonical instance,
reusing the existing header-marker mechanism in `BoilerplateDeduplicator`. Conservative first version:
require exact normalized-hash match, not fuzzy similarity. The marker must never drop a public signature.

**Tests** (`tests/Fuse.Fusion.Tests`).
- Two files with an identical normalized member body emit one canonical plus a marker; a member with a
  unique body is untouched.
- Body-integrity (the Phase 1b tool) passes for non-deduplicated members.
- `verify` API-surface preservation unchanged (the dedup marker must not drop a public signature).

**Docs.** `reference/reducers.mdx` (body-level dedup and the marker format); `reference/output-specification.mdx`
(the canonical-reference marker).

**Benchmarks.** Layer 1 token reduction on corpus repos with significant generated or templated code
(NewtonsoftJson, AutoMapper are candidates). Confirm `verify` preservation unchanged. Update
`project/benchmarks.mdx` if reduction numbers move. Note: header dedup is near-zero on this corpus, so body
dedup's corpus impact may also be modest except on generated-heavy code; report honestly.

**Acceptance.** Measurable token reduction on generated or templated repos, `verify` API-surface
preservation unchanged, body-integrity intact for non-deduplicated members.

---

## 4.1 Calibrate non-OpenAI tokenizers

**Problem (verified).** `TokenizerFactory` (`src/Core/Fuse.Emission/Tokenization/TokenizerFactory.cs`) uses
fixed constants `AnthropicCharsPerToken = 3.5` and `GeminiCharsPerToken = 4.0` via `ApproximateTokenCounter`.
OpenAI uses an exact Tiktoken encoding (`o200k_base` default, `TikTokenCounter`). The fixed constants drift
on code.

**Approach.** You have internet: calibrate the chars-per-token constants against a corpus of real C# run
through the providers' tokenizers. For Anthropic, use the Claude token-counting API (needs a key; see the
`claude-api` skill for the endpoint and current model ids) to get ground-truth token counts for a held-out
sample, then fit the constant. For Gemini, use the provider's tokenizer or count-tokens endpoint similarly.
Store the calibrated constants per family in `TokenizerFactory` with a comment citing how they were
measured. If a key is genuinely unavailable at implementation time, build the calibration harness and the
gated test now and leave a clear TODO with the measured constants to follow; do not invent numbers.

**Tests** (`tests/Fuse.Emission.Tests`).
- Predicted vs actual token counts on held-out C# stay within a tighter error band per family (gate the
  live-API portion behind the presence of a key).
- Assert the calibration constants are the committed values.

**Docs.** `reference/tokenizers.mdx` (the calibrated constants and the calibration methodology).

**Benchmarks.** Add a tokenizer-accuracy check (predicted vs actual on held-out code) to the harness or as
a unit metric. Report the error band in `reference/tokenizers.mdx`.

**Acceptance.** `MaxTokens` budgets for Claude and Gemini targets are honored within a tighter error band
(predicted vs actual on held-out code).

---

## Delivery order

1. 2.2 symbol-level retrieval and packing (two steps: chunk model plus chunk indexing, then emission). It
   is the highest-value item and unblocks 3.1 and 3.3.
2. 3.1 reduction-aware single-pass packing (on top of 2.2's chunks).
3. 3.3 near-duplicate body dedup (reusing 2.2's member-body spans).
4. 2.1 ONNX embeddings (independent; land opt-in, verify the AOT build locally, flip the default only after
   the 1b measurement).
5. 4.1 tokenizer calibration (independent; needs the provider tokenizers / a key).

Binding edges: 2.2 precedes 3.1 and 3.3. 2.1 and 4.1 are independent of the others.

## Cross-cutting deliverable checklist (per item)

- Code change with XML docs on new public API and `//` comments on non-obvious private logic.
- Tests in the matching `*.Tests` project; regenerate golden snapshots where output changes.
- Doc updates in `site/content/docs` (state explicitly if none are needed).
- Benchmark impact: re-run the relevant harness layer, refresh `tests/benchmarks/results`, and update
  `project/benchmarks.mdx` and the AGENTS.md "Measured Results" if any published number moves. Use
  `run-all.ps1 -Compare tests/benchmarks/results/baseline.layer1.json` to gate against regressions. Never
  fabricate or weaken a number; label illustrative claims as illustrative.
- Green `dotnet build`, `dotnet test --no-build`, `dotnet format --verify-no-changes`, and the AOT publish
  (verify locally with the `vswhere` PATH fix from the Environment section).
