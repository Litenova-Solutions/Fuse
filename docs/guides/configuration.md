---
title: Configuration Files
description: How to set per-project defaults with fuse.json or .fuserc, and how Fuse resolves them.
---

A configuration file records the options a project uses on every fusion, so you set them once rather than typing them on each run. Fuse reads `fuse.json` (or `.fuserc`) from the working directory upward and merges its values with the command line. This guide explains how to create one, how Fuse finds it, and how its values combine with explicit flags.

This page is for engineers who run Fuse repeatedly against the same project and want stable defaults without a long command line.

## When This Applies

A configuration file applies whenever Fuse runs from a directory at or below the file's location. The file holds the same options that the command line exposes, expressed as JSON keys. A value in the file becomes the default for that option; an explicit flag on the command line still wins over it.

## Creating A Configuration File

Run `fuse init` to scaffold a `fuse.json` in the current directory. The command writes the file only when one does not already exist; if a `fuse.json` is present, it reports that and writes nothing.

```bash
fuse init
```

The scaffold contains a starting set of keys:

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

Edit these to match the project, then run `fuse dotnet` from that directory with no flags to use them.

## How Fuse Finds The File

Fuse searches for a configuration file starting in the working directory and moving up the directory tree to the filesystem root, stopping at the first file it finds. In any one directory, `fuse.json` takes precedence over `.fuserc`. This upward search means a file at the repository root applies to runs from any subdirectory, while a file in a subdirectory overrides it for runs there.

If a file is found but cannot be parsed, Fuse treats it as absent and falls back to command-line flags and built-in defaults rather than reporting an error.

## How Values Combine

Each option resolves through a fixed precedence:

1. An explicit flag on the command line.
2. The value in the configuration file.
3. The built-in default.

So a project can set `"format": "markdown"` in `fuse.json` and still produce XML for a single run by passing `--format xml`, without editing the file. An option absent from both the command line and the file uses its built-in default.

## Supported Keys

A configuration file accepts the same options the command line exposes, including the source directory, output location and name, format, tokenizer, token limits, and the manifest, provenance, git stats, recursion, and metadata toggles.

| Key | Sets |
|-----|------|
| `directory` | Source directory to fuse |
| `output` | Output directory |
| `format` | Output format: `xml`, `markdown`, or `json` |
| `tokenizer` | Tokenizer model or encoding |
| `noManifest` | Drop the manifest header when true |
| `provenance` | Annotate why each file was included |

This is a representative subset. The [Configuration Keys reference](../reference/configuration-keys.md) lists every supported key and the flag it corresponds to. For the flags themselves, see the [Options reference](../reference/options.md).

## What This Does Not Cover

This page does not document every configuration key or describe the options in detail; the reference pages above do. It also does not cover the scoping options, which a configuration file does not set.

## Next

Continue to [Choosing an Output Format](output-formats.md) to decide which format to make the project default, or see [Your First Fusion](../getting-started/first-fusion.md) for the run that produces output from these settings.
