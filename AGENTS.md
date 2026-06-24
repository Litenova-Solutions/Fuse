# Fuse - Agent and Contributor Guide

Read this before editing Fuse source or docs. It holds the durable context about the project so a session does not need it repeated in a prompt.

## What Fuse Is

Fuse is a .NET-native codebase context optimizer for AI-assisted development. It collects source files, reduces them for token efficiency, and emits one structured payload an agent or developer can read in a single call instead of opening thousands of files. It ships as a .NET global tool (`fuse`) and as a Model Context Protocol server (`fuse mcp serve`) with eleven tools. It cuts tokens while keeping the public API intact, scopes to the files a task needs, and trims the round-trips an agent makes during its explore phase.

## Repository Layout

- `src/Core`: pipeline libraries - `Fuse.Collection`, `Fuse.Reduction`, `Fuse.Emission`, `Fuse.Fusion` (the orchestrator).
- `src/Host`: user-facing surfaces - `Fuse.Cli` (CLI commands and the MCP server).
- `src/Plugins`: `Fuse.Plugins.Abstractions`, `Fuse.Plugins.Languages.CSharp`, `Fuse.Plugins.Languages.CSharp.Roslyn`, `Fuse.Plugins.Formats.Web`.
- `tests/`: unit, golden-output, and integration tests. `tests/benchmarks/`: the harness, corpus manifest, and recorded results.
- `site/`: the documentation website (Next.js + Fumadocs), published at fuse.codes. All prose documentation lives here as MDX under `site/content/docs`.
- `assets/`: the benchmark figure (`fuse-benchmarks.png` and `.svg`) and the chart-generating script. `mcp-registry/`: the MCP Registry server manifest.
- Solution file: `Fuse.slnx`.

## Build, Test, Format

```bash
dotnet build Fuse.slnx -c Release
dotnet test Fuse.slnx -c Release --no-build
dotnet format Fuse.slnx --verify-no-changes
```

Build first, then test with `--no-build`. CI verifies build, test, format, and a self-contained publish smoke test for win-x64 and linux-x64.

## Design Invariants

- C# structural analysis (skeleton, outline, dependency, type-name, route map, semantic markers, symbol slice) uses Roslyn syntax parsing. Regex remains for lexer-based reduction, project-graph parsing, format reducers, pattern detectors, and secret redaction.
- Query scoping ranks files with BM25F; Fuse performs no model download.
- Persistent cache and index data live in a single SQLite file at `.fuse/fuse.db` (WAL mode).
- JSON uses source-generated `JsonSerializerContext` only (no reflection serialization).

## MCP Tools

Eleven tools: `fuse_toc`, `fuse_skeleton`, `fuse_focus`, `fuse_search`, `fuse_changes`, `fuse_ask`, `fuse_dotnet`, `fuse_generic`, `fuse_reduce`, `fuse_explain`, `fuse_find`. Plus MCP resources for skeleton, focus, search, and change workflows.

## Measured Results (source of truth)

All numbers come from `tests/benchmarks/results` (the recorded data) and the benchmarks page at `site/content/docs/project/benchmarks.mdx` (published at fuse.codes/docs/project/benchmarks), over a commit-pinned OSS corpus, counted with `o200k_base`. Reproduce with `pwsh -File tests/benchmarks/harness/run-all.ps1`. Never fabricate or weaken a number; verify against the results before quoting. Current headline figures:

- Token reduction at full API fidelity: default 7 to 10 percent, `--all` 21 to 40 percent, keeping 99 to 100 percent of public types and methods. Skeleton mode removes 66 to 93 percent.
- Change scoping recall 87 percent at 50 percent precision over 24 real merged PRs; all scoping modes beat a grep baseline (38 percent).
- The Roslyn skeleton keeps 100 percent of method signatures, including Newtonsoft.Json where the regex skeleton kept 4 percent.
- The persistent analysis index roughly halves warm-call wall-clock.
- Context acquisition (layer 4, the same 24 PRs, 50,000 token budget): one scoped `fuse --query` call delivers the task's files in about 41,800 tokens at 57 percent recall, against a generic-packer (Repomix) dump of about 512,000 tokens at the same one call, and against about 494,000 tokens to read the repository blind. Couple Fuse's tokens with recall: 41,800 tokens at 57 percent recall is the honest pairing, and the change-scoping mode reaches 87 percent recall when a git base is available.

Round-trips are bounded by ground truth (layer 4): blind exploration must read each file a task needs at least once (a structural lower bound, mean 5.8 over the 24 PRs), while Fuse and Repomix each acquire the context in one call, so Fuse ties Repomix on round-trips and wins on tokens at that one call. That count is a lower bound, not a measured agent. Agent wall-clock and live multi-turn traces are still not benchmarked; treat them as illustrative.

## Working Conventions

- Branch off `main`; open a PR via `gh` when verified. Do not merge, self-approve, or enable auto-merge; leave merges to reviewers.
- Keep build, test, and format green. New public API without XML docs is incomplete.

## Writing Style (docs and prose)

- Plain ASCII only. No em dashes, no emoji.
- User-facing pages (Start, Scenarios, Concepts) are outcome-first: lead with the result and a runnable example, then explanation. Reference pages stay dense and precise.
- Define a coined term (fusion, skeleton, seed, scoping, manifest, round-trip) in one plain sentence on first use.
- Avoid filler jargon: seamless, robust, ensure, leverage.
- Measured numbers are exact and sourced. Label any illustrative or theory-grounded claim as illustrative; never present it as a benchmark.

## Code Documentation Standard

### Public API: XML (`///`)

Apply to every `public` and `protected` type and member in `src/**/Fuse.*`.

1. Document on the interface or abstract base first; implementations use `<inheritdoc />` unless they add behavior worth noting.
2. Required tags: `<summary>`, `<param>` (every parameter, including `CancellationToken`), `<returns>` (non-void), `<exception cref="...">` (intentionally thrown).
3. Use `<remarks>` for ordering guarantees, side effects, performance, null semantics, or algorithm constraints.
4. Use `<see cref="..."/>` to link related types instead of repeating docs.
5. Style: four-space indent after `///`; property summaries as noun phrases (never "Gets or sets"); `<c>` for literals.

Do not add XML to `private` members.

### Internal complexity: `//` comments

Use regular comments for non-obvious `private` or `internal` logic: heuristics, state machines, regex pipelines, invariants, edge cases. Explain why, not what; skip obvious code. Comment when a reader must hold mental state (depth counters, accumulation, thresholds) to change the code safely.

### Where docs go

| Area | Public XML | Private `//` |
|------|------------|--------------|
| Orchestration (`FusionOrchestrator`, `*Pipeline`) | `<remarks>` on stage ordering and delegation | Stage logic inside private helpers |
| Language plugins | Full docs on capability interfaces; thin impls use `<inheritdoc />` | Regex and scan heuristics |
| Detectors and reducers | Summary plus remarks on false-positive tradeoffs | Non-obvious matching rules |
| Options and DTO records | Summary when the name alone is ambiguous | Rarely needed |

Full contribution workflow: [fuse.codes/docs/project/contributing](https://fuse.codes/docs/project/contributing). Pipeline context: [fuse.codes/docs/internals/pipeline](https://fuse.codes/docs/internals/pipeline). Documentation source lives in `site/content/docs`.
