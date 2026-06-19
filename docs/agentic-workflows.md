# Agentic Workflows

Fuse 2.0 agentic features help AI agents work with large codebases without exhausting context windows. This guide covers when and how to use each feature.

---

## The Cold-Start Problem

Agents reading files one-by-one against a million-token codebase fail before reaching core logic. Fuse compresses and scopes context so agents can start with architecture, drill into features, or review PRs efficiently.

---

## Skeleton Mode

**When:** First pass on an unfamiliar .NET solution.

**CLI:** `fuse dotnet --directory ./src --all --skeleton`

**MCP:** `fuse_dotnet(path="./src", skeleton=true, all=true)`

**Expected tokens:** A full `--all` run (~800k on large solutions) often drops to ~40-100k with skeleton.

**Limitations:** C# only. Non-C# files pass through normal reduction. No method bodies, fields, or XML docs.

---

## Semantic Markers

**When:** Agents need quick type-level context without parsing full files.

**Format:**
```xml
<!-- fuse:type OrderService | kind:class | implements:IOrderService | depends-on:IPaymentGateway | constructors:IPaymentGateway -->
```

**CLI:** `--semantic-markers`

**MCP:** `semanticMarkers=true`

Combine with skeleton for minimal tokens plus structural hints.

---

## Dependency Scoping (Focus)

**When:** You know the feature area (type name, file, or folder) and need full content plus related code.

**Seed syntax (tried in order):**
1. Relative path: `Services/OrderService.cs`
2. Filename: `OrderService.cs`
3. Type name: `OrderService`
4. Directory prefix: `Payments`

**CLI:** `--focus OrderService --depth 1`

**MCP:** `focus="OrderService", depth=1`

**Caveat:** Regex-based dependency graph. Not Roslyn-accurate. Depth 1 = direct dependencies; max depth 10.

**Recommended `maxTokens`:** 100000-200000

---

## Change Scoping

**When:** PR review or incremental work against a git branch.

**CLI:** `--changed-since main --include-dependents`

**MCP:** `changedSince="main", includeDependents=true`

**Git ref syntax:** Branch names, commit SHAs, `HEAD~1`, etc.

**Requires:** Git on PATH; source directory must be a git repository.

---

## Pattern Summary

**When:** Implementing a new feature and need to match existing conventions.

**Detects:** DI registration, exception handling, logging, async patterns, CQRS/MediatR, repository pattern.

**CLI:** `--pattern-summary`

**MCP:** `patternSummary=true`

Output is appended as `<!-- fuse:patterns ... -->` in MCP responses or disk output.

---

## Composing Features

| Combination | Useful? | Notes |
|-------------|---------|-------|
| skeleton + all | Yes | Primary architecture workflow |
| skeleton + semanticMarkers | Yes | Markers generated from skeleton content |
| focus + changedSince | No | Validator rejects; pick one |
| patternSummary + skeleton | Yes | Conventions + structure |
| focus + patternSummary | Rare | Focus already limits scope |

---

## Token Budget Guidance

| Workflow | Suggested maxTokens |
|----------|---------------------|
| Skeleton overview | 50000-100000 |
| Focus scoping | 100000-200000 |
| PR change review | 50000-150000 |
| Full reduction (--all) | 200000-800000 |
| Pattern summary add-on | +5000 overhead |

---

## MCP Quick Reference

```
# Architecture pass
fuse_dotnet(path="...", skeleton=true, all=true, maxTokens=80000)

# Feature drill-down
fuse_dotnet(path="...", focus="MyService", depth=2, maxTokens=150000)

# PR review
fuse_dotnet(path="...", changedSince="origin/main", maxTokens=100000)

# Conventions
fuse_dotnet(path="...", patternSummary=true, maxTokens=200000)
```

See [mcp-integration.md](mcp-integration.md) for full parameter tables.
