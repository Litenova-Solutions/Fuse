---
title: Recommended Workflows
description: The progressive tool sequence for giving an AI agent context on a large .NET codebase.
---

An agent working on a large .NET codebase should gather context in stages rather than in one large request. Starting wide and narrowing keeps each request inside the agent's token budget and avoids spending that budget on files the task does not touch. This page describes the sequence the Fuse tools are designed to support and the token range to expect at each stage.

This page is for engineers writing agent instructions and for anyone deciding how much context a given task needs.

## Purpose and Scope

The sequence below applies to .NET codebases addressed through the Fuse MCP server. MCP (Model Context Protocol) is the open protocol that lets an AI client call external tools. Each stage maps to one tool documented in [Tools Reference](tools.md). The scoping concepts behind focus, change, and query selection are described in [Scoping to What Matters](../guides/scoping.md).

## The Progressive Sequence

Work through these stages in order, stopping at the stage that gives the agent enough to act.

1. Call `fuse_toc` first, or `fuse_skeleton`. The table of contents returns a directory tree with per-file token costs and a symbol outline; the skeleton returns signatures without method bodies. Either gives a low-token map of the whole codebase, and the table of contents also tells the agent what fetching each file will cost.
2. Drill into the relevant area. When the agent knows the type or file by name, call `fuse_focus` with that seed. When it has a topic but not a name, call `fuse_search` with a query. Both expand from the seed through the dependency graph.
3. For pull-request review, call `fuse_changes` with the base branch as the git ref. It returns the files changed since that ref plus their first-degree dependents; set `review=true` to also get the diff hunks and direct callers per changed file.
4. When a task needs options the workflow tools do not expose, call `fuse_dotnet`. It exposes every .NET fusion option in one call.

As a shortcut, `fuse_ask` collapses stages 1 and 2: give it a task and a token budget, and it chooses skeleton, focus, or search and packs the result to budget. Across calls in one task, pass a `session` id to `fuse_focus` and `fuse_search` so files already returned are not sent again.

The three scoping modes (focus, change, and query) are mutually exclusive. A single fusion uses at most one of them.

## Token Budget Guidance

Set `maxTokens` on each call to fit the agent's remaining context window. The ranges below are starting points for a large solution; actual counts depend on the codebase and the reduction applied.

| Stage | Tool | Typical token range |
|-------|------|---------------------|
| Survey | `fuse_toc` | 5,000 to 30,000 |
| Architecture map | `fuse_skeleton` | 50,000 to 100,000 |
| Focused drill-in | `fuse_focus` or `fuse_search` | 100,000 to 200,000 |
| Change review | `fuse_changes` | 50,000 to 150,000 |
| Full fusion | `fuse_dotnet` with `all` | 200,000 to 800,000 |

## Example Call Sequence

A typical task moves from the skeleton into a focused area:

```
fuse_skeleton(path="C:/Projects/MyApp/src", maxTokens=80000)
fuse_focus(path="C:/Projects/MyApp/src", focus="OrderService", depth=1, maxTokens=150000)
fuse_changes(path="C:/Projects/MyApp/src", changedSince="origin/main", maxTokens=100000)
```

The first call orients the agent, the second pulls in the order-processing area and its dependencies, and the third scopes a later review to the branch under review.

## What This Does Not Cover

This page covers the order and budget of tool calls. It does not document each tool's full parameter set or the resource URIs. See [Tools Reference](tools.md) for parameters and [Scoping to What Matters](../guides/scoping.md) for how seed selection and dependency expansion work.

## Next

Continue to [Tools Reference](tools.md) for the complete parameter tables, or to [Scoping to What Matters](../guides/scoping.md) to understand the selection behind each scoping mode.
