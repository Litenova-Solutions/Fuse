---
title: Commands
description: The five Fuse commands, what each one fuses, and the options they accept.
---

Fuse exposes its work through seven commands. Three of them (`fuse`, `fuse dotnet`, and `fuse wiki`) produce a fusion from a source directory and differ only in which file types they collect and how they reduce them. Two more (`fuse explain` and `fuse verify`) run a fusion in memory to report on it without writing output. The remaining two (`fuse init` and `fuse serve`) manage configuration and the agent server and run no fusion of their own. This page states what each command does, when to reach for it, and which option set applies.

This page is for engineers choosing a command and for maintainers who need the exact dispatch behavior. Stakeholders can read the summary table alone.

## Purpose And Scope

This page covers the command surface: names, descriptions, and one example invocation each. It does not document the flags themselves. Every fusion command (`fuse`, `fuse dotnet`, `fuse wiki`) shares the option set in the [Options reference](options.md), and `fuse dotnet` adds .NET-specific flags described there. The `fuse explain` and `fuse verify` commands share the same options plus a subset of the .NET scoping and reduction flags. The commands `fuse init` and `fuse serve` take no fusion options.

## Summary

| Command | Fuses | Reduction | Scoping | Options |
|---------|-------|-----------|---------|---------|
| `fuse` | Any files, using template defaults and shared options | None beyond shared content trimming | None | Shared |
| `fuse dotnet` | A .NET project, including C#, F#, and web files | C# reduction | Focus, changes, query | Shared plus .NET |
| `fuse wiki` | An Azure DevOps wiki repository (Markdown only) | None beyond shared content trimming | Changes | Shared |
| `fuse explain` | Nothing; previews a .NET fusion in memory | Same as `fuse dotnet` | Focus, changes, query | Shared plus a scoping subset |
| `fuse verify` | Nothing; checks a .NET fusion in memory | Same as `fuse dotnet` | Focus, changes, query | Shared plus a scoping subset |
| `fuse init` | Nothing; writes a config file | Not applicable | Not applicable | None |
| `fuse serve` | Nothing; starts the MCP server | Not applicable | Not applicable | None |

## fuse

The root command is a flexible file combining tool for developers. It collects files using template defaults and the shared options, then emits one fusion. Use it when no language-specific reduction is needed and you want direct control over which extensions to include.

```bash
fuse --directory ./docs --include-extensions .txt,.md
```

The shared options for this command are documented in the [Options reference](options.md).

## fuse dotnet

This command fuses a .NET project, including C#, F#, and web files. It applies C# reduction and supports scoping a fusion to the files relevant to a task through focus, change, and query modes. It carries .NET-specific options beyond the shared set, including the C# reduction flags, skeleton extraction, and the scoping flags.

```bash
fuse dotnet --directory ./src --all --skeleton
```

The .NET-specific flags and the three mutually exclusive scoping modes are documented in the [Options reference](options.md). The same command also exposes the table-of-contents survey (`--toc`), generated-code collapse (`--collapse-generated`), the on-disk analysis index (`--index`), the opt-in Roslyn precision tier (`--semantic`), and the query reranker (`--rerank`). For task-oriented usage, see the [Scoping to What Matters](../guides/scoping.md) and [Reducing Tokens](../guides/reducing-tokens.md) guides.

## fuse wiki

This command fuses an Azure DevOps wiki repository and includes only `.md` files. Use it to combine a wiki tree into a single document for review or for an agent to read in one call.

```bash
fuse wiki --directory ./my-wiki
```

This command accepts the shared options in the [Options reference](options.md). It applies no language reduction beyond the shared content handling.

## fuse explain

This command previews a .NET fusion without writing anything. It runs collection, scoping, and in-memory reduction exactly as `fuse dotnet` would, then prints which files would be included with a per-file token estimate, which collected files would be excluded, and the estimated token total. Use it to check the effect of a focus seed, a query, a change range, or a reduction mode before committing to a full run.

```bash
fuse explain --directory ./src --focus OrderService --depth 2
```

It accepts the shared options plus a subset of the `fuse dotnet` scoping and reduction flags (`--focus`, `--query`, `--query-top`, `--depth`, `--all`, `--skeleton`), documented in the [Options reference](options.md).

## fuse verify

This command reports how much of a project's public API surface survives a .NET fusion. It runs a fusion in memory, then compares the public and protected types and methods, and ASP.NET route templates, declared in the included source against the fused output, printing the preserved percentage for each category. Pass `--json` for a machine-readable result. Use it to confirm that a reduction mode keeps the API you expect: a drop under the default or `--all` reduction signals lost API, while skeleton is signatures only by design.

```bash
fuse verify --directory ./src --all
fuse verify --directory ./src --skeleton --json
```

The source side is parsed by Roslyn in the framework-dependent tool and by an AOT-clean regex analyzer in the Native AOT build; both report the same categories. It accepts the same scoping and reduction subset as `fuse explain`, plus `--json`, documented in the [Options reference](options.md).

## fuse init

This command creates a `fuse.json` configuration file in the current directory. It writes the file only when one is absent; if a `fuse.json` already exists, the command writes nothing and reports an error. It takes no fusion options.

```bash
fuse init
```

The scaffold it writes and every supported key are documented in the [Configuration Keys reference](configuration-keys.md).

## fuse serve

This command starts the Fuse MCP server for AI agent integration. The server communicates over stdio: stdout is reserved for the Model Context Protocol byte stream, so all logging is routed to stderr to avoid corrupting the protocol. It takes no fusion options; each fusion is requested by the connected client through a tool call.

```bash
fuse serve
```

The server keeps the on-disk analysis index warm across calls, so a multi-call task pays the analysis cost once. Set the `FUSE_SEMANTIC` environment variable before starting the server to enable the Roslyn precision tier for that session.

For client setup and the exposed tools, see the [Agent Integration overview](../agent-integration/overview.md).

## What This Does Not Cover

This page does not list the flags each command accepts, the configuration keys, or the output format. Those are in the [Options reference](options.md), the [Configuration Keys reference](configuration-keys.md), and the [Output Specification](output-specification.md).

## Next

Continue to the [Options reference](options.md) for the full flag set, or to [Your First Fusion](../getting-started/first-fusion.md) for a worked example.
