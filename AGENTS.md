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

## Change-safety invariants

- Host RPC contract: any change to a `Fuse.Cli.Rpc` DTO or a `[JsonRpcMethod]` signature must bump both `FuseHostService.ProtocolVersion` and `ext/vscode/src/host/protocol.ts` `PROTOCOL_VERSION` in the same change, and update the extension client. The version constant exists to surface a stale-extension or new-host mismatch cleanly; leaving it stale turns a mismatch into an opaque error.
- External-process arguments are bounded. Commands built from a variable-length path or id list (for example the git invocations in `GitStatsProvider` and `GitChangeDetector`) must chunk the list or pass it via stdin. A single concatenated command line overflows the OS limit on large repos and silently degrades to empty results.
- Tests must actually run. When adding a test, confirm the test command discovers and executes it (the count goes up). The extension contract suite is wired through `ext/vscode/package.json` `test:contract`; a test placed where that script does not reach is dead. A green gate that ran zero new tests is not coverage.
- Behavior changes are not silent. A change that alters throw or propagation behavior (for example SQLite flush exhaustion swallowing instead of rethrowing) is a behavior change; call it out, do not fold it into an "add logging" change.

## MCP Tools

Eleven tools: `fuse_toc`, `fuse_skeleton`, `fuse_focus`, `fuse_search`, `fuse_changes`, `fuse_ask`, `fuse_dotnet`, `fuse_generic`, `fuse_reduce`, `fuse_explain`, `fuse_find`. Plus MCP resources for skeleton, focus, search, and change workflows.

## Measured Results (source of truth)

All numbers come from `tests/benchmarks/results` (the recorded data) and the benchmarks page at `site/content/docs/project/benchmarks.mdx` (published at fuse.codes/docs/project/benchmarks), over a commit-pinned OSS corpus, counted with `o200k_base`. V3 is judged by the semantic-engine north star (Section 18.13), not by tokens saved: can it resolve .NET wiring, scope a change with precision, help an agent find what runs, avoid overconfident junk on low-signal queries, and serve from a warm index. The four suites are reproduced with `fuse eval semantics|review|localize|agent`; the C# eval driver lives in `tests/benchmarks/Fuse.Benchmarks`. Never fabricate or weaken a number; verify against the results before quoting. Current figures (V3, recorded 2026-06-26):

- Corpus: five .NET libraries (MediatR, FluentValidation, AutoMapper, Newtonsoft.Json, Serilog) plus one ASP.NET Core application (eShopOnWeb, MIT, pinned), so external validity beyond clean library APIs is tested. PR ground truth (`prs.json`) reconstructs real merged-PR change sets from merge history and drops misleading-maintenance titles (a CI tweak or dependency bump over an unrelated C# diff). In this environment the corpus loads mostly in syntax or partial mode (full MSBuild semantic load needs a restored workspace), recorded with each suite's index-mode distribution; the full semantic graph is exercised on the eShopOnWeb application (partial) and the in-repo wiring fixture.
- Semantic resolution (Suite A, `semantics.json`): the extracted graph matches the hand-built edge ground truth 100 percent (10 of 10 edges, recall and precision 1.0) on the OrderingApp wiring fixture, which covers DI registration and constructor injection, MediatR request-to-handler, ASP.NET route-to-action, and options binding and consumption. This is the deterministic moat.
- Change impact (Suite B, `review.json`, 48 real merged PRs, 8 per repo, 25,000-token budget): `fuse review` changed-file recall 100 percent (it seeds changed files as must-keep), precision 89.6 percent, F1 0.95, at a median 874 and mean 3,196 returned tokens. On the eShopOnWeb application (partial mode) review adds a semantic blast radius beyond the changed files (for example eShopOnWeb#322 at 21 percent precision, #300 at 35 percent), which is the support-file signal; the library repos in syntax mode scope to the changed files. Index modes over the 48 PRs: partial 12, syntax 36.
- Open-ended localization (Suite C, `localize.json`, 108 PRs, title only, no git base): `fuse localize` overall changed-file recall 27.3 percent (95 percent CI 21.5 to 33.4) at 8.4 percent precision and a median 2,658 returned tokens. By signal bucket: identifier-rich 40 percent, natural-language domain 23 percent, route/API 12 percent, formatting 8 percent, test-only 33 percent. Low-signal detection F1 is 0.11 (1 true positive, 8 false positives, 8 false negatives): V3 does not yet flag a no-signal title, it returns full-text candidates, an honest gap the suite surfaces. This is the open-ended floor, the weakest mode, reported straight.
- Agent context sufficiency (Suite D, `agent.json`, model-dependent, not byte-reproducible): one Claude Code CLI driver (claude-sonnet-4-6) over two toolboxes, 6 PRs x native and fuse x 1 rollout (12 rollouts). Mean file recall: fuse 21 percent at a median 135,012 cumulative tokens, native 27 percent at a median 211,850 tokens, on a small sample with a wide CI (0 to 54 percent). The shape matches the moat: on MediatR#1171, whose title names the wiring, the fuse arm reached 100 percent recall in 4 tool calls at 121k tokens while the native arm read 328k tokens. Read recall together with tokens; this is a small, model-dependent sample.

- Token reduction and public-API fidelity (Suite E, `reduction.json`, offline, six repositories): the Roslyn skeleton keeps 100 percent of public and protected types and 99 to 100 percent of public methods (mean fidelity 99 percent), while removing 37 to 55 percent of tokens at skeleton level and 47 to 60 percent at public-API level; the lighter levels remove 11 to 39 percent (standard) and 16 to 45 percent (aggressive). Fidelity is counted by parsing the raw source with Roslyn as independent ground truth, so the measure is not circular.
- Semantic-mode restore (R0, `localize.restore.json` and `review.restore.json`): the harness can now restore each checkout before indexing via `fuse eval <suite> --restore` (and `--require-semantic` to skip rather than score below semantic). In this environment restore lifts the two repositories whose pinned commit still restores on the installed SDK: NewtonsoftJson and eShopOnWeb reach partial mode, while AutoMapper, FluentValidation, MediatR, and Serilog fail to restore (pinned-era central package management or older target framework against a newer SDK) and stay in syntax mode, a corpus-pinning limit rather than an engine limit. On eShopOnWeb, restoring the per-PR worktree lifts review to 15 of 18 PRs in partial mode (semantic blast radius), changed-file recall 1.0 at 64 percent precision in a median 587 returned tokens. Corpus-wide localization recall is unchanged at 27.3 percent because only two repositories lift and localization is still lexical (reconnecting the lexical ranker and hybrid retrieval is R1 and R2).

Token reduction is now a rendering and transport feature rather than the product claim: `fuse review` delivers a PR's context in a median 874 tokens. Warm latency, the peer-scoper comparison, and full task resolution (patch plus test oracle) are not re-run on V3 here; treat any such claim as illustrative until recorded.

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
