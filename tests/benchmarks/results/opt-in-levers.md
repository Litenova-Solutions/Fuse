# Opt-in retrieval lever A/B results

Recorded measurements for the opt-in retrieval levers (default off), kept as a durable artifact alongside the
layer results. Each is a query-mode A/B over the same 24 pull requests layer 2A uses, on the pinned corpus,
counted with `o200k_base`, at the headline 50,000 token budget unless a budget column is shown. "off" is the
lexical BM25F floor; "on" enables the lever. Regenerate any row with the named spike (rebuild
`src/Host/Fuse.Cli` first); these are A/B probes, not the published default-path headline, which stays on the
lexical floor in `layer2a.md`.

None of these levers is on by default, so the published headline numbers in `benchmarks.mdx` and `AGENTS.md`
are unaffected; this file records why each stays opt-in.

## Item 9: dense bi-encoder rerank (all-MiniLM-L6-v2)

`spike-rerank.ps1`. Blends the lexical score with query-to-document cosine over a widened candidate pool.

| Budget | Repo | off | on | delta |
|-------:|------|----:|---:|------:|
| 10000 | AutoMapper | 33% | 42% | +9 |
| 10000 | FluentValidation | 39% | 35% | -4 |
| 10000 | MediatR | 72% | 78% | +6 |
| 10000 | NewtonsoftJson | 40% | 38% | -2 |
| 10000 | OVERALL | 46% | 48% | +2 |
| 25000 | OVERALL | 54% | 54% | 0 |
| 50000 | AutoMapper | 50% | 50% | 0 |
| 50000 | FluentValidation | 57% | 55% | -2 |
| 50000 | MediatR | 94% | 94% | 0 |
| 50000 | NewtonsoftJson | 42% | 38% | -4 |
| 50000 | OVERALL | 61% | 59% | -1 |

Decision: kept opt-in (`FUSE_RERANK=1`). It slightly regresses the headline (61 to 59) and only trades off at
tight budgets, below the ~60 percent bar for defaulting. A general-purpose embedder promotes plausible-but-wrong
files over lexically exact ones once the budget is generous.

## Item 11: cross-encoder rerank (ms-marco-MiniLM-L-6-v2)

`spike-rerank-cross.ps1`. Scores each query-document pair jointly. Hard repos (the plan's validation set),
50,000 token budget, three arms: lexical floor, bi-encoder, cross-encoder.

| Repo | off | bi | cross |
|------|----:|---:|------:|
| AutoMapper | 50% | 50% | 33% |
| NewtonsoftJson | 42% | 38% | 34% |
| OVERALL | 46% | 44% | 33% |

Decision: kept opt-in (`FUSE_RERANK=1` with `FUSE_RERANK_MODEL=cross`). It scored 33 percent, below both the
lexical floor (46) and the bi-encoder (44), and below the ~60 percent default bar. The cause is structural: a
cross-encoder runs once per candidate over the pair truncated to the model's context window, so at file
granularity the relevant member deep in a large file falls outside the window the model sees. Feeding it
shorter member-level chunks is the open follow-up.

## Item 8: coarse project-reference graph

`spike-projectgraph.ps1`. Links each candidate to files across a `.csproj` `ProjectReference` boundary, fed
into the expansion graph at a decayed weight. Query mode, 50,000 token budget.

| Repo | off | on | delta |
|------|----:|---:|------:|
| AutoMapper | 50% | 50% | 0 |
| FluentValidation | 57% | 61% | +4 |
| MediatR | 94% | 94% | 0 |
| NewtonsoftJson | 42% | 42% | 0 |
| OVERALL | 61% | 62% | +1 |

Decision: kept opt-in (`FUSE_PROJECT_GRAPH=1`). A small clean win (no per-repo regression) but within the wide
query confidence interval, and it adds a `.csproj` disk scan per query. The corpus is mostly two-project, so
the cross-assembly case is lightly exercised; a multi-assembly corpus is the honest re-test.

## Q5: member-level retrieval

`spike-memberlevel.ps1`. Indexes each declared member as its own document, rolls per-member scores up to a
file score, and adds a file the member pass surfaces as an extra seed. 50,000 token budget.

| Repo | off | on | delta |
|------|----:|---:|------:|
| AutoMapper | 50% | 62% | +12 |
| FluentValidation | 57% | 69% | +12 |
| MediatR | 94% | 94% | 0 |
| NewtonsoftJson | 42% | 46% | +4 |
| OVERALL | 61% | 68% | +7 |

Decision: kept opt-in (`FUSE_MEMBER_LEVEL=1`). The biggest single query lever this release (61 to 68, no
per-repo regression), but off by default because it re-parses files for member chunks every query and roughly
doubles to triples warm latency; default-on awaits caching the chunk extraction.

## Q6: git churn prior

`spike-churn.ps1`. Multiplies each candidate's score by a normalized recent-commit-count prior, computed
excluding the PR's own commits to avoid the head-checkout leak. 50,000 token budget.

| Repo | off | on | delta |
|------|----:|---:|------:|
| AutoMapper | 50% | 50% | 0 |
| FluentValidation | 57% | 57% | 0 |
| MediatR | 94% | 94% | 0 |
| NewtonsoftJson | 42% | 42% | 0 |
| OVERALL | 61% | 61% | 0 |

Decision: kept opt-in (`FUSE_GIT_CHURN_WEIGHT`). A no-op here by construction: the benchmark worktrees are
historical PR-head checkouts, so churn-from-now is uniformly empty once the PR's own commits are excluded. It
is a production-routing lever the pinned benchmark cannot validate, not a benchmark recall lever; the zero
delta confirms it does not leak.

## Default-on lever validation: query expansion

`spike-query-expansion.ps1`. Pseudo-relevance feedback (a second BM25F pass seeded with the first pass's
recurring declared-symbol terms). This lever is on by default; the arm shows the contribution folded into the
published query headline. 50,000 token budget.

| Repo | off | on | delta |
|------|----:|---:|------:|
| AutoMapper | 38% | 50% | +13 |
| FluentValidation | 55% | 57% | +2 |
| MediatR | 94% | 94% | 0 |
| NewtonsoftJson | 41% | 42% | +1 |
| OVERALL | 57% | 61% | +4 |

The "on" column is the default and matches the published query headline (61 percent) in `layer2a.md`. The
other default-on levers (budget-aware expansion, tiered emission, downgrade-before-drop) are validated in
`layer2a.md` and the `benchmarks.mdx` Findings.

## Behavior-preserving levers (no recall A/B)

Item 23 (rerank embedding cache by content hash) and the huge-file sketch (`FUSE_SKETCH_HUGE`) do not change
file selection on this corpus: item 23 only caches embeddings for the opt-in rerank path (a latency win, no
recall change), and the corpus failure mode is multi-file truncation rather than single-giant-file pack-outs,
so the sketch lever leaves the default output unchanged. Structural proximity edges (item 7, `FUSE_PROXIMITY`)
measured neutral and are folded into the same expansion channel as the project-graph above.
