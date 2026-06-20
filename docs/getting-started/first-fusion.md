---
title: Your First Fusion
description: Run Fuse against a .NET project, read the output it produces, and find the generated file.
---

This page walks through a first run of Fuse against a .NET project: the command to run, what appears on screen, where the output lands, and how to read it. By the end you will have produced a single fused file and understood its structure.

This page is for engineers running Fuse for the first time. It assumes the `fuse` command is installed, which [Installation](installation.md) covers.

## Run A Fusion

From any directory, point the `dotnet` template at a source folder:

```bash
fuse dotnet --directory ./src
```

The `dotnet` template selects the file extensions and exclusions appropriate for a .NET solution (C#, project files, Razor, configuration, and more) and applies C# reduction. The [Templates reference](../reference/templates.md) lists every template and its defaults.

## Read The Console Output

Fuse reports its progress and a summary as it runs. The summary states the number of files included, the estimated token count, the elapsed time, and reduction cache statistics in the form `cache: N hit / M miss`. The token count is the figure that matters for budget planning, because it estimates how much of an AI model's context window the output will consume.

## Find The Output File

By default, output is written to a `Fuse` folder inside your Documents directory. The filename includes the project name, a timestamp, and a token estimate, for example:

```
MyProject_2026-06-19_0130_22k.txt
```

The `22k` suffix is the estimated token count, so you can judge the size of a fusion from its filename alone. To control the destination and name:

```bash
fuse dotnet --directory ./src --output ./context --name myproject
```

## Read The Output Structure

The output opens with a manifest: a file tree listing each included file and its token cost. The manifest is what an agent or a reader consults first to understand the shape of the fusion before reading any file body. After the manifest, each file appears in a path-tagged block. In the default XML format:

```xml
<file path="src/Services/OrderService.cs">
public class OrderService { }
</file>
```

Files are ordered largest first by token count, so the most expensive content is visible at the top. The [Output Specification](../reference/output-specification.md) documents every format and the manifest in full.

## Reduce Further

The first run applies standard reduction. To cut tokens aggressively, add `--all`, which removes comments, usings, namespaces, and regions and applies compression:

```bash
fuse dotnet --directory ./src --all
```

For an architecture-only view that emits signatures without method bodies, add `--skeleton`:

```bash
fuse dotnet --directory ./src --all --skeleton
```

Skeleton output typically runs 80 to 90 percent smaller than a full fusion. The [Reducing Tokens](../guides/reducing-tokens.md) guide explains each reduction level and when to use it.

## Create A Config File

To avoid repeating options, create a `fuse.json` in your project root:

```bash
fuse init
```

Fuse reads this file on every run from that directory. [Configuration Files](../guides/configuration.md) covers the supported keys and precedence.

## What This Does Not Cover

This page does not cover scoping a fusion to a feature area, connecting the MCP server, or the full option set. Those follow in the guides and reference sections.

## Next

Continue to [Core Concepts](core-concepts.md) to understand the four-stage pipeline and the vocabulary the rest of the documentation uses.
