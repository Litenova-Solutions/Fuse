---
title: Introduction
description: What Fuse is, the problem it solves, and when to use it for AI-assisted .NET development.
---

AI coding agents fail on large codebases for a predictable reason: they read files one at a time. Against a solution with hundreds of C# files, an agent spends most of its context window discovering which files matter before it can do any real work, and on the largest repositories it runs out of room before it reaches the relevant code at all. Fuse removes that cold start. It collects the source that matters, reduces it for token efficiency, and emits a single structured payload an agent can consume in one call.

This page is for anyone evaluating Fuse: product and engineering leads deciding whether it fits their workflow, engineers about to use it for the first time, and maintainers who want the precise scope of what the tool does.

## What Fuse Is

Fuse is a context optimizer for .NET codebases. It reads a source directory, applies structure-aware reduction (stripping comments, usings, namespaces, and whitespace, or extracting signatures only), optionally scopes the result to the files relevant to a task, counts the tokens, and writes one output file or returns one in-memory payload.

It ships in two forms from a single package:

- A .NET global command, `fuse`, for use at the terminal and in scripts.
- A Model Context Protocol (MCP) server, started with `fuse serve`, that exposes the same capabilities as tools an AI client can call. MCP is the open protocol that lets clients such as Claude Code, Cursor, and GitHub Copilot call external tools.

## The Problem It Solves

Consider an agent asked to change how an order is charged in a 250,000-line solution. Without Fuse, it reads candidate files, greps for symbols, reads more files, and re-reads several as the conversation grows. The context it gathers is verbose, because C# carries usings, namespace wrappers, attributes, and documentation comments on every file, and most of what it reads turns out to be irrelevant.

Fuse changes the gathering step from many calls to one. A single query-scoped fusion returns the payment-related files plus their dependencies, reduced and token-counted, with a manifest the agent reads first to orient. The agent spends its budget reasoning about the right files instead of finding them.

## Who It Serves

| Reader | What Fuse gives them |
|--------|----------------------|
| AI agent (via MCP) | One scoped, reduced, token-budgeted payload instead of hundreds of file reads |
| Engineer at the terminal | A single fused file to paste into a chat, review, or archive |
| Team lead | Lower token spend and fewer failed agent runs on large repositories |

## What Makes It Different

Generic repository packers concatenate files as text. Fuse understands C# structure. It builds a dependency graph, extracts type and method skeletons, detects routes and project references, and ranks files by relevance to a query. That structural knowledge is what lets it scope to a feature area rather than dumping a directory.

## What This Does Not Cover

This page does not explain installation, command syntax, or the reduction pipeline in detail. It states what Fuse is and why it exists. Deep C# semantic analysis through Roslyn is not part of the current release; the dependency graph is regex-based and best-effort, which the [Scoping Internals](../architecture/scoping-internals.md) page describes in full.

## Next

Continue to [Installation](installation.md) to set up the `fuse` command, then [Your First Fusion](first-fusion.md) to produce output from a real project.
