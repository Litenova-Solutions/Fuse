# Autonomous goal: Fuse scoping, benchmarks, and research

Use this document when running long autonomous sessions (Claude Code, Cursor agent, etc.). Read `AGENTS.md` first. This goal extends the roadmap with a **research loop**: find better practices and tech, validate against Fuse constraints, then implement when the evidence supports it.

---

## Mission

Maximize Fuse scoping **recall**, **precision**, and **token efficiency** on the pinned benchmark corpus. Work autonomously until interrupted. Ship features from `site/content/docs/project/roadmap.mdx` (phases A through E), but **do not limit yourself to the roadmap** when research surfaces a clearly better approach.

**North star:** Honest benchmark lifts with tests, docs, and regenerated `tests/benchmarks/results`. Never fabricate numbers. Layer 4 always pairs tokens with recall.

---

## Research mandate (run alongside implementation)

### What to research

Continuously look for improvements in:

- **Code retrieval and RAG for code** (2024 to 2026): sparse+dense hybrid, ColBERT-style late interaction, code-specific embedders, cross-encoders for rerank, query expansion, HyDE-style query rewriting (local/heuristic only unless user approves external APIs).
- **Static analysis for .NET**: Roslyn compilation APIs, incremental generators, MSBuild workspace, reference resolution without full compile, call graph tooling.
- **Lexical search**: BM25 variants, SPLADE/learned sparse, fielded search for code (path, symbol, body), identifier-aware tokenization.
- **Graph methods**: dependency extraction, k-hop expansion with learned or heuristic stopping, personalized PageRank on code graphs.
- **Context packing**: hierarchical summarization, sketch/outline tiers, delta/session context, token-budget packing algorithms.
- **Evaluation**: recall@k, nDCG, MRR for code search; SWE-bench-style task eval; regression gates for retrieval.
- **Performance**: ONNX Runtime patterns, small embedding models, quantization, caching embeddings in SQLite, lazy compilation.

### How to research

1. **Web search** for recent papers, blog posts, OSS tools (e.g. tree-sitter graphs, ctags, Sourcegraph-style ranking, Continue/Devin context patterns, Aider repo map, Cody context, Cursor indexing ideas described in public docs).
2. **Read competitor and adjacent repos** (conceptually): what do packers, IDE indexers, and `grep`-first agents do that Fuse does not?
3. **Scan Fuse internals** before adopting: `scoping-internals.mdx`, `FusionOrchestrator`, graph builder, BM25 index, harness scripts.
4. **Record findings** briefly in `CHANGELOG.md` under `[Unreleased]` > **Research notes** (bullet per finding: source, idea, fit, decision).

### When to proceed with a research finding

Proceed with implementation when:

