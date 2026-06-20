---
title: Benchmarks
description: Reproducible measurements of Fuse token reduction, fidelity, and scoping recall over a pinned corpus of real .NET repositories, with a competitor comparison and honest reporting of where Fuse loses.
---

This page reports what Fuse does to a real .NET codebase in numbers: how far it cuts the token count, how much of the public API surface survives that cut, and how well its scoping finds the files a task needs. Every number here comes from a harness that anyone can rerun against the same pinned commits. The goal is a credible claim backed by a script, not a marketing figure.

Three readers are served below. A stakeholder can read the headline findings and the fidelity table. A new engineer can follow the reproduction steps and run the suite. A maintainer can read the method, the corpus manifest, and the per-mode results, including the arms where Fuse ties or loses.

The benchmark is built in layers by cost and persuasiveness. Layer 1 is intrinsic and deterministic: token reduction, speed, memory, and fidelity. Layer 2 tests retrieval: scoping recall against real merged-PR change sets, and a single-turn localization task. Layer 3 (an agentic resolution harness) is not built here and is listed under Blocked And Out Of Scope.

## Reproduce First

Prerequisites: the .NET SDK 10.0 or later, Git, and Node.js (the competitor packer Repomix runs through `npx`). Network access is needed once to clone the corpus.

One command runs everything from a clean checkout:

```bash
pwsh -File tests/benchmarks/harness/run-all.ps1
```

That script builds the CLI and two measurement tools, clones the corpus at the pinned commits, regenerates the PR ground truth, and runs all three layers. Results are written as JSON, CSV, and Markdown tables under the results directory. To run one layer at a time, call the per-layer scripts in the harness directory after the corpus is in place.

The inputs that make the run reproducible are committed alongside the harness: the corpus manifest pins each repository to an exact commit, the PR ground truth records base and head commits per change set, and the localization question set records each question with its known answer file.

## What Is Measured, And How It Stays Fair

Token counts use the `o200k_base` encoding through `Microsoft.ML.Tokenizers`, the same encoding Fuse uses by default. Reduction ratios transfer across models because they are ratios; absolute token counts do not, because each model tokenizes differently. Every arm in a comparison is counted with the one tokenizer, so raw concatenation, Fuse output, and Repomix output are measured on the same scale.

Three rules keep the comparison honest:

1. One file set per repository. Each tool sees a C#-only mirror of the repository, so raw concatenation, Fuse, and Repomix all process the same files. This isolates C# reduction, which is the feature under test.
2. An independent fidelity oracle. The fidelity tool parses the raw source with Roslyn to build the ground-truth set of public and protected types, public methods, and ASP.NET route templates. It does not reuse Fuse's own regex extraction, so the measure cannot flatter Fuse by sharing its blind spots.
3. No tuning for the result. Fuse runs with documented flags only. Runtime behavior was not changed to improve any number on this page.

The corpus is four open-source .NET libraries pinned by commit, plus the in-repo sample fixture for route coverage:

| Repository | Size | C# files |
|------------|------|---------:|
| MediatR | small | 152 |
| FluentValidation | small to mid | 218 |
| AutoMapper | mid | 511 |
| Newtonsoft.Json | large | 945 |
| SampleShop | micro, in-repo | 7 |

## Layer 1: Token Reduction And Fidelity

