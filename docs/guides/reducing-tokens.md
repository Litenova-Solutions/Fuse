---
title: Reducing Tokens
description: How to cut a fusion from light cleanup to aggressive compression and skeleton extraction, and when to use each level.
---

A fusion's value is its token count: the smaller it is, the more of an agent's context window stays free for reasoning. Fuse reduces C# along a range, from condensing whitespace at one end to emitting signatures with no method bodies at the other. This guide walks through that range level by level, so you can pick the smallest reduction that still answers the question you are asking.

This page is for engineers tuning the size of a fusion and for anyone deciding how much detail a given task needs.

## When This Applies

Reach for reduction whenever a fusion is larger than the budget you have for it, or when the reader does not need full implementation detail. The levels below stack: each builds on the one before it, so you can stop at the point where the output is small enough and still says what you need. The exact transform each flag performs is in the [Reducers reference](../reference/reducers.md), and the flags themselves are in the [Options reference](../reference/options.md).

## The Default Level

Every fusion applies a base level of reduction with no flags at all. Fuse normalizes whitespace, condenses blank lines, and minifies XML-family files such as `.csproj` and Razor and HTML views. This level changes nothing about the code's meaning and removes nothing a reader relies on, so it is always on.

```bash
fuse dotnet --directory ./src
```

## Individual C# Removals

The next level removes specific categories of C# text, each behind its own flag, so you can drop what is noise for your task and keep the rest:

- `--remove-csharp-comments` removes line and block comments, preserving string-literal contents.
- `--remove-csharp-usings` removes using directives and aliases.
- `--remove-csharp-namespaces` removes namespace declarations and the indentation a block namespace adds.
- `--remove-csharp-regions` removes `#region` markers and other preprocessor directives.

Combine the ones that fit. Removing comments and usings is a common middle ground that cuts tokens without altering structure:

```bash
fuse dotnet --directory ./src --remove-csharp-comments --remove-csharp-usings
```

## Aggressive Compression

The `--aggressive` flag runs the individual removals and then a second tier: it strips noise attributes such as `DebuggerDisplay` and `MethodImpl`, removes the `this.` prefix, rewrites auto-properties to compact form, and collapses whitespace around delimiters. It preserves string-literal contents throughout, so literal text is never altered. The output is no longer guaranteed to compile, which is acceptable for context an agent reads rather than builds.

```bash
fuse dotnet --directory ./src --aggressive
```

## All Reductions At Once

The `--all` flag applies every C# reduction in one switch: the full standard removal set together with aggressive mode. It is the shortest way to reach maximum reduction while keeping method bodies.

```bash
fuse dotnet --directory ./src --all
```

## Skeleton Extraction

The `--skeleton` flag goes further by dropping method bodies entirely, emitting class, interface, and method signatures only. The result is a structural map of the codebase, 80 to 90 percent smaller than a full fusion. Use it for a first pass on an unfamiliar solution, where the shape matters more than the implementation.

Skeleton extraction pairs with `--all`, which reduces the signatures it keeps, and with `--semantic-markers`, which annotates each type with a structural comment:

```bash
fuse dotnet --directory ./src --skeleton --all --semantic-markers
```

The [Generating an Architecture Overview](architecture-overview.md) guide covers skeletons and semantic markers as part of a structural first pass.

## Table-Of-Contents Survey

The `--toc` flag emits a directory tree with a per-file token cost and a symbol outline, instead of file bodies. It is a cheap first call: survey a codebase to decide which files are worth fetching, then fetch them. On the SampleShop fixture the table of contents is 221 tokens against a 624-token full read.

```bash
fuse dotnet --directory ./src --toc
```

## Collapse Generated Code

Fuse already excludes `*.g.cs`, `*.Designer.cs`, and auto-generated files by default, but EF Core migrations escape that exclusion because they are hand-checked-in source. The `--collapse-generated` flag collapses the generated method bodies in EF migrations and model snapshots, `Up`, `Down`, `BuildModel`, and `BuildTargetModel`, to a placeholder while keeping their signatures. Files without EF markers are left untouched. This flag is included in `--all`.

```bash
fuse dotnet --directory ./src --collapse-generated
```

## Reduction Levels

| Level | Command | Removes | Typical Use |
|-------|---------|---------|-------------|
| Default | `fuse dotnet --directory ./src` | Whitespace, blank lines; minifies XML and Razor | Every fusion; safe baseline |
| Individual removals | `--remove-csharp-comments --remove-csharp-usings` | Selected categories: comments, usings, namespaces, regions | Trim noise while keeping structure |
| Aggressive | `--aggressive` | Removals plus attributes, `this.`, property whitespace | Maximum reduction with bodies intact |
| All | `--all` | Every C# reduction at once | One switch for full compression |
| Skeleton | `--skeleton` | Method bodies; signatures only | Architecture pass on an unfamiliar solution |

## What This Does Not Cover

This page covers C# reduction levels. It does not cover the format reducers for web and configuration files, which run by default and are documented in the [Reducers reference](../reference/reducers.md), nor the token budgets that cap a fusion's size; see [Token Budgets and Splitting](token-budgets.md).

## Next

Continue to [Generating an Architecture Overview](architecture-overview.md) to combine skeletons with structural maps, or to [Token Budgets and Splitting](token-budgets.md) to cap the size of the result.
