---
title: MCP Overview and Setup
description: How to start the Fuse MCP server and connect it to Claude Code, Cursor, and GitHub Copilot.
---

Fuse exposes its fusion capabilities to AI clients through a Model Context Protocol server. MCP (Model Context Protocol) is the open protocol that lets an AI client call external tools, so connecting Fuse gives an agent a way to request scoped, reduced codebase context on demand instead of reading files one at a time. This page explains how to start that server and how to register it with the three clients Fuse documents.

This page is for engineers connecting Fuse to an AI client and for leads who want to confirm what the integration installs and how it communicates.

## Configuration Context

The server starts with `fuse serve`. It runs over stdio, which means it owns standard input and standard output for protocol traffic. All logging is routed to standard error, so standard output carries only protocol messages and the stream stays clean for the client. The server identifies itself as "fuse" version 2.0.0 and is built on the ModelContextProtocol SDK.

Every fusion the server runs happens in memory and returns its result in the tool or resource response. The MCP server writes no files, which is the one behavioral difference from the `fuse` command. Confirm the `fuse` command is installed before connecting a client; [Installation](../getting-started/installation.md) covers that step.

## Client Setup

Each client reads a JSON configuration file that names the server, the command to run, and its arguments. For Fuse the command is `fuse` and the arguments are `["serve"]` in every client. Place the file at the path each client expects.

### Claude Code

Add the server to `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "fuse": {
      "type": "stdio",
      "command": "fuse",
      "args": ["serve"]
    }
  }
}
```

The same registration is available as a one-line command:

```bash
claude mcp add fuse --scope project -- fuse serve
```

### Cursor

Add the server to `.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "fuse": {
      "command": "fuse",
      "args": ["serve"]
    }
  }
}
```

### GitHub Copilot in VS Code

Add the server to `.vscode/mcp.json`. Copilot reads its servers under a `servers` key rather than `mcpServers`:

```json
{
  "servers": {
    "fuse": {
      "type": "stdio",
      "command": "fuse",
      "args": ["serve"]
    }
  }
}
```

## Registry Discovery

Fuse publishes a server manifest for MCP Registry discovery, stored as the registry manifest under the project's docs. The manifest declares the package "Fuse" on NuGet with the runtime hint `dnx`, so a client that resolves servers through the registry can install and run Fuse without a prior global install.

## What This Does Not Cover

This page covers starting the server and registering it with a client. It does not document the individual tools, their parameters, or the resource URIs. For those, see [Tools Reference](tools.md) and [Resources Reference](resources.md). For the order in which to call the tools during a task, see [Recommended Workflows](workflows.md).

## Next

Continue to [Recommended Workflows](workflows.md) to learn the call sequence for a large .NET codebase, or go straight to [Tools Reference](tools.md) for the full parameter detail.