- It maps to a **measurable** harness metric (Layer 2A/2B/4 or Layer 1 fidelity).
- It respects **design invariants** in `AGENTS.md` (Roslyn for structural C#, no reflection JSON, optional ML not mandatory heavy bundles by default).
- You can ship a **minimal vertical slice** with tests in one iteration.
- Default path stays **fast**; expensive paths are **opt-in** (flags, env, MCP params).

Defer or spike-only when:

- Requires cloud LLM on every query (document as optional tool, not default hot path).
- Needs multi-minute compile on every cold call without cache.
- Benchmark gain is speculative with no cheap prototype.

**Spike:** time-boxed experiment branch or `tests/` proof; run `layer2a.ps1` on one repo before full integration.

---

## Baselines (verify after every harness run)

BM25-only today; re-measure after changes.

| Layer | Metric |
|-------|--------|
| 2A @50k | changes 88% / 47% (~40k tok); focus 71% / 5%; query 49% / 2% (~46k tok); grep 38% / 11% |
| 2B @20k | grep 58% (7/12); query/focus 42% (5/12) |
| 4 @50k | fuse `--query` 52% recall, ~44,632 tokens, 1 round-trip |
| Per-repo query @50k | MediatR 94%; FluentValidation 47%; AutoMapper 29%; Newtonsoft.Json 27% |

### Stretch targets

- Layer 2A query mean @50k: **60 to 68%+** recall; precision **8 to 15%+**
- Layer 4 fuse query: **58 to 65%+** recall at **<=38k** tokens (or higher recall at same budget)
- Changes: **90 to 94%** recall, **55%+** precision; Newtonsoft/AutoMapper query **40%+**
- Layer 2B query: **match or beat grep (58%)**
- Layer 1: no fidelity regression on default/aggressive

---

## Implementation order (default)

Roadmap phases A through E in `site/content/docs/project/roadmap.mdx`. **Insert research wins** when they outperform the planned item for the same metric (document why in Research notes).

**A:** query expansion, tiered emission, two-phase expansion, admission tuning, per-repo harness reports, MCP/docs for PR to `fuse_changes`.

**B:** graph edge weights, false-edge fixes, adaptive depth, selective query reverse hops, heuristic post-expansion rerank.

**C:** opt-in hybrid rerank on BM25 top-K (sideload/small model; no mandatory ~90 MB bundle), dual recall, vector cache when opt-in.

**D:** lazy compile/reference graph when solution exists, cached in `.fuse/fuse.db`.

**E:** Layer 2B CI compare, task-success eval scaffold, feature/LTR rerank if justified by research.

**Research-driven examples to evaluate (implement if better than planned):**

- Learned sparse retrieval instead of hand-tuned BM25F only
- Small code embedding models (e.g. recent MiniLM/CodeBERT-class) for rerank-only, not full-index embed
- Graph diffusion (PPR) from seeds instead of fixed-depth BFS
- Better identifier tokenization aligned with 2024+ code search literature
- Test-aware expansion policies from SWE-agent / patch-localization papers

---

## Per-iteration loop (mandatory)

1. **Research pulse** (15 to 30 min at start of iteration, or when stuck): one focused search or paper skim; update Research notes.
2. **Pick next item** (roadmap or research-backed).
3. **Implement** smallest vertical slice; match repo style.
4. **Tests** unit/integration; add harness regression if retrieval changed.
5. **XML docs** on public API; MDX if user-facing surface changed.
6. **Verify:** `dotnet build Fuse.slnx -c Release` then `dotnet test Fuse.slnx -c Release --no-build` then `dotnet format Fuse.slnx --verify-no-changes`.
7. **Benchmark:** `layer2a`, `layer2b`, `layer4-scenario`, or `run-all.ps1`; commit `tests/benchmarks/results/`.
8. **Docs:** sync `site/content/docs/project/benchmarks.mdx`; `CHANGELOG.md`; roadmap if shipped.
9. **Commit** one feature per commit; do not push unless configured.

---

## Architecture rules

- Roslyn for structural C#; regex for lexer reduction and non-structural paths.
- `JsonSerializerContext` only for JSON serialization.
- BM25 + syntax graph = **default** fast path.
- Embeddings, compile graph, external rewrite = **opt-in**.
- Persistent cache: `.fuse/fuse.db` (WAL).
- MCP/CLI breaking changes: CHANGELOG Breaking + reference docs.

---

## Anti-patterns

- Fabricating or cherry-picking benchmark numbers.
- Optimizing mean recall while Newtonsoft/AutoMapper regress.
- Mandatory large model bundle on every MCP call.
- Skipping tests after "tuning only" changes.
- Leaving build/test/format red.
- Adopting research hype with no harness delta.

---

## Success criteria

- CI commands green.
- Benchmark results committed; docs match results.
- Measurable lift on Layer 2A query and Layer 4 fuse; per-repo table improves on hard repos.
- Research notes document what was tried, adopted, or rejected and why.

Work until interrupted or phases complete. If blocked, log blocker in CHANGELOG and move to an independent item.
