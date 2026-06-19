# MCP Integration

Fuse can run as a Model Context Protocol (MCP) server, allowing AI agents to generate optimized codebase context on demand. The MCP surface in Fuse 2.0 replaces the legacy `get_optimized_context` tool with two full-parity tools: `fuse_dotnet` and `fuse_generic`.

---

## Starting the Server

```bash
fuse serve
```

This starts a persistent process that communicates via stdio using JSON-RPC. All logging is redirected to stderr. stdout carries only MCP protocol messages.

The server advertises these instructions to connected agents:

> Fuse is a codebase context optimizer for AI-assisted workflows. Use fuse_dotnet for .NET/C# projects (skeleton, focus, change scoping, pattern summary) or fuse_generic for other templates.

---

## Client Configuration

### VS Code / GitHub Copilot

Add to your MCP settings (`.vscode/mcp.json` or user-level settings):

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

The `fuse` command must be on your PATH. Install via `dotnet tool install -g Fuse` or from source per [getting-started.md](getting-started.md).

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

### Cursor

Add to your MCP configuration:

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

## MCP Tools

Both tools always use in-memory emission (`FusionRequest.InMemory = true`). They return XML-formatted file content directly in the tool response, not as disk files.

### fuse_dotnet

Equivalent to `fuse dotnet`. Generates optimized .NET codebase context.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `path` | `string` | required | Absolute or relative path to the source directory |
| `excludeDirectories` | `string[]?` | `null` | Directory names to skip |
| `excludeFiles` | `string[]?` | `null` | File names to exclude |
| `excludePatterns` | `string[]?` | `null` | Glob patterns to exclude |
| `includeExtensions` | `string[]?` | `null` | Extensions added to DotNet template defaults |
| `excludeExtensions` | `string[]?` | `null` | Extensions removed from DotNet template defaults |
| `onlyExtensions` | `string[]?` | `null` | Extensions exclusively; ignores template defaults |
| `maxFileSizeKb` | `int` | `0` | Max file size in KB; 0 = unlimited |
| `excludeTestProjects` | `bool` | `false` | Exclude all test project directories |
| `excludeUnitTestProjects` | `bool` | `false` | Exclude only unit test directories |
| `removeCSharpComments` | `bool` | `false` | Remove C# comments |
| `removeCSharpUsings` | `bool` | `false` | Remove C# using directives |
| `removeCSharpNamespaces` | `bool` | `false` | Remove C# namespace declarations |
| `removeCSharpRegions` | `bool` | `false` | Remove C# region directives |
| `aggressive` | `bool` | `false` | Aggressive C# reduction |
| `all` | `bool` | `false` | Set all reduction options to true |
| `skeleton` | `bool` | `false` | Emit structural skeleton only (signatures, no bodies) |
| `semanticMarkers` | `bool` | `false` | Prepend structural annotation comments per type |
| `focus` | `string?` | `null` | Type name, filename, or path to scope around |
| `depth` | `int` | `1` | Dependency traversal depth for focus scoping |
| `changedSince` | `string?` | `null` | Git ref to scope to changed files |
| `includeDependents` | `bool` | `true` | Include first-degree dependents of changed files |
| `patternSummary` | `bool` | `false` | Append cross-codebase pattern summary |
| `maxTokens` | `int?` | `null` | Hard token limit |
| `trackTopTokenFiles` | `bool` | `false` | Include top files in stats comment |

### fuse_generic

Equivalent to the root `fuse` command with an optional template. Generates optimized context for any supported template.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `path` | `string` | required | Absolute or relative path to the source directory |
| `template` | `string?` | `null` | Template name: Python, JavaScript, TypeScript, Go, Rust, Java, etc. |
| `excludeDirectories` | `string[]?` | `null` | Directory names to skip |
| `excludeFiles` | `string[]?` | `null` | File names to exclude |
| `excludePatterns` | `string[]?` | `null` | Glob patterns to exclude |
| `includeExtensions` | `string[]?` | `null` | Extensions added to template defaults |
| `excludeExtensions` | `string[]?` | `null` | Extensions removed from template defaults |
| `onlyExtensions` | `string[]?` | `null` | Extensions exclusively |
| `maxFileSizeKb` | `int` | `0` | Max file size in KB; 0 = unlimited |
| `excludeTestProjects` | `bool` | `false` | Exclude all test project directories |
| `changedSince` | `string?` | `null` | Git ref to scope to changed files |
| `includeDependents` | `bool` | `true` | Include first-degree dependents of changed files |
| `maxTokens` | `int?` | `null` | Hard token limit |
| `trackTopTokenFiles` | `bool` | `false` | Include top files in stats comment |

