---
title: Installation
description: Install the Fuse global tool from NuGet, run it without installing via dnx, or build it from source.
---

Fuse is distributed as a .NET global tool named `Fuse`. Installing it puts a `fuse` command on your PATH that runs both the command-line interface and the MCP server. This page covers every supported installation path, the prerequisite runtime, and how to verify the result.

This page is for engineers setting up Fuse for the first time and for operators scripting its installation into a development environment.

## System Requirements

Fuse requires the [.NET SDK 10.0](https://dotnet.microsoft.com/download) or later. The SDK provides both the runtime that executes Fuse and the `dotnet tool` command used to install it. No other dependencies are required for the command-line tool. Git change scoping additionally requires the `git` executable on your PATH, which the [Scoping to What Matters](../guides/scoping.md) guide notes where relevant.

## Install From NuGet

The supported path for most users is a global tool install from NuGet:

```bash
dotnet tool install -g Fuse
```

This downloads the `Fuse` package and registers the `fuse` command for your user account. To update an existing install to the latest published version:

```bash
dotnet tool update -g Fuse
```

## Run Without Installing

The .NET 10 SDK can run a tool on demand without a global install using `dnx`, which fetches the package and executes it in one step:

```bash
dnx Fuse -- serve
```

This form suits continuous integration jobs and one-off runs where a persistent install is not wanted. The arguments after `--` are passed to Fuse exactly as they would be on the command line.

## Build From Source

To build from a clone of the repository, pack the CLI project and install from the local package source.

On Windows, a helper script performs both steps:

```cmd
install.bat
```

On any operating system, run the two commands directly:

```bash
dotnet pack src/Host/Fuse.Cli/Fuse.Cli.csproj -c Release
dotnet tool install -g Fuse --add-source src/Host/Fuse.Cli/nupkg
```

The first command produces a NuGet package under the project's `nupkg` directory. The second installs from that local source instead of NuGet.

## Verify The Install

Confirm the command resolves and reports its options:

```bash
fuse --help
```

A successful install prints the list of commands (`fuse`, `dotnet`, `wiki`, `init`, `serve`). If the shell reports that `fuse` is not found, the .NET global tools directory is not on your PATH. Restart the shell, or add the tools directory shown by `dotnet tool list -g` to PATH.

## Installation Paths Compared

| Method | Command | Use when |
|--------|---------|----------|
| NuGet global tool | `dotnet tool install -g Fuse` | Day-to-day local development |
| On-demand run | `dnx Fuse -- serve` | CI jobs, one-off runs, no persistent install |
| From source | `install.bat` or `dotnet pack` then install | Local development of Fuse itself, or unreleased changes |

## What This Does Not Cover

This page does not cover connecting the MCP server to an AI client; that is in [MCP Overview and Setup](../agent-integration/overview.md). It does not cover the `fuse.json` configuration file; that is in [Configuration Files](../guides/configuration.md).

## Next

Continue to [Your First Fusion](first-fusion.md) to produce output from a project.
