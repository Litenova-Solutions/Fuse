# Agentic Workflows

Fuse agentic features help AI agents work with large codebases without exhausting context windows. This guide covers when and how to use each feature, with MCP tool names for the 2.0 split-tool surface.

---

## The cold-start problem

Agents reading files one-by-one against a million-token codebase fail before reaching core logic. Fuse compresses and scopes context so agents can start with architecture, drill into features, or review PRs efficiently.

Recommended sequence: skeleton, then focus or search, then changes for PR review.

---

## Skeleton mode

**When:** First pass on an unfamiliar .NET solution.

**CLI:**

```bash
fuse dotnet --directory ./src --all --skeleton
```

**MCP:**

```
fuse_skeleton(path="./src", all=true, maxTokens=80000)
```

**Expected tokens:** A full `--all` run (~800k on large solutions) often drops to ~40-100k with skeleton.

**Notes:** Implemented via `CSharpSkeletonExtractor`. Non-C# files in the DotNet template pass through normal reduction. Combine with `--semantic-markers` for type-level hints without full bodies.

---

## Semantic markers

**When:** Agents need quick type-level context without parsing full files.

**Format:**

```xml
<!-- fuse:type OrderService | kind:class | implements:IOrderService | depends-on:IPaymentGateway -->
```

**CLI:** `--semantic-markers`

**MCP:** `semanticMarkers=true` on `fuse_skeleton` or `fuse_dotnet`

Combine with skeleton for minimal tokens plus structural hints.

---

## Dependency scoping (focus)

**When:** You know the feature area (type name, file, or folder) and need full content plus related code.

**Seed syntax (tried in order):**

1. Relative path: `Services/OrderService.cs`
2. Filename: `OrderService.cs`
3. Type name: `OrderService`
4. Directory prefix: `Payments`

**CLI:**

```bash
fuse dotnet --directory ./src --focus OrderService --depth 1
```

**MCP:**

```
fuse_focus(path="./src", focus="OrderService", depth=1, maxTokens=150000)
```

**Caveat:** Regex-based dependency graph, not Roslyn-accurate. Depth 1 = direct dependencies; max depth 10.

**Recommended maxTokens:** 100000-200000

---

## Query scoping (BM25)

**When:** You have a topic or question but not a specific type name.

**CLI:**

```bash
fuse dotnet --directory ./src --query "authentication JWT middleware" --query-top 10 --depth 1
```

**MCP:**

```
fuse_search(path="./src", query="authentication JWT middleware", queryTop=10, depth=1)
```

BM25 ranks files by relevance, takes the top N seeds, then expands through the dependency graph. Mutually exclusive with focus and change scoping.

**Recommended maxTokens:** 100000-200000

---

## Change scoping

**When:** PR review or incremental work against a git branch.

**CLI:**

```bash
fuse dotnet --directory ./src --changed-since main --include-dependents
```

**MCP:**

```
fuse_changes(path="./src", changedSince="main", includeDependents=true, maxTokens=100000)
```

**Git ref syntax:** Branch names, commit SHAs, `HEAD~1`, etc.

**Requires:** Git on PATH; source directory must be a git repository.

---

## Pattern summary

**When:** Implementing a new feature and need to match existing conventions.

**Detects:** DI registration, exception handling, logging, async patterns, CQRS/MediatR, repository pattern.

**CLI:** `--pattern-summary`

**MCP:** `patternSummary=true` on `fuse_dotnet`

Output appears in the manifest (default) or as an appended `<!-- fuse:patterns ... -->` block when manifest is disabled.

---

## Structural maps

**When:** You need a compact view of endpoints or project structure before reading implementation files.

| Flag | Output |
|------|--------|
| `--route-map` | Verb/path/handler table from ASP.NET controllers and minimal APIs |
| `--public-api` | Skeleton with public and protected members only |
| `--project-graph` | Solution and project reference graph |

**CLI example:**

```bash
fuse dotnet --directory ./src --route-map --project-graph --skeleton
```

These prepend to output. Useful as a first pass before focus scoping.

---

## Composing features

| Combination | Valid? | Notes |
|-------------|--------|-------|
| skeleton + all | Yes | Primary architecture workflow |
| skeleton + semanticMarkers | Yes | Markers from skeleton content |
| focus + changedSince | No | Validator rejects; pick one |
| focus + query | No | Validator rejects; pick one |
| query + changedSince | No | Validator rejects; pick one |
| patternSummary + skeleton | Yes | Conventions plus structure |
| provenance + any scoping mode | Yes | Shows why each file was included |
| routeMap + skeleton | Yes | Endpoints plus type structure |

---

## Token budget guidance

| Workflow | Suggested maxTokens |
|----------|---------------------|
| Skeleton overview | 50000-100000 |
| Focus scoping | 100000-200000 |
| Query scoping | 100000-200000 |
| PR change review | 50000-150000 |
| Full reduction (--all) | 200000-800000 |
| Pattern summary add-on | +5000 overhead |

---

## MCP quick reference

```
# 1. Architecture pass
fuse_skeleton(path="...", all=true, maxTokens=80000)

# 2a. Feature drill-down (known type)
fuse_focus(path="...", focus="MyService", depth=2, maxTokens=150000)

# 2b. Topic discovery (unknown type)
fuse_search(path="...", query="payment processing", queryTop=10, maxTokens=150000)

# 3. PR review
fuse_changes(path="...", changedSince="origin/main", maxTokens=100000)

# Full control when needed
fuse_dotnet(path="...", all=true, patternSummary=true, maxTokens=200000)
```

Full parameter tables: [mcp.md](mcp.md).

---

## Provenance and manifest

When using focus, query, or change scoping, enable provenance to see why transitive dependencies were included:

**CLI:** `--provenance`

**Manifest (default ON):** Shows file tree and token costs before file entries. Use `--no-manifest` to disable.

**Git stats:** `--git-stats` adds churn and last-modified to the manifest for prioritizing hot files.

These features help agents interpret scoped output without re-reading the full codebase.