Template names match the `ProjectTemplate` enum values (case-insensitive): `DotNet`, `Python`, `JavaScript`, `TypeScript`, `Go`, `Rust`, `Java`, `Infrastructure`, `AzureDevOpsWiki`, and others. See [templates.md](templates.md) for the full list.

---

## MCP Resources

Fuse exposes passive resources via the `fuse://` URI scheme.

| URI Pattern | Description |
|-------------|-------------|
| `fuse://{template}/{path}` | Read fused content for a given template and directory path |

Examples:

- `fuse://dotnet/C:/Projects/MyApp/src`
- `fuse://python/C:/Projects/my-api`
- `fuse://generic/C:/Projects/mixed-repo`

Resources use default reduction options. No token limits or C# reduction flags apply unless you use the tools instead.

The `template` segment accepts any `ProjectTemplate` enum name or `generic` for no template preset.

---

## Response Format

Tool responses return XML-formatted content:

```xml
<file path="src/Services/OrderService.cs">
// reduced content
</file>
<file path="src/Models/Order.cs">
// reduced content
</file>
```

When `trackTopTokenFiles` is `true`, a stats comment is appended:

```xml
<!-- fuse: 47/52 files | ~84k tokens | 2.3s | top: OrderService.cs (12k), Order.cs (8k) -->
```

The stats comment appears only when `trackTopTokenFiles` is enabled.

Error responses return plain text starting with `Error:`.

---

## Agentic Workflow Patterns

### 1. Cold-start architecture review (skeleton mode)

```
fuse_dotnet(path="./src", skeleton=true, all=true, maxTokens=100000)
```

Use when onboarding to a large .NET codebase. Skeleton mode emits type and method signatures only, typically reducing token count by 80-90% versus full fusion.

### 2. Targeted feature work (focus scoping)

```
fuse_dotnet(path="./src", focus="OrderService", depth=1, maxTokens=150000)
```

Scopes output to `OrderService` plus files that define types it references. Dependency scoping is regex-based (best-effort, not Roslyn).

### 3. PR review (change scoping)

```
fuse_dotnet(path="./src", changedSince="main", includeDependents=true, maxTokens=100000)
```

Restricts output to files changed since `main` plus first-degree dependents.

### 4. Convention discovery (pattern summary)

```
fuse_dotnet(path="./src", patternSummary=true, maxTokens=200000)
```

Appends a `<!-- fuse:patterns ... -->` block detecting DI registration, logging, CQRS, repository patterns, and more.

---

## Agent Instructions

When configuring an AI agent to use Fuse, include these guidelines:

1. **Choose the right tool.** Use `fuse_dotnet` for .NET/C# projects. Use `fuse_generic` with a `template` parameter for other languages.

2. **Prefer tools over resources for control.** Tools expose all filtering and reduction options. Resources use defaults only.

3. **Set token limits for large codebases.** Pass `maxTokens` to avoid exceeding the agent's context window. A value of 100000 to 200000 is a reasonable starting point for analysis tasks.

4. **Use `all` for review tasks.** When the agent needs to understand code structure rather than exact syntax, `fuse_dotnet` with `all: true` maximizes token savings.

5. **Exclude noise.** Pass `excludeTestProjects: true` when test code is not relevant. Use `excludePatterns` for generated or vendor code not covered by template defaults.

6. **Check the stats comment.** When `trackTopTokenFiles` is enabled, the stats line shows which files dominate token usage. Consider excluding or further reducing those files.

7. **Path must exist.** Both tools resolve paths with `Path.GetFullPath`. Relative paths are resolved from the MCP server's working directory, not the agent's.

8. **Concurrent calls are safe.** Fuse 2.0 uses a stateless orchestrator and unique temp files. Multiple simultaneous tool invocations do not corrupt output.

### Example Agent Prompt Snippet

```
When you need to read a codebase, call the fuse_dotnet or fuse_generic MCP tool
instead of reading files individually. For .NET projects use fuse_dotnet with
all=true for maximum context efficiency. Set maxTokens to fit within your
remaining context budget. Exclude test projects unless the task involves tests.
```

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---------|--------------|-----|
| `Directory not found` | Invalid or relative path from wrong working directory | Pass an absolute path |
| `Unknown template` | Typo in template name | Use enum names from [templates.md](templates.md) |
| `No files found` | Empty directory or overly aggressive exclusions | Check exclusions and template extensions |
| Server not starting | `fuse` not on PATH | Install global tool or use full path in MCP config |
| Empty response | All files filtered as trivial or binary | Check `maxFileSizeKb` and exclusion settings |
| `Git is not available on PATH` | Git not installed for change scoping | Install git or avoid `changedSince` |
| `not a git repository` | Source path is not a git repo | Run from repo root or drop change scoping |
| Git diff errors | Invalid ref in `changedSince` | Verify branch/commit name |
