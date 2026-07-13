<!-- mcp-name: io.github.Litenova-Solutions/fuse -->

<p align="center">
  <img src="assets/fuse-logo.svg" alt="Fuse" width="150">
</p>

<p align="center">
  <b>A local compiler and typed .NET wiring service for your existing coding agent.</b>
</p>

<p align="center">
  <a href="https://fuse.codes">Website</a> .
  <a href="https://fuse.codes/docs">Documentation</a> .
  <a href="https://fuse.codes/docs/start/connect-your-ai">Connect your agent</a> .
  <a href="https://fuse.codes/docs/project/benchmarks">Benchmarks</a>
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/Fuse"><img src="https://img.shields.io/nuget/v/Fuse?logo=nuget&label=NuGet" alt="NuGet version"></a>
  <a href="https://www.nuget.org/packages/Fuse"><img src="https://img.shields.io/nuget/dt/Fuse?logo=nuget&label=downloads&color=6d4aff" alt="NuGet downloads"></a>
  <a href="https://github.com/Litenova-Solutions/Fuse/actions/workflows/ci.yml"><img src="https://github.com/Litenova-Solutions/Fuse/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI status"></a>
  <a href="https://registry.modelcontextprotocol.io"><img src="https://img.shields.io/badge/MCP-registry-6d4aff" alt="MCP Registry"></a>
  <a href="https://dotnet.microsoft.com/download"><img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 10"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Litenova-Solutions/Fuse?color=6d4aff" alt="License: Apache 2.0"></a>
</p>

---

Fuse is a local compiler and typed .NET wiring service for AI coding agents. Through Model Context Protocol (MCP), Claude Code, Cursor, GitHub Copilot, and other MCP clients can ask the installed `fuse` process to typecheck a proposed edit (`fuse_check`), inspect a change's typed blast radius (`fuse_impact`), stage compiler-executed refactors (`fuse_refactor`), or resolve which implementation, handler, action, or options type is wired at runtime. Fuse runs against the workspace on your machine. It does not require a hosted model or download an embedding model.

Compiler answers carry an evidence grade. Fuse uses the build-captured compilation when available, falls back to a scoped `dotnet build`, and abstains when neither path can answer. Checks evaluate an in-memory single-file proposal and do not write the working tree. Scoped context, lexical retrieval, and structural reduction support these compiler and graph operations; they are not the product identity. The same engine is also available through the `fuse` CLI.

<p align="center">
  <img src="assets/demo/fuse-check-demo.gif" alt="Terminal demo: an AI agent proposes an edit to OrderService.cs that references OrderOptions.MaxItemCount (a member that does not exist); fuse_check returns the real CS1061 diagnostic and a repair packet naming the fix (replace MaxItemCount with MaxItems) at oracle grade, before the edit lands; the corrected edit checks clean, with no dotnet build round-trip." width="820">
</p>

<p align="center">
  <img src="assets/fuse-loop-diagram.svg" alt="The Fuse verify loop: an agent proposes an edit; fuse_check returns graded compiler diagnostics and a repair packet before the file is written; the agent applies the repair and re-checks; when clean, fuse_test runs the selected covering tests." width="820">
</p>

