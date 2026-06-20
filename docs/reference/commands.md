---
title: Commands
description: The five Fuse commands, what each one fuses, and the options they accept.
---

Fuse exposes its work through five commands. Three of them (`fuse`, `fuse dotnet`, and `fuse wiki`) produce a fusion from a source directory and differ only in which file types they collect and how they reduce them. The remaining two (`fuse init` and `fuse serve`) manage configuration and the agent server and run no fusion of their own. This page states what each command does, when to reach for it, and which option set applies.

This page is for engineers choosing a command and for maintainers who need the exact dispatch behavior. Stakeholders can read the summary table alone.

## Purpose And Scope

This page covers the command surface: names, descriptions, and one example invocation each. It does not document the flags themselves. Every fusion command (`fuse`, `fuse dotnet`, `fuse wiki`) shares the option set in the [Options reference](options.md), and `fuse dotnet` adds .NET-specific flags described there. The commands `fuse init` and `fuse serve` take no fusion options.

## Summary

| Command | Fuses | Reduction | Scoping | Options |
|---------|-------|-----------|---------|---------|
| `fuse` | Any files, using template defaults and shared options | None beyond shared content trimming | None | Shared |
| `fuse dotnet` | A .NET project, including C#, F#, and web files | C# reduction | Focus, changes, query | Shared plus .NET |
| `fuse wiki` | An Azure DevOps wiki repository (Markdown only) | None beyond shared content trimming | Changes | Shared |
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

The .NET-specific flags and the three mutually exclusive scoping modes are documented in the [Options reference](options.md). For task-oriented usage, see the [Scoping to What Matters](../guides/scoping.md) and [Reducing Tokens](../guides/reducing-tokens.md) guides.

## fuse wiki

This command fuses an Azure DevOps wiki repository and includes only `.md` files. Use it to combine a wiki tree into a single document for review or for an agent to read in one call.

```bash
fuse wiki --directory ./my-wiki
```

This command accepts the shared options in the [Options reference](options.md). It applies no language reduction beyond the shared content handling.

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

For client setup and the exposed tools, see the [Agent Integration overview](../agent-integration/overview.md).

## What This Does Not Cover

This page does not list the flags each command accepts, the configuration keys, or the output format. Those are in the [Options reference](options.md), the [Configuration Keys reference](configuration-keys.md), and the [Output Specification](output-specification.md).

## Next

Continue to the [Options reference](options.md) for the full flag set, or to [Your First Fusion](../getting-started/first-fusion.md) for a worked example.
