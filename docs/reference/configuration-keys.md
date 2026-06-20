---
title: Configuration Keys
description: The keys Fuse reads from fuse.json or .fuserc, their discovery order, and how they combine with command-line flags.
---

A configuration file lets a project set fusion defaults once instead of repeating flags on every run. Fuse reads these defaults from `fuse.json` or `.fuserc`, merges them with the command-line flags, and applies built-in defaults for anything left unspecified. This page lists every supported key, where Fuse looks for the file, and how a key's value combines with a flag of the same meaning.

This page is for engineers setting project defaults and for maintainers who need the exact discovery and precedence rules. For a task-oriented walkthrough, see the [Configuration Files](../guides/configuration.md) guide.

## Purpose And Scope

This page documents the configuration file: its discovery, its keys, and its precedence against flags. It does not document the flags themselves; each key corresponds to a flag whose full behavior is in the [Options reference](options.md). All keys are optional.

## Discovery

Fuse searches for a configuration file starting in the working directory and walking up through parent directories to the filesystem root. The first match wins. Within a single directory, `fuse.json` takes precedence over `.fuserc`. A file that fails to parse is ignored silently: Fuse continues as if no configuration file were present, falling back to flags and built-in defaults.

## Precedence

For any setting, Fuse resolves the value in this order, highest first:

1. An explicit command-line flag.
2. The value from the discovered configuration file.
3. The built-in default.

An explicit flag always overrides the configuration file, and the configuration file overrides the built-in default. A key omitted from the file leaves the flag or default in effect.

## Supported Keys

| Key | Type | Maps to flag | Description |
|-----|------|--------------|-------------|
| `directory` | string | `--directory` | Source directory to fuse. |
| `output` | string | `--output` | Output directory for fused files. |
| `name` | string | `--name` | Output file name without extension. |
| `format` | xml \| markdown \| json | `--format` | Output format. |
| `tokenizer` | string | `--tokenizer` | Tokenizer model or encoding name. |
| `noManifest` | bool | `--no-manifest` | Omit the manifest header when `true`. |
| `provenance` | bool | `--provenance` | Enable dependency inclusion provenance annotations. |
| `gitStats` | bool | `--git-stats` | Include git churn and last-modified statistics in the manifest. |
| `maxTokens` | int | `--max-tokens` | Hard token limit. |
| `splitTokens` | int | `--split-tokens` | Per-part split threshold in tokens. |
| `recursive` | bool | `--recursive` | Search subdirectories recursively. |
| `includeMetadata` | bool | `--include-metadata` | Include file metadata in output entries. |

The full behavior, type conventions, and defaults for each corresponding flag are in the [Options reference](options.md).

## The Scaffold From fuse init

Running `fuse init` writes the following `fuse.json` to the current directory when one is absent:

```json
{
  "directory": ".",
  "output": "./fuse-output",
  "format": "xml",
  "tokenizer": "o200k_base",
  "noManifest": false,
  "provenance": false
}
```

These values are a starting point, not a required set. Edit, remove, or add keys from the table above as needed; any key you remove falls back to its built-in default.

## What This Does Not Cover

This page does not document the flags' full semantics (see [Options reference](options.md)), the `fuse init` command (see [Commands](commands.md)), or the output formats (see [Output Specification](output-specification.md)).

## Next

Continue to the [Configuration Files](../guides/configuration.md) guide for a worked example, or to the [Options reference](options.md) for full flag semantics.