Full documentation: **[fuse.codes](https://fuse.codes/docs)**. Contributors and roadmap planners: see [briefing.md](briefing.md) for architecture, benchmark evidence, and plan history.

## Install

Fuse is a .NET developer tool, so the recommended install is the .NET global tool ([.NET SDK 10.0](https://dotnet.microsoft.com/download) or later):

```bash
dotnet tool install -g Fuse
```

Or run it on demand without installing: `dnx Fuse -- serve`. No SDK? Install a self-contained binary (the .NET runtime is bundled):

```bash
curl -fsSL https://fuse.codes/install.sh | sh      # Linux / macOS
irm https://fuse.codes/install.ps1 | iex           # Windows PowerShell
```

On Windows: `winget install Litenova.Fuse`, or grab a binary from [Releases](https://github.com/Litenova-Solutions/Fuse/releases). Verify with `fuse --help`. Full notes: [fuse.codes/docs/start/install](https://fuse.codes/docs/start/install).

## Connect your agent

Register Fuse once; your client launches `fuse mcp serve` automatically when MCP is enabled:

```bash
fuse mcp install --rules
```

That writes MCP config for Claude Code, Cursor, and GitHub Copilot in the current project, and `--rules` adds a short instruction so the agent reaches for the `fuse_*` tools instead of grepping blindly. Use `--scope user` for every project on the machine, or `--client cursor` for one client.

Prefer to wire it by hand? The MCP server config is the same shape everywhere:

```json
{
  "mcpServers": {
    "fuse": {
      "command": "fuse",
      "args": ["mcp", "serve"]
    }
  }
}
```

Put it in `.mcp.json` (Claude Code), `.cursor/mcp.json` (Cursor), or `.vscode/mcp.json` (Copilot); any MCP client uses the same `command` and `args`. The Claude CLI shortcut is `claude mcp add fuse --scope project -- fuse mcp serve`. Both surfaces are covered in [Connect your agent](https://fuse.codes/docs/start/connect-your-ai).

## Quickstart (what the agent calls)

Fuse exposes verbs an agent uses while it works. The ones that carry the identity verify and scope a change:

```text
# Check an edit against the compiler before writing it
fuse_check  file="OrderService.cs"  content="<proposed edit>"
  -> CS1061 at OrderService.cs:41: 'Order' has no member 'TotalAmount'
     repair packet: 'Total' exists on Order; 'TotalAmount' does not
     grade: oracle

# Blast radius: what a signature change breaks, before touching it
fuse_impact  symbol="IBasketService.Checkout"
  -> callers, implementers, referencing types from the typed graph

# Resolve .NET wiring: which implementation does the container give for IBasketService?
fuse_find  kind="service" query="IBasketService"
  -> BasketService  (src/ApplicationCore/Services/BasketService.cs)
     edge: di_resolves_to  (registered services.AddScoped<IBasketService, BasketService>())

# Review a branch: git-seeded context plus typed support files
fuse_review  changedSince="main"
  -> changed files + graph-selected support files, with provenance
     median 1,026 tokens across the recorded 69-PR corpus
```

The rest of the surface, by what an agent does with it:

| Verb | What it does |
|------|--------------|
| `fuse_workspace` | Status and lifecycle: index mode and verify grade (`status`), build or refresh (`index`), symbols/routes/counts (`map`), per-project load diagnosis (`doctor`), and the one explicit tree-write path (`apply`). The cheap first call. |
| `fuse_find` | The find union: exact symbol/path/text lookup; resolve wiring (service, request, route, config); a symbol's exact signature; its callers and implementers; or rank candidate files for a task (refuses and hands back a map when the task names no code). |
| `fuse_check` | Typecheck a proposed single-file edit against the build-captured compilation; repair packets on API-shape errors. Oracle-grade, build-grade, or abstains. |
| `fuse_impact` | Blast radius for a symbol: callers, implementers, referencing types from the typed graph; also a NuGet upgrade break set. |
| `fuse_test` | Run the tests connected to a symbol by persisted test edges, scoped by filter; reports selection-only when no covering edge is known. |
| `fuse_refactor` | Compiler-executed, verify-gated refactors staged as a diff: rename, change-signature, extract-interface, move-type, apply-codefix. |
| `fuse_review` | Diff-first semantic impact and packed context for a change, with the public API delta and a PR handoff packet. |
| `fuse_context` | Emit scoped, reduced source (with provenance) for selected seeds. |
| `fuse_reduce` | Compact a known set of files or raw content. |

Tool parameters and the full catalog: [MCP Tools](https://fuse.codes/docs/reference/mcp-tools).

## Benchmarks (honest, sourced, reproducible)

Fuse is judged as a local compiler and typed wiring service: can it verify an edit honestly, resolve .NET wiring, assemble useful branch context, and abstain when its evidence is insufficient? Every figure below is recorded under `tests/benchmarks/results` and reproduced with `fuse eval <suite>`; the full methodology and limits are on the [benchmarks page](https://fuse.codes/docs/project/benchmarks).

- **Correct on the tested edit set.** On the OrderingApp fixture, 1,000 compiler-labeled mutations (500 breaking and 500 neutral) produced zero false-green and zero false-red verdicts. Eight curated cases also classified correctly. This result describes that tested fixture, not every .NET edit. Reproduce with `fuse eval checkgate --mutations 500`. (`checkgate.json`)
- **Promising agent-loop result, with no build-call collapse.** The reduced-scope loop run observed 89% true pass@1 for Fuse (95% CI 82%-95%) and 82% for native tools (95% CI 74%-90%). The confidence intervals overlap. Agent-visible build and test calls were 3.1 versus 3.2, so this run does not support a claim that Fuse halves build round-trips. (`loop.json`)
- **Millisecond checks are opt-in.** With `FUSE_RESIDENT=1`, the dedicated NodaTime measurement recorded speculative checks at P50 31.2 ms and P95 42.4 ms. The resident workspace is not the default and timings depend on the machine. (`resident-latency.json`)
- **Resolves curated .NET wiring exactly.** The extracted graph matched all 24 hand-built edges in the OrderingApp wiring fixture, with no false positives. This is a curated fixture result. (`semantics.json`)
- **Builds compact, git-seeded branch context.** Across 69 merged pull requests, `fuse_review` retained every git-changed file at 93.4% precision in a median 1,026 returned tokens. Changed files are seeds by construction; the result does not prove that every file needed to reason about each change was present. (`review.json`)
- **Honest on open-ended localization.** From a task title alone, `fuse localize` recalls 37.7% of changed files: the weakest mode, reported straight. On no-signal titles, Fuse refuses and hands back a navigation map instead of guessing. (`localize.json`)
- **Reduces context after scope is known.** The Roslyn skeleton removed 38% to 44% of tokens while retaining all public types and 97% to 100% of public methods across the four recorded repositories. The public-API tier removed 47% to 60%. (`reduce.json`)

No head-to-head ranking against other tools is claimed here; the measured peer comparison (with the exact reproduction command and every caveat) lives on the [benchmarks page](https://fuse.codes/docs/project/benchmarks).

## Command-line use

The same engine runs as a CLI for one-shot context outside an agent:

```bash
fuse index ./src                                   # build the semantic index
fuse localize ./src --task "discount rounding at checkout"
fuse reduce --files src/Program.cs --level skeleton
```

Output and option lists: [Commands](https://fuse.codes/docs/reference/commands) and [Quickstart](https://fuse.codes/docs/start/quickstart).

## Status

- **Core: local compiler evidence and typed .NET wiring.** Fuse checks proposed edits, reports evidence grades, resolves DI and framework wiring, computes typed impact, and stages compiler-verified refactors. The read and check paths do not write the working tree; `fuse_workspace action=apply` is the explicit write path.
- **Opt-in: the resident verified-edit loop.** `FUSE_RESIDENT=1` keeps a build-captured compilation resident for millisecond speculative checks. Without it, Fuse uses the graded build fallback or abstains.
- **Supporting machinery: retrieval and reduction.** Ranked localization is the fallback when a task names no anchor; skeleton reduction fits scoped context to a token budget. Both feed the verification and resolution answers above.
- **Early: multi-language.** Non-C# languages are supported at the syntax tier (token-efficient context and search); the deep typed graph is .NET-only for now.

The planned direction is the [Roadmap](https://fuse.codes/docs/project/roadmap); shipped work is the [Changelog](https://fuse.codes/docs/project/changelog).

## Repository layout

```
src/
  Core/        Pipeline and engine: Fuse.Collection, Fuse.Reduction, Fuse.Emission,
               Fuse.Fusion, plus the semantic stack (Fuse.Indexing, Fuse.Semantics,
               Fuse.Retrieval, Fuse.Context).
  Host/        Fuse.Cli: the CLI commands and the MCP server.
  Plugins/     Extension-keyed providers: Abstractions, Languages.CSharp,
               Languages.CSharp.Roslyn, Formats.Web.
tests/         Unit, golden-output, and integration tests; benchmarks under tests/benchmarks.
site/          The fuse.codes website and documentation (Next.js + Fumadocs).
assets/        The benchmark figure and its chart-generating script.
mcp-registry/  MCP Registry server manifest.
```

## Development

```bash
dotnet build Fuse.slnx --configuration Release
dotnet test Fuse.slnx --configuration Release --no-build
dotnet format Fuse.slnx --verify-no-changes
```

Contribution workflow: [Contributing](https://fuse.codes/docs/project/contributing). Durable project context for contributors and agents: [AGENTS.md](AGENTS.md). The docs site is in [site/](site/).

## License

Apache 2.0. Copyright (c) 2026 Litenova Solutions. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
