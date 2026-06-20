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
| changes | 71% | 21% | 30,091 |
| grep (baseline) | 38% | 11% | 41,452 |
| query | 37% | 3% | 52,206 |
| focus | 26% | 16% | 28,066 |

Recall varies widely by repository, and the aggregate hides that:

| Repo | changes | focus | query | grep |
|------|--------:|------:|------:|-----:|
| MediatR | 100% | 40% | 89% | 94% |
| FluentValidation | 100% | 47% | 59% | 23% |
| AutoMapper | 67% | 17% | 0% | 29% |
| Newtonsoft.Json | 17% | 0% | 0% | 5% |

Findings, including losses:

- Change scoping is the strongest mode. It leads recall at 71 percent and the lowest token cost, because it works from the Git diff rather than from a regex graph. On MediatR and FluentValidation it recalls every changed file.
- Query is competitive on well-named work and fails on noisy titles. The query is the pull request title. On FluentValidation it beats grep (59 versus 23 percent); on MediatR it is close to grep (89 versus 94). On AutoMapper the sampled pull requests carry continuous-integration titles with no domain words, and query recall falls to zero. The same noise hurts grep, but grep still finds some files by incidental term matches.
- Query precision is low. At 3 percent, query emits far more files than the task needed, because seeding ten files and expanding through the dependency graph pulls in a wide neighborhood. Query buys recall on good queries at a high token cost.
- Focus underperforms grep on recall here. Focus follows dependencies outward from one seed file, so it does not recover sibling changes such as a test that references the seed. This is a structural property of the mode, not a tuning gap.
- Newtonsoft.Json is hard for every mode. Deeply nested types and conditional compilation defeat the regex graph and the lexical index alike. This repeats the Layer 1 skeleton finding from the retrieval side.

## Layer 2B: Single-Turn Localization

Layer 2B is a cheap, outcome-flavored signal. It asks twelve natural-language questions of the form "which file handles X," each with one known correct answer file, and checks whether a mode surfaces that file inside a 20,000 token budget. The question set is recorded so the task is reproducible.

| Mode | Accuracy | Hits | Mean tokens |
|------|---------:|-----:|------------:|
| grep (baseline) | 58% | 7/12 | 19,924 |
| query | 25% | 3/12 | 23,674 |
| focus | 25% | 3/12 | 7,229 |

Findings, including losses:

- Grep wins this task. Literal keyword matching finds the answer file in 58 percent of questions, against 25 percent for Fuse query and focus. On a single-turn lexical lookup over well-named files, grep is a strong baseline and Fuse does not beat it here.
- Focus is the cheapest by far. It reaches its answers in about 7,000 tokens, a third of grep's budget use, but at lower accuracy. When focus resolves the right seed it localizes cheaply; when the seed is a concept rather than a type name, it misses.
- Lexical query has a ceiling. BM25 ranks by shared vocabulary, so a question that does not share words with the target file does not retrieve it. This is the case the planned embeddings rerank is meant to address.

## How To Read These Results Together

Layer 1 is the strong, clean claim: Fuse cuts 7 to 40 percent of tokens at full public-API fidelity with default and `--all`, cuts far more with skeleton at signature level, and beats Repomix on token cost in every mode. That claim is deterministic and holds across all four repositories.

Layer 2 is the honest boundary. Change scoping is reliable because it rests on Git. Focus and query rest on a best-effort regex dependency graph and a lexical index, and they do not dominate a grep baseline on these tasks. They help most when the query carries real domain words and the codebase is not dominated by conditional compilation. The planned Roslyn semantic plugin and embeddings rerank target exactly these gaps, and Layer 2 is the regression guard that will show whether they close.

## Blocked And Out Of Scope

- Layer 3, agentic resolution. A SWE-bench-style harness that drives a programmatic agent across arms and scores patches with a test oracle is not built here. It needs provisioned model credentials and compute, and it is confounded and scaffold-specific. It remains aspirational, as the roadmap states.
- Cross-machine timing. Wall-clock and memory were captured on one Windows machine. They are directional. Token counts and fidelity are machine-independent.
- Tokenizer scope. All counts use `o200k_base`. Absolute counts differ under other encodings; the ratios are what transfer.

## Related Pages

The measurement method for cold start and pipeline timing is in [Performance and Benchmarking](performance.md). The reduction flags are in the [Options reference](../reference/options.md), and the scoping modes are in [Scoping To What Matters](../guides/scoping.md). The harness, corpus manifest, PR ground truth, and question set live in the benchmarks directory described in its own readme.
