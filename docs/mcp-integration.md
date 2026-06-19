# MCP Integration

Fuse runs as a Model Context Protocol (MCP) server, allowing AI agents to generate optimized codebase context on demand. Version 2.0 replaces the legacy single tool with six focused tools and five resource URI patterns.

**Tool catalog and parameters:** [mcp.md](mcp.md)

**Agent workflows:** [agentic-workflows.md](agentic-workflows.md)

---

## Starting the server

```bash
fuse serve
```

Communicates via stdio using JSON-RPC. All logging goes to stderr; stdout carries only MCP protocol messages.

Install the global tool first:

```bash
dotnet tool install -g Fuse
```

Or run without a global install:

```bash
dnx Fuse -- serve
```

The server advertises workflow instructions to connected agents (skeleton, focus, search, changes, full control).

---

## Client configuration

The `fuse` command must be on your PATH, or use the full path in MCP config.

### Claude Code

Add to `.mcp.json` in your project root:

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

Or: `claude mcp add fuse --scope project -- fuse serve`

### Cursor

Add to `.cursor/mcp.json`:

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

### GitHub Copilot (VS Code)

Add to `.vscode/mcp.json`:

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

### Claude Desktop

Add to `claude_desktop_config.json`:

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

---

## Tools at a glance

| Tool | Purpose |
|------|---------|
| `fuse_skeleton` | Architecture review (skeleton only) |
| `fuse_focus` | Dependency scoping around a seed |
| `fuse_search` | BM25 query scoping |
| `fuse_changes` | Git diff scoping |
| `fuse_dotnet` | Full-control .NET fusion |
| `fuse_generic` | Template-based fusion |

Full parameter tables, example transcripts, and response format: [mcp.md](mcp.md).

---

## Resources

Passive reads via `fuse://` URIs:

| URI | Purpose |
|-----|---------|
| `fuse://skeleton/{path}` | Skeleton overview |
| `fuse://focus/{path}/{seed}` | Focus-scoped content |
| `fuse://search/{path}/{query}` | Query-scoped content |
| `fuse://changes/{path}/{since}` | Change-scoped content |
| `fuse://{template}/{path}` | Template fusion with defaults |

Prefer tools when you need token limits, reduction flags, or exclusions.

---

## Agent guidelines

1. Start with `fuse_skeleton` on unfamiliar .NET codebases.
2. Use `fuse_focus` when you know the type or file; use `fuse_search` for topic discovery.
3. Use `fuse_changes` for PR review.
4. Set `maxTokens` to fit your context budget (100000-200000 is a reasonable range).
5. Pass `excludeTestProjects: true` unless tests are relevant.
6. Use absolute paths when possible.
7. Concurrent tool calls are safe.

Example prompt snippet:

```
When you need to read a codebase, use Fuse MCP tools instead of reading
files individually. For .NET: fuse_skeleton first, then fuse_focus or
fuse_search, then fuse_changes for PR review. Set maxTokens to fit your
context budget.
```

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Directory not found | Invalid or relative path | Pass an absolute path |
| Unknown template | Typo in template name | See [templates.md](templates.md) |
| No files found | Empty directory or aggressive exclusions | Check exclusions |
| Server not starting | `fuse` not on PATH | Install `Fuse` |
| Empty response | All files filtered | Check `maxFileSizeKb` and exclusions |
| Git errors | Git missing or not a repo | Install git or avoid change scoping |
| Validation error | Combined focus + query or focus + changes | Use one scoping mode |

---

## MCP Registry

Fuse publishes a server manifest for MCP Registry discovery: [mcp-registry/server.json](mcp-registry/server.json).

Package: `Fuse` on NuGet. Runtime hint: `dnx Fuse -- serve`.
