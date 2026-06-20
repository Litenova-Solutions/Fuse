---
title: Fusing a .NET Project
description: The end-to-end guide to the fuse dotnet command, from pointing at a directory to controlling output and excluding test projects.
---

The `fuse dotnet` command is the path most users take into Fuse. It selects the file extensions and exclusions a .NET solution needs, applies C# reduction, and writes one fused file you can paste into a chat, review, or archive. This guide walks through a full run: where to point it, what the DotNet template collects, how to name and place the output, and how to drop test projects from the result.

This page is for engineers running Fuse against a .NET solution and for anyone who wants to understand the default behavior before tuning it.

## When This Applies

Use `fuse dotnet` whenever the source you want to fuse is a .NET solution or project: C# source, project files, Razor views, and the configuration that sits alongside them. For a non-.NET repository, the generic `fuse` command takes a named template instead, listed in the [Templates reference](../reference/templates.md). The walkthrough in [Your First Fusion](../getting-started/first-fusion.md) covers the minimal run; this guide covers the options around it.

## Point At A Directory

The one input `fuse dotnet` needs is a source directory, given with `--directory`:

```bash
fuse dotnet --directory ./src
```

When `--directory` is omitted, Fuse processes the current directory. The path can be relative or absolute. Collection scans it recursively, applies the DotNet template's extension and exclusion lists, honors `.gitignore`, and skips binary and auto-generated files.

## What The DotNet Template Includes

The DotNet template collects C# source along with the files that describe a solution: project files, MSBuild props and targets, configuration in JSON, XML, and YAML, Razor and XAML views, and stylesheets. It excludes the directories that hold build output and editor state, such as `bin`, `obj`, `.vs`, and `node_modules`, and it drops generated, designer, and lock files through a pattern list so they never reach the output. The full extension list, the excluded directories, and the exclusion patterns are in the [Templates reference](../reference/templates.md).

To adjust the collected set for a single run, use `--include-extensions`, `--exclude-extensions`, or `--only-extensions`, documented in the [Options reference](../reference/options.md).

## Control The Output

By default, Fuse writes to a `Fuse` folder inside your Documents directory and generates a filename from the project name, a timestamp, and a token estimate. Three options take control of that:

- `--output` sets the destination directory.
- `--name` sets the file name without extension. When unset, the name is auto-generated.
- `--overwrite` controls whether an existing file of the same name is replaced. It defaults to true.

```bash
fuse dotnet --directory ./src --output ./context --name payments-context
```

This writes `payments-context` into a local `context` folder. The output format defaults to XML and is changed with `--format`; the [Output Formats](output-formats.md) guide covers the choice.

## Exclude Test Projects

Test code adds tokens that rarely help an agent reason about production behavior. Two options remove it, and they differ in what they keep:

- `--exclude-test-projects` excludes all test project directories: unit, integration, and benchmark projects alike.
- `--exclude-unit-test-projects` excludes only unit test projects, keeping integration tests and benchmarks.

Reach for `--exclude-unit-test-projects` when integration tests document the behavior you care about, such as how endpoints wire together, and for `--exclude-test-projects` when you want production source alone.

```bash
fuse dotnet --directory ./src --exclude-unit-test-projects --name app-with-integration
```

## Command Examples

| Goal | Command |
|------|---------|
| Quick fusion of a source tree | `fuse dotnet --directory ./src` |
| Named output in a local folder | `fuse dotnet --directory ./src --output ./context --name payments-context` |
| Production source only | `fuse dotnet --directory ./src --exclude-test-projects --all` |

## What This Does Not Cover

This page covers the shape of a `fuse dotnet` run, not the reduction levels that shrink it or the scoping modes that narrow it to a feature area. It does not cover the configuration file that lets you set these options once; see [Configuration Files](configuration.md).

## Next

Continue to [Reducing Tokens](reducing-tokens.md) to cut the output down, or to [Scoping to What Matters](scoping.md) to fuse only the files a task touches.
