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

All numbers come from `tests/benchmarks/results` (the recorded data) and the benchmarks page at `site/content/docs/project/benchmarks.mdx` (published at fuse.codes/docs/project/benchmarks), over a commit-pinned OSS corpus, counted with `o200k_base`. Reproduce with `pwsh -File tests/benchmarks/harness/run-all.ps1`. Never fabricate or weaken a number; verify against the results before quoting. Current headline figures:

- Token reduction at full API fidelity: default 7 to 10 percent, `--all` 21 to 46 percent, keeping 99 to 100 percent of public types and methods. The Roslyn skeleton removes 39 to 56 percent at full signature fidelity.
- Change scoping recall 89 percent at 54 percent precision over 90 real merged PRs across five libraries; all scoping modes beat a grep baseline (34 percent).
- The Roslyn skeleton keeps 100 percent of method signatures, including Newtonsoft.Json where the retired regex skeleton kept 4 percent.
- The persistent analysis index roughly halves warm-call wall-clock.
- Context acquisition (layer 4, the same 90 PRs, 50,000 token budget): one scoped `fuse --query` call delivers the task's files in about 37,000 tokens at 49 percent recall, against a generic-packer (Repomix) dump of about 425,000 tokens at the same one call, and against about 409,000 tokens to read the repository blind. Couple Fuse's tokens with recall: 37,000 tokens at 49 percent recall is the honest pairing for the query stress floor (a sentence, no base). The routed default when a git base is available is change scoping, which in layer 4 reaches 91 percent recall at about 29,500 tokens (and clears 80 percent recall at the 25,000 token budget), so with git Fuse matches the task's files at a fraction of Repomix tokens rather than trading recall for tokens.

Round-trips are bounded by ground truth (layer 4): blind exploration must read each file a task needs at least once (a structural lower bound, mean 6.9 over the 90 PRs), while Fuse and Repomix each acquire the context in one call, so Fuse ties Repomix on round-trips and wins on tokens at that one call. That count is a lower bound, not a measured agent.

- Agent context sufficiency (layer 5, model-dependent, not byte-reproducible): a real agent (claude-haiku-4-5, run 2026-06-25, 10 PRs, 2 rollouts) driven with bare filesystem tools (native) versus the `fuse_*` MCP tools. Both arms reach 46 percent mean recall, but the fuse arm acquires sufficient context at a lower median token cost (about 324,000 against 440,000) with a higher model-scored sufficiency rate (0.42 against 0.35). Fuse wins on MediatR and Newtonsoft.Json, loses on FluentValidation and Serilog; both score 0 on two AutoMapper PRs whose titles do not describe their C# diff. The codegraph and serena arms are designed but not yet run (tools not installed). This layer measures tool calls and tokens to sufficiency, not wall-clock or task success.

Agent wall-clock and full task resolution (patch plus test oracle) are still not benchmarked; treat any such claim as illustrative.

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
