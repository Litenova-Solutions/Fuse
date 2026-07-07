<!-- mcp-name: io.github.Litenova-Solutions/fuse -->

<p align="center">
  <img src="assets/fuse-logo.svg" alt="Fuse" width="150">
</p>

<p align="center">
  <b>A faster, cheaper, more accurate AI coding assistant on your .NET codebase.</b>
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

Fuse is a Model Context Protocol server that makes your AI coding assistant (Claude Code, Cursor, GitHub Copilot) faster, cheaper, and more accurate on .NET code. Its one concrete edge: it understands how your code is actually wired (which service implements an interface, which handler runs a request, which action a route hits) by reading the code with Roslyn, so the assistant answers from the real graph instead of guessing from file names and grep. Context for a task arrives scoped and reduced in one call, so the agent spends fewer tokens and fewer round-trips. The same engine is also a `fuse` CLI.

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

Fuse exposes verbs an agent uses while it works. Two carry most of the value:

```text
# Resolve .NET wiring: which implementation does the container give for IBasketService?
fuse_resolve  service="IBasketService"
  -> BasketService  (src/ApplicationCore/Services/BasketService.cs)
     edge: di_resolves_to  (registered services.AddScoped<IBasketService, BasketService>())

# Scope a change: the blast radius of a branch, packed under a token budget
fuse_review  changedSince="main"
  -> changed files + support files (the interface a changed type implements, its consumers)
     100% of changed files kept, ~958 tokens median, with provenance for each file
```

The rest of the surface, by what an agent does with it:

| Verb | What it does |
|------|--------------|
| `fuse_map` | Workspace map (symbols, routes, counts). The cheap first call. |
| `fuse_localize` | Rank candidate files for a task. Refuses and hands back a map when the task names no code. |
| `fuse_resolve` | Resolve wiring: service to implementation, request to handler, route to action, config to options. |
| `fuse_context` | Emit scoped, reduced source (with provenance) for selected seeds. |
| `fuse_review` | Diff-first semantic impact and packed context for a change. |
| `fuse_neighbors` | Graph neighborhood of a file, callers and implementers of a symbol, central files of an area. |
| `fuse_find` | Exact symbol, path, or text lookup. |
| `fuse_index` | Build or refresh the persistent semantic index (the read tools build it on first use). |

Tool parameters and the full catalog: [MCP Tools](https://fuse.codes/docs/reference/mcp-tools).

## Benchmarks (honest, sourced, reproducible)

Fuse is judged as a semantic engine, not a token compressor: can it resolve .NET wiring, scope a change precisely, help an agent, and stay honest on a vague query. Every figure below is recorded under `tests/benchmarks/results` and reproduced with `fuse eval <suite>`; the full methodology, the corpus, and the modes where Fuse is weak are on the [benchmarks page](https://fuse.codes/docs/project/benchmarks). Numbers are counted with the `o200k_base` tokenizer over a commit-pinned corpus (Scrutor, Ardalis.Specification, NodaTime, and the eShopOnWeb application).

- **Resolves .NET wiring deterministically.** On the wiring fixture, the extracted semantic graph matches the hand-built edge ground truth exactly (22 of 22 edges, recall and precision 1.0): DI registration and injection, MediatR request-to-handler, ASP.NET route-to-action, EF Core, decorators, options binding, and more. This is the moat a lexical or tree-sitter index cannot follow.
- **Scopes a change with precision.** Over 53 real merged pull requests, `fuse review` keeps 100 percent of the changed files at 79.8 percent precision in a median 958 returned tokens, adding the semantic blast radius (callers, DI consumers, handlers) on top. A grep baseline reaches 53 percent recall at 14 percent precision.
- **Honest on open-ended localization.** From a task title alone, with no git base, `fuse localize` recalls about 15 percent of the changed files: the weakest mode, reported straight. Rather than return a low-precision guess on a no-signal title, Fuse refuses and hands back a navigation map (correct-refusal rate 100 percent on no-signal titles).
- **Helps a real agent.** Driving Claude (sonnet-4-6) over 12 pull requests, the Fuse MCP arm edged out bare filesystem tools on file recall (30 versus 26 percent) at comparable token cost, on a small, model-dependent sample.
- **Token-efficient context.** The Roslyn skeleton keeps essentially all public API while removing roughly 37 to 55 percent of tokens at skeleton level (a support number; the headline is "a PR in about a thousand tokens").

No head-to-head ranking against other tools is claimed here; the measured peer comparison (with the exact reproduction command and every caveat) lives on the [benchmarks page](https://fuse.codes/docs/project/benchmarks).

## Command-line use

The same engine runs as a CLI for one-shot context outside an agent:

```bash
fuse index ./src                                   # build the semantic index
fuse localize ./src --task "discount rounding at checkout"
fuse dotnet --directory ./src --level skeleton     # architecture overview, signatures only
```

Output and option lists: [Commands](https://fuse.codes/docs/reference/commands) and [Quickstart](https://fuse.codes/docs/start/quickstart).

## Status

- **Solid: .NET semantic context.** The Roslyn-backed wiring graph, change-impact review, and warm millisecond retrieval are the mature core.
- **Default-on, offline dense retrieval.** A small local embedding model is fetched once and cached, then runs entirely offline; lexical is the fallback when no model is present.
- **Early: multi-language.** Non-C# languages are supported at the syntax tier (token-efficient context and search); the deep typed graph is .NET-only for now.

The planned direction is the [Roadmap](https://fuse.codes/docs/project/roadmap); shipped work is the [Changelog](https://fuse.codes/docs/project/changelog).

## Repository layout

```
src/
  Core/        Pipeline and engine: Fuse.Collection, Fuse.Reduction, Fuse.Emission,
               Fuse.Fusion, plus the V3 semantic stack (Fuse.Indexing, Fuse.Semantics,
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
