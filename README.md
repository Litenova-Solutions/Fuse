<!-- mcp-name: io.github.Litenova-Solutions/fuse -->

<p align="center">
  <img src="assets/fuse-logo.svg" alt="Fuse" width="150">
</p>

<p align="center">
  <b>Give your coding agent local compiler evidence and typed .NET wiring.</b>
</p>

<p align="center">
  <a href="https://fuse.codes/docs/start/quickstart">Quickstart</a> .
  <a href="https://fuse.codes/docs/start/connect-your-ai">Connect your agent</a> .
  <a href="https://fuse.codes/docs/reference/mcp-tools">Tool reference</a> .
  <a href="https://fuse.codes/docs/project/benchmarks">Benchmarks</a>
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/Fuse"><img src="https://img.shields.io/nuget/v/Fuse?logo=nuget&label=NuGet" alt="NuGet version"></a>
  <a href="https://github.com/Litenova-Solutions/Fuse/actions/workflows/ci.yml"><img src="https://github.com/Litenova-Solutions/Fuse/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI status"></a>
  <a href="https://registry.modelcontextprotocol.io"><img src="https://img.shields.io/badge/MCP-registry-6d4aff" alt="MCP Registry"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Litenova-Solutions/Fuse?color=6d4aff" alt="License: Apache 2.0"></a>
</p>

Fuse is a Model Context Protocol (MCP) server for Claude Code, Cursor, GitHub
Copilot, and other coding agents. It runs against the workspace on your machine
and requires no hosted model or embedding download.

<p align="center">
  <img src="assets/demo/fuse-check-demo.gif" alt="An agent proposes an edit with an invalid OrderOptions member. fuse_check returns CS1061 and a repair packet before the edit lands, then verifies the corrected proposal." width="820">
</p>

## Install and connect

From your project directory, with [.NET SDK 10](https://dotnet.microsoft.com/download)
or later:

```bash
dotnet tool install -g Fuse
fuse mcp install --rules
```

Restart or reload your MCP client. Then ask it to call `fuse_workspace` with
`action=status`. A response containing the workspace path, index mode, and verification
grade confirms that Fuse is active.

Need a self-contained binary, manual MCP configuration, or user-level setup? See
[Install](https://fuse.codes/docs/start/install) and
[Connect your agent](https://fuse.codes/docs/start/connect-your-ai).

## Five outcomes

1. **Check before writing.** `fuse_check` typechecks a proposed single-file edit and
   returns graded compiler diagnostics.
2. **See typed impact.** `fuse_impact` finds callers, implementers, and referencing
   types before a signature change.
3. **Resolve runtime wiring.** `fuse_find` traces services, requests, routes, and
   configuration to their implementations.
4. **Stage compiler-executed refactors.** `fuse_refactor` returns a diff only when
   the refactor introduces no new compiler diagnostic.
5. **Review scoped change context.** `fuse_review` starts from the git diff and adds
   graph-selected support files with provenance.

<p align="center">
  <img src="assets/fuse-wiring-example.svg" alt="Fuse resolves an interface through dependency injection registration to its concrete implementation and related callers." width="820">
</p>

The [MCP tool reference](https://fuse.codes/docs/reference/mcp-tools) contains the full
catalog, parameters, and response shapes. The
[internals documentation](https://fuse.codes/docs/internals/pipeline) covers the pipeline
and repository architecture.

## Scoped proof

Every result below comes from `tests/benchmarks/results` and has a reproduction command
on the [benchmarks page](https://fuse.codes/docs/project/benchmarks).

- On the OrderingApp fixture, 1,000 compiler-labeled mutations produced zero false-green
  and zero false-red verdicts. This result covers that fixture and mutation set.
- On the curated OrderingApp wiring fixture, the graph matched 24 of 24 hand-built edges
  with no false positives.
- Across 69 merged pull requests, review retained every git-changed file at 93.4 percent
  precision in a median 1,026 returned tokens. Changed files are seeds by construction.
- Across four recorded repositories, skeleton reduction removed 38 to 44 percent of
  tokens, retained every public and protected type name, and retained 96.3 to 99.4
  percent of public and protected method names.
- In the reduced-scope agent loop, true pass@1 was 89 percent for Fuse and 82 percent for
  native tools, with overlapping confidence intervals. Build and test calls were 3.1
  versus 3.2, so the run did not show a reduction by half.

## Local and write-safe

Fuse reads source, compiler state, git metadata, and its local `.fuse/fuse.db` index.
Read, check, impact, refactor, and review operations do not write the working tree.
`fuse_workspace` with `action=apply` is the one explicit tree-write path, and it is a dry
run unless `write=true`.

The typed semantic graph is .NET-only. Other languages have syntax-level search and
reduction. Open-ended localization is the fallback when a task provides no symbol, route,
request, service, config section, or git base.

## Contributing

Build and test commands, repository layout, and contribution policy are in
[Contributing](https://fuse.codes/docs/project/contributing) and [AGENTS.md](AGENTS.md).

Apache 2.0. Copyright (c) 2026 Litenova Solutions. See [LICENSE](LICENSE) and
[NOTICE](NOTICE).
