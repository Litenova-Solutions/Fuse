<!-- mcp-name: io.github.Litenova-Solutions/fuse -->

<p align="center">
  <img src="assets/fuse-logo.svg" alt="Fuse" width="150">
</p>

<p align="center">
  <b>Typecheck your AI agent's .NET edits against the compiler before they land.</b>
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

Fuse is a Model Context Protocol server that connects your AI coding agent (Claude Code, Cursor, GitHub Copilot) to the .NET compiler. It typechecks a proposed edit before the agent writes it (`fuse_check`), computes the blast radius of a change (`fuse_impact`), stages compiler-executed refactors as diffs that compile or are not handed over (`fuse_refactor`), and resolves how the code is actually wired (which service implements an interface, which handler runs a request, which action a route hits) from a Roslyn graph rather than file names and grep. Every answer carries a grade so the agent knows what it is trusting, and Fuse abstains when it cannot answer at compiler grade. Scoped, reduced context and ranked retrieval are the supporting machinery that feed those answers. The same engine is also a `fuse` CLI.

<p align="center">
  <img src="assets/demo/fuse-check-demo.gif" alt="Terminal demo: an AI agent proposes an edit to OrderService.cs that references OrderOptions.MaxItemCount (a member that does not exist); fuse_check returns the real CS1061 diagnostic and a repair packet naming the fix (replace MaxItemCount with MaxItems) at oracle grade, before the edit lands; the corrected edit checks clean, with no dotnet build round-trip." width="820">
</p>

<p align="center">
  <img src="assets/fuse-loop-diagram.svg" alt="The Fuse verify loop: an agent proposes an edit; fuse_check typechecks it before it lands, returning diagnostics and a repair packet without a build; the agent applies the repair and re-checks; when clean, fuse_test runs only the covering tests; done. The path this replaces is the full dotnet build and dotnet test round-trip." width="820">
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
# Check an edit against the compiler before writing it (oracle-grade, or it abstains)
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

# Scope a change: the blast radius of a branch, packed under a token budget
fuse_review  changedSince="main"
  -> changed files + support files (the interface a changed type implements, its consumers)
     100% of changed files kept, ~1,026 tokens median, with provenance for each file
```

The rest of the surface, by what an agent does with it:

| Verb | What it does |
|------|--------------|
| `fuse_workspace` | Status and lifecycle: index mode and verify grade (`status`), build or refresh (`index`), symbols/routes/counts (`map`), per-project load diagnosis (`doctor`), and the one explicit tree-write path (`apply`). The cheap first call. |
| `fuse_find` | The find union: exact symbol/path/text lookup; resolve wiring (service, request, route, config); a symbol's exact signature; its callers and implementers; or rank candidate files for a task (refuses and hands back a map when the task names no code). |
| `fuse_check` | Typecheck a proposed single-file edit against the build-captured compilation; repair packets on API-shape errors. Oracle-grade, build-grade, or abstains. |
| `fuse_impact` | Blast radius for a symbol: callers, implementers, referencing types from the typed graph; also a NuGet upgrade break set. |
| `fuse_test` | Run the covering tests for a symbol, scoped by filter so the whole suite never runs. |
| `fuse_refactor` | Compiler-executed, verify-gated refactors staged as a diff: rename, change-signature, extract-interface, move-type, apply-codefix. |
| `fuse_review` | Diff-first semantic impact and packed context for a change, with the public API delta and a PR handoff packet. |
| `fuse_context` | Emit scoped, reduced source (with provenance) for selected seeds. |
| `fuse_reduce` | Compact a known set of files or raw content. |

Tool parameters and the full catalog: [MCP Tools](https://fuse.codes/docs/reference/mcp-tools).

## Benchmarks (honest, sourced, reproducible)

Fuse is judged as a compiler-grade semantic engine: can it verify an edit honestly, resolve .NET wiring, scope a change precisely, help an agent, and stay honest on a vague query. Every figure below is recorded under `tests/benchmarks/results` and reproduced with `fuse eval <suite>`; the full methodology and the modes where Fuse is weak are on the [benchmarks page](https://fuse.codes/docs/project/benchmarks). Numbers come from a fixed set of 24 real open-source .NET libraries pinned by commit, with the eShopOnWeb application as a supplementary run.

- **Verifies an edit without lying.** Over 1,000 compiler-checked edits (500 breaking, 500 neutral, generated by Roslyn and labeled by the compiler) plus 8 curated cases, `fuse_check` had zero false green and zero false red. Reproduce with `fuse eval checkgate --mutations 500`. (`checkgate.json`)
- **Finishes more tasks with fewer silent failures.** In a 234-run comparison driving Claude with and without Fuse, the Fuse arm finished 89% of tasks correctly (verified by the project's own tests) against the native arm's 82%, with fewer silent wrong answers (8 versus 9). Build round-trips were essentially equal (3.1 versus 3.2). Reproduce with `FUSE_LOOP_RUN=1 fuse eval loop`. (`loop.json`)
- **Fast compiler verdicts when resident.** With the resident workspace enabled (opt-in `FUSE_RESIDENT=1`, environment-dependent), speculative checks answered at P50 31.2 ms and P95 42.4 ms at NodaTime scale. Reproduce with `fuse resident-latency <path>`. (`resident-latency.json`)
- **Resolves .NET wiring deterministically.** On the wiring fixture, the extracted graph matches hand-built ground truth exactly (24 of 24 edges). (`semantics.json`)
- **Scopes a change with precision.** Over 69 real merged pull requests, `fuse review` keeps 100% of changed files at 93.4% precision in a median 1,026 returned tokens. A grep baseline reaches 67% recall at 8% precision. (`review.json`)
- **Honest on open-ended localization.** From a task title alone, `fuse localize` recalls 37.7% of changed files: the weakest mode, reported straight. On no-signal titles, Fuse refuses and hands back a navigation map instead of guessing. (`localize.json`)
- **Helps a real agent.** Driving Claude over 12 pull requests, the Fuse MCP arm edged out bare filesystem tools on file recall (30% versus 26%) at comparable token cost, on a small sample. (`agent.json`)
- **Token-efficient context.** The Roslyn skeleton keeps essentially all public API while removing 38 to 44% of tokens at skeleton level, 47 to 60% at public-API level. (`reduce.json`)

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

- **Solid: compiler-backed verification and the wiring graph.** The Roslyn-backed wiring graph (24 of 24 edges), speculative typecheck with repair packets, blast-radius impact, compiler-executed rename staged as a diff, change-impact review, and warm millisecond retrieval are the mature core.
- **Growing: the resident verified-edit loop.** The current program (see the [Roadmap](https://fuse.codes/docs/project/roadmap)) makes the compilation resident so truth arrives within a second after an edit without a build, adds out-of-process covering-test execution, and grades every verify answer so it never shrugs.
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
