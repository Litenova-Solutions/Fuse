---
title: Scoping to What Matters
description: Use focus, change, and query scoping to fuse only the files a task touches, plus their dependencies.
---

Fusing an entire solution wastes tokens on code a task never touches. Scoping narrows a fusion to a seed set of files and the dependencies around them, so the output holds the area that matters and little else. Fuse offers three scoping modes, and they are mutually exclusive: a single fusion uses at most one. This guide walks through each mode, when to reach for it, and the command that drives it.

This page is for engineers narrowing a fusion to a feature, a change, or a topic, and for agents selecting context for a task.

## When This Applies

Scope a fusion whenever you know which part of the codebase a task concerns. Pick the mode by what you know: a name, a set of changes, or a topic. The modes start from a seed set and expand through the dependency graph to a depth you control. Combining two scoping modes in one run is rejected by validation, so choose one. The ranking and graph traversal behind these modes are documented in [Scoping Internals](../architecture/scoping-internals.md), and the flags are in the [Options reference](../reference/options.md).

## Focus: Scope By Name

Use `--focus` when you know the area by name. The seed is a single string, and Fuse resolves it by trying four strategies in order, taking the first that yields a match: an exact relative path, then an exact filename, then files defining a type of that name, then a directory prefix. The `--depth` flag sets how far to expand from the seed through the dependency graph, from 1 to 10, defaulting to 1, which brings in direct dependencies only.

```bash
fuse dotnet --directory ./src --focus OrderService --depth 2
```

This seeds on the type `OrderService`, then expands two hops through its dependencies.

## Changes: Scope By Git Diff

Use `--changed-since` when reviewing a branch or pull request. The seed is the set of files changed since a git ref, which can be a branch, a commit, or a relative reference such as `HEAD~5`. By default `--include-dependents` is true, so the fusion also pulls in the first-degree dependents of each changed file, the code most likely to break from the change. This mode requires git on the PATH and a git repository.

```bash
fuse dotnet --directory ./src --changed-since main --include-dependents
```

This scopes to everything that changed since `main`, plus the files that depend on those changes.

## Query: Scope By Topic

Use `--query` when you have a topic but not a file name. Fuse ranks files by relevance to the query text, seeds on the top-ranked ones, and expands through their dependencies. The `--query-top` flag sets how many top-ranked files seed the expansion, defaulting to 10, and `--depth` controls the expansion the same way it does for focus.

```bash
fuse dotnet --directory ./src --query "discount calculation at checkout" --query-top 5 --depth 2
```

This ranks files against the query, seeds on the five most relevant, and expands two hops.

## Trace Inclusions With Provenance

Add `--provenance` to any scoping mode to annotate each included file with the chain that pulled it in: the seed it expanded from and the hops between. It answers why a file is in the fusion, which is useful for confirming a scope captured the right area and nothing more.

```bash
fuse dotnet --directory ./src --focus OrderService --depth 2 --provenance
```

## Scoping Modes

| Mode | Flag | Seeds on | Use when |
|------|------|----------|----------|
| Focus | `--focus` | A path, filename, type, or directory | You know the area by name |
| Changes | `--changed-since` | Files changed since a git ref, plus dependents | Reviewing a branch or pull request |
| Query | `--query` | The top-ranked files for a search query | You have a topic but not a file name |

## What This Does Not Cover

This page covers how to drive each scoping mode. It does not cover how relevance ranking and graph traversal work internally, including their false-positive and missed-edge behavior; see [Scoping Internals](../architecture/scoping-internals.md). It does not cover the reduction applied to the scoped result; see [Reducing Tokens](reducing-tokens.md).

## Next

Continue to [Token Budgets and Splitting](token-budgets.md) to cap a scoped fusion's size, or to [Scoping Internals](../architecture/scoping-internals.md) to understand the graph behind these modes.