Layer 1 runs each reduction mode against the raw concatenation baseline and against Repomix. The modes are the default reduction, `--all` (every C# reduction combined), and `--skeleton` (signatures only, bodies removed by design). Reduction ratio is the fraction of baseline tokens removed; a negative value means the output is larger than raw concatenation.

| Repo | Mode | Tokens | Reduction | Wall ms | Peak MB |
|------|------|-------:|----------:|--------:|--------:|
| MediatR | default | 77,135 | 7.1% | 439 | 109 |
| MediatR | all | 61,282 | 26.2% | 451 | 109 |
| MediatR | skeleton | 26,405 | 68.2% | 446 | 108 |
| MediatR | repomix | 86,328 | -3.9% | 1,893 | n/a |
| FluentValidation | default | 242,458 | 7.0% | 511 | 114 |
| FluentValidation | all | 166,740 | 36.0% | 574 | 111 |
| FluentValidation | skeleton | 51,890 | 80.1% | 450 | 109 |
| FluentValidation | repomix | 264,895 | -1.6% | 1,468 | n/a |
| AutoMapper | default | 420,320 | 9.8% | 608 | 117 |
| AutoMapper | all | 366,121 | 21.5% | 601 | 127 |
| AutoMapper | skeleton | 159,542 | 65.8% | 516 | 116 |
| AutoMapper | repomix | 475,896 | -2.1% | 1,645 | n/a |
| Newtonsoft.Json | default | 1,337,554 | 8.9% | 1,051 | 148 |
| Newtonsoft.Json | all | 879,747 | 40.1% | 863 | 161 |
| Newtonsoft.Json | skeleton | 96,953 | 93.4% | 534 | 128 |
| Newtonsoft.Json | repomix | 1,486,596 | -1.3% | 1,662 | n/a |

Repomix memory is not recorded because it runs through a Node launcher whose working set does not reflect the packer process. Its wall-clock and token count are comparable.

### Fidelity: Reduction Is Not Deletion

Fidelity is the integrity guard. It reports the fraction of the public API surface that survives each reduction. For `--skeleton`, these figures are signature preservation: bodies are removed on purpose, so skeleton makes no claim of body fidelity.

| Repo | Mode | Types kept | Methods kept | Routes kept |
|------|------|-----------:|-------------:|------------:|
| MediatR | default | 100% | 100% | n/a |
| MediatR | all | 100% | 100% | n/a |
| MediatR | skeleton | 100% | 84% | n/a |
| FluentValidation | default | 100% | 100% | n/a |
| FluentValidation | all | 100% | 100% | n/a |
| FluentValidation | skeleton | 98% | 99% | n/a |
| AutoMapper | default | 99% | 100% | n/a |
| AutoMapper | all | 99% | 100% | n/a |
| AutoMapper | skeleton | 91% | 86% | n/a |
| Newtonsoft.Json | default | 100% | 100% | n/a |
| Newtonsoft.Json | all | 100% | 99% | n/a |
| Newtonsoft.Json | skeleton | 71% | 4% | n/a |
| SampleShop | default | 100% | 100% | 100% (4/4) |
| SampleShop | all | 100% | 100% | 100% (4/4) |
| SampleShop | skeleton | 100% | 100% | 0% (0/4) |

The library repositories define no ASP.NET routes, so route fidelity is reported only for the sample fixture, which plants four routes.

### Layer 1 Findings, Including Losses

- Default and `--all` preserve the public API. Across the four libraries, default and `--all` keep 99 to 100 percent of public types and methods while removing 7 to 40 percent of tokens. This is the headline result: Fuse cuts tokens without dropping the surface an agent reads for.
- `--all` is the dependable cut. It removes 21 to 40 percent of tokens at full type and method fidelity. The cut is larger on repositories with heavy comments and usings (FluentValidation at 36 percent, Newtonsoft.Json at 40 percent).
- Skeleton trades signature completeness for the deepest cut, and it degrades on large complex code. Skeleton removes 66 to 93 percent of tokens. On the three smaller libraries it keeps 91 to 100 percent of types and 84 to 99 percent of method signatures. On Newtonsoft.Json it keeps only 71 percent of types and 4 percent of method signatures. That collapse is real: the regex skeleton extractor loses most signatures in a codebase with heavy conditional compilation and partial classes. Skeleton is an architecture map, not a substitute for the source, and this case is the clearest example of its limit.
- Skeleton drops route attributes. On the sample fixture, route fidelity falls to zero under skeleton because skeleton removes attributes. Default and `--all` keep all four routes. Use default or `--all` when route templates matter.
- Default reduction loses to raw on tiny inputs. SampleShop default produces more tokens than raw concatenation because the structured wrapper and manifest cost more than whitespace normalization saves on a 7-file project. The cut turns positive once the project is large enough for reduction to dominate the wrapper cost.
- Fuse beats Repomix in every mode, including default. Repomix output is 1.3 to 3.9 percent larger than raw concatenation on these repositories, because its preamble and structure add tokens. Fuse default is smaller than raw and smaller than Repomix; `--all` and `--skeleton` widen the gap. Both tools preserve the API surface at full output, so the difference is token cost. Fuse cold wall-clock is also lower here, though Fuse pays a fixed startup cost that dominates on tiny inputs.

## Layer 2A: Scoping Recall Against Real Pull Requests

Layer 2A measures whether a scoping mode includes the files a real change touched. The ground truth is the set of C# files changed by 24 real merged pull requests across the four libraries. For each pull request the harness reconstructs the head state in a Git worktree and runs three Fuse modes plus a grep baseline at a 50,000 token budget. The grep baseline ranks files by query-term hit count and fills the budget, which is what an agent does when it greps and reads the top results. Recall (the necessary files included) is the headline; precision is the fraction of emitted files that were necessary.

| Mode | Mean recall | Mean precision | Mean tokens |
|------|------------:|---------------:|------------:|
| changes | 88% | 61% | 34,605 |
| query | 54% | 11% | 42,531 |
| focus | 43% | 11% | 27,348 |
| grep (baseline) | 38% | 11% | 41,452 |

Recall varies widely by repository, and the aggregate hides that:

| Repo | changes | focus | query | grep |
|------|--------:|------:|------:|-----:|
| MediatR | 100% | 68% | 89% | 94% |
| FluentValidation | 100% | 44% | 49% | 23% |
| AutoMapper | 92% | 54% | 38% | 29% |
| Newtonsoft.Json | 58% | 7% | 40% | 5% |

Findings, including losses:

- Change scoping is the strongest mode. It leads recall at 88 percent and now carries high precision (61 percent), because following reverse edges one hop reaches the files that use a changed file without pulling in a wide neighborhood. On MediatR and FluentValidation it recalls every changed file.
- All three scoping modes now beat the grep baseline. Query (54 percent) and focus (43 percent) both clear grep (38 percent), where in the previous release focus trailed grep and query barely matched it. Focus gains because it now also pulls a seed's dependents, not only its dependencies, so a changed test that references the seed is reached. Query gains from fielded ranking and query normalization, which land a pull-request title on the files that declare the concept.
- Query precision improved but stays modest. Seeding and forward expansion still surface more files than the task strictly needed; the relevance-ordered budget cut keeps the most relevant within the 50,000 tokens rather than the largest.
- Newtonsoft.Json remains the hardest repository, but it is no longer a near-total miss. Change recall rose from 17 to 58 percent and query from 0 to 40 percent as comment and string stripping removed false graph edges and fielded ranking found concept files. Deeply nested types and conditional compilation still defeat parts of the regex graph, so it trails the smaller libraries.

## Layer 2B: Single-Turn Localization

Layer 2B is a cheap, outcome-flavored signal. It asks twelve natural-language questions of the form "which file handles X," each with one known correct answer file, and checks whether a mode surfaces that file inside a 20,000 token budget. The question set is recorded so the task is reproducible.

| Mode | Accuracy | Hits | Mean tokens |
|------|---------:|-----:|------------:|
| query | 67% | 8/12 | 19,911 |
| grep (baseline) | 58% | 7/12 | 19,924 |
| focus | 42% | 5/12 | 5,792 |

Findings, including losses:

- Query now leads this task. Fielded ranking and query normalization lift query accuracy from 25 to 67 percent, past the grep baseline (58 percent). A natural-language question of the form "which file handles X" lands on the file that declares X because the declared-symbol and path fields are weighted above the body and the question is stemmed onto the same terms as the code.
- Focus improved but still trails. Focus accuracy rose from 25 to 42 percent now that it follows dependents as well as dependencies, and it remains the cheapest mode by far at about 5,800 tokens. When the seed is a concept rather than a type name it still misses, which is why query is the better localization tool.
- A lexical ceiling remains. Ranking rewards shared vocabulary, so a question that shares no words or stems with the target file still misses. This is the case the planned embeddings rerank is meant to address; Layer 2B is the regression guard for it.

## Retrieval And Output Changes: Before And After

This section records the effect of the Phase 1 retrieval work and the Phase 2 output and trust work against the same pinned corpus. All token counts use `o200k_base`. Layer 1 reduction and fidelity did not change: default and `--all` still keep 99 to 100 percent of public types and methods, and skeleton still collapses on Newtonsoft.Json, so the cut was not bought by dropping API.

### Phase 1: Retrieval

The six Phase 1 items change one scoring and expansion pipeline, so the harness measures their combined effect rather than isolating each. The before column is the previous release; the after column is this release at the same budgets and depths.

| Metric | Before | After |
|--------|-------:|------:|
| Layer 2A recall, changes | 71% | 88% |
| Layer 2A recall, focus | 26% | 43% |
| Layer 2A recall, query | 37% | 54% |
| Layer 2A precision, changes | 21% | 61% |
| Layer 2B accuracy, focus | 25% | 42% |
| Layer 2B accuracy, query | 25% | 67% |
| Grep baseline (unchanged) | 38% / 58% | 38% / 58% |

What each item contributes to that combined movement:

1. Reverse edges and dependents. Focus and changed-since now pull files that reference a seed's declared types, not only the files it references. This is the main driver of the focus recall gain and the changes precision gain (dependents replace a wider forward expansion).
2. Fielded ranking (BM25F). Declared type and member names and path tokens are weighted above the body, which moves the file that declares a concept above files that mention it. This drives the Layer 2B query accuracy gain.
3. Comment and string stripping. Removing type names that appear only in comments or strings before graph extraction cut false edges; this is the largest contributor to the Newtonsoft.Json improvement, where prose and string mentions previously polluted the graph.
4. Budget-aware, rank-decayed expansion. Best-first expansion with per-hop decay and a budget stop keeps the seed neighborhood and drops distant files first, which holds precision as recall rises.
5. Query normalization. CamelCase splitting, stopword removal, and light stemming align a natural-language question with code identifiers, contributing to the query gains in both layers.
6. Relevance-ordered truncation. Under a token budget, emission now writes most-relevant first instead of largest first, so the seed file survives the cut. This protects recall and accuracy at the 50,000 and 20,000 token budgets used here.

### Phase 2: Output And Trust

These features are opt-in and do not change the default output, so they do not move the Layer 1 or Layer 2 arms above. Their effect is measured directly.

- Compact envelope (`--format compact`). Measured on the corpus under default reduction against XML: MediatR 85,431 to 84,732 tokens (0.8 percent), FluentValidation 277,453 to 276,389 (0.4 percent), AutoMapper 456,992 to 454,740 (0.5 percent), Newtonsoft.Json 3,155,917 to 3,151,980 (0.1 percent). The saving is the per-file envelope, so it grows with file count and shrinks as a fraction when files are large.
- Header dedup (`--dedup-headers`). On the pinned corpus the saving is negligible, because these projects do not prepend an identical comment header to every file (Newtonsoft.Json wraps its license in a `#region`, which the dedup correctly leaves alone). On a synthetic 40-file project where each file carries the same three-line banner, dedup cut 3,131 tokens to 1,989 (36 percent). The feature pays off on header-heavy codebases and is honestly near-zero here.
- Tokenizers. The Anthropic and Gemini estimators are deterministic. On a small sample the run report counted 49 tokens under `o200k_base`, 60 under `claude`, and 59 under `gemini`, reflecting their published characters-per-token ratios. These are estimates; OpenAI encodings remain exact.
- Verify. On MediatR excluding tests, `fuse verify` reports 86 of 86 public types and 36 of 36 public methods preserved under default and `--all`. It is a trust check, not a reduction metric.
- Explain and the JSON run report. Both surface the run without changing it: explain lists included and excluded files with a token estimate, and `--report` emits a machine-readable summary that names the tokenizer. They have no token-reduction figure to report.

### Phase 3 and Phase 4: Round-Trips and the Precision Tier

The Phase 3 round-trip features (table-of-contents survey, the `fuse_ask` auto-scope tool, review-shaped change emission, session-delta emission) and the Phase 4 precision tier (Roslyn analysis, symbol-level scoping, the persistent index, hybrid retrieval, cross-language reduction, and generated-code collapse) are all opt-in. None of them change the default reduction or scoping path. The harness was rerun over the same pinned corpus to confirm that, and the Layer 1 token counts and fidelity and the Layer 2 recall and precision above are byte-identical to the previous release. The gains are not bought by changing the default, because the default did not change.

Each item is measured directly where the harness exercises it:

- Table-of-contents (`--toc`, `fuse_toc`). On the SampleShop fixture the table of contents is 221 tokens against a 624-token cost to read every listed file in full under the default reduction, so the survey costs about a third of fetching the files it maps. The saving grows with file count, since the table of contents lists one line plus a symbol outline per file regardless of file size.
- Generated-code collapse (`--collapse-generated`). The corpus libraries contain no EF Core migrations, so this item does not move the Layer 1 numbers; it is unit-tested on a migration fixture, where the generated `Up`, `Down`, and `BuildModel` bodies are dropped while the class and method signatures are kept. It is reported here as not exercised by the corpus rather than as an improvement.
- Cross-language reduction (TS, JS, SQL). The corpus is a C#-only mirror, so this item does not move the Layer 1 numbers either; the TypeScript-family and SQL reducers are unit-tested for comment and whitespace removal. Extending the corpus with a TypeScript or SQL repository is the way to confirm a ratio, and is noted as future work rather than claimed here.
- Hybrid retrieval (`--rerank`). The bundled embedding is a deterministic lexical hashing model with no learned semantics, so on the Layer 2B localization questions, which are the lexical-ceiling cases, it does not change accuracy: BM25 already selects the candidates and the lexical vector reorders within the same lexical space. This is the honest result the roadmap predicts; closing the lexical gap needs a learned embedding model, for which the reranker provides the plug point. Layer 2B remains the regression guard for it.
- Roslyn precision tier (`--semantic`) and symbol-level scoping. These are opt-in and not part of the default harness arms. The Roslyn skeleton extractor is unit-tested on the conditional-compilation case that collapses the regex extractor; a corpus arm that runs the precision tier against Newtonsoft.Json, where the regex skeleton keeps only 4 percent of method signatures, is the planned measurement and is not run here.

### The Persistent Index: Cold Versus Warm

The persistent index (`--index`, on by default in watch and serve) caches per-file analysis on disk so repeated scoped calls reuse it. Measured on MediatR (152 C# files) with a query-scoped run, wall-clock cold (a cleared index) versus warm (the index populated by a prior run), counted on one Windows machine and including the fixed process startup cost:

| Tier | Cold | Warm |
|------|-----:|-----:|
| Regex (default) | 1,032 ms | 626 ms |
| Roslyn (`--semantic`) | 1,127 to 1,217 ms | 611 to 644 ms |

The warm run is about half the cold run. The Roslyn tier is more expensive cold because it parses each file, and the index is what amortizes that parse: warm, the two tiers converge because neither re-analyzes an unchanged file. These figures are directional and machine-specific, like the other wall-clock numbers on this page; the index's correctness (a hit returns the stored analysis, a changed file misses) is covered by unit tests.

A note on the competitor arm: the Repomix comparison in the Layer 1 table was not re-run in this environment because the `npx` fetch it needs had no network access. Repomix and the corpus are unchanged, so the previously recorded Repomix figures still stand; only the Fuse arms were re-measured, and they are unchanged.

## How To Read These Results Together

Layer 1 is the strong, clean claim: Fuse cuts 7 to 40 percent of tokens at full public-API fidelity with default and `--all`, cuts far more with skeleton at signature level, and beats Repomix on token cost in every mode. That claim is deterministic and holds across all four repositories.

Layer 2 is the honest boundary, and it moved this release. Change scoping is reliable because it rests on Git, and it now carries high precision through one-hop dependents. Focus and query still rest on a best-effort regex dependency graph and a lexical index, but with reverse edges, fielded ranking, comment and string stripping, and query normalization they now clear the grep baseline on both the recall and the localization tasks. They help most when the query carries real domain words and the codebase is not dominated by conditional compilation, which is why Newtonsoft.Json still trails. The planned Roslyn semantic plugin and embeddings rerank target the remaining lexical ceiling, and Layer 2 is the regression guard that will show whether they close it.

## Blocked And Out Of Scope

- Layer 3, agentic resolution. A SWE-bench-style harness that drives a programmatic agent across arms and scores patches with a test oracle is not built here. It needs provisioned model credentials and compute, and it is confounded and scaffold-specific. It remains aspirational, as the roadmap states.
- Cross-machine timing. Wall-clock and memory were captured on one Windows machine. They are directional. Token counts and fidelity are machine-independent.
- Tokenizer scope. All counts use `o200k_base`. Absolute counts differ under other encodings; the ratios are what transfer.

## Related Pages

The measurement method for cold start and pipeline timing is in [Performance and Benchmarking](performance.md). The reduction flags are in the [Options reference](../reference/options.md), and the scoping modes are in [Scoping To What Matters](../guides/scoping.md). The harness, corpus manifest, PR ground truth, and question set live in the benchmarks directory described in its own readme.
