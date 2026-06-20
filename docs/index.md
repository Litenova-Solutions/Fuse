---
title: Fuse Documentation
description: Documentation for Fuse, the .NET-native context optimizer for AI-assisted development.
---

Fuse collects the source files of a .NET codebase, reduces them for token efficiency, and emits a single structured payload an AI agent or a developer can consume in one call instead of reading thousands of files individually. It ships as a .NET global command and as a Model Context Protocol (MCP) server for clients such as Claude Code, Cursor, and GitHub Copilot.

This documentation is organized into a learning track you can read top to bottom and a reference track you consult for exact detail.

## Start Here

| If you want to... | Go to |
|-------------------|-------|
| Understand what Fuse is and why it exists | [Introduction](getting-started/introduction.md) |
| Install the tool and run it | [Installation](getting-started/installation.md), then [Your First Fusion](getting-started/first-fusion.md) |
| Learn the vocabulary and pipeline | [Core Concepts](getting-started/core-concepts.md) |
| Connect Fuse to an AI agent | [MCP Overview and Setup](agent-integration/overview.md) |
| Look up a command or flag | [Commands](reference/commands.md), [Options](reference/options.md) |
| Understand the internals | [Architecture](architecture/pipeline.md) |
| Add a language, template, or reducer | [Extending Fuse](extending/language-plugin.md) |

## Sections

- [Getting Started](getting-started/introduction.md): install, first run, and the mental model.
- [Guides](guides/fusing-dotnet.md): task-oriented walkthroughs for reduction, scoping, formats, and configuration.
- [Agent Integration](agent-integration/overview.md): the MCP server, its tools and resources, and recommended agent workflows.
- [Reference](reference/commands.md): every command, flag, template, reducer, and output format.
- [Architecture](architecture/pipeline.md): the four-stage pipeline, capability model, and scoping internals.
- [Extending Fuse](extending/language-plugin.md): add languages, templates, reducers, and pattern detectors.
- [Project](project/performance.md): performance, roadmap, contributing, and changelog.

## Next

New to Fuse? Begin with the [Introduction](getting-started/introduction.md).
