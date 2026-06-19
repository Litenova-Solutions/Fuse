# MCP Tool Catalog

Fuse exposes six MCP tools and five resource URI patterns. All tools use in-memory emission and return structured content directly in the tool response.

Start the server:

```bash
fuse serve
```

Install the global tool first:

```bash
dotnet tool install -g Fuse
```

Client setup: [mcp-integration.md](mcp-integration.md). Agent workflows: [agentic-workflows.md](agentic-workflows.md).

---

## Tool overview

| Tool | When to use | CLI equivalent |
|------|-------------|----------------|
| `fuse_skeleton` | Cold-start architecture review | `fuse dotnet --skeleton --all` |
| `fuse_focus` | Drill into a type, file, or area | `fuse dotnet --focus {seed} --depth N` |
| `fuse_search` | Natural-language or keyword discovery | `fuse dotnet --query {q} --query-top N` |
| `fuse_changes` | PR or branch diff review | `fuse dotnet --changed-since {ref}` |
| `fuse_dotnet` | Full control, all options combined | `fuse dotnet` |
| `fuse_generic` | Non-.NET or template-based fusion | `fuse` with template |

Recommended workflow: skeleton, then focus or search, then changes for PR review. See server instructions emitted by `fuse serve`.

---

## fuse_skeleton

Emits structural skeleton only (signatures, no bodies) for a .NET codebase. Optimized for low-token architecture review.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `path` | string | required | Absolute or relative path to the source directory |
| `excludeDirectories` | string[] | null | Directory names to skip |
| `excludeFiles` | string[] | null | File names to exclude |
| `excludePatterns` | string[] | null | Glob patterns to exclude |
| `excludeTestProjects` | bool | false | Exclude all test project directories |
| `semanticMarkers` | bool | false | Prepend structural annotation comments |
| `all` | bool | true | Apply all C# reduction options |
| `maxTokens` | int | null | Hard token limit |
| `trackTopTokenFiles` | bool | false | Append stats comment with top token consumers |

Example:

```
fuse_skeleton(path="./src", all=true, maxTokens=80000)
```

Example transcript:

```
Agent: I need to understand this codebase structure before making changes.
[calls fuse_skeleton(path="C:/Projects/MyApp/src", maxTokens=80000)]
Agent: The skeleton shows 47 types across 12 projects. OrderService in
src/Services/ looks like the entry point for order processing. I'll drill
into that area next.
[calls fuse_focus(path="...", focus="OrderService", depth=1)]
```

---

## fuse_focus

Scopes fusion to a type name, filename, or path plus dependency traversal.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `path` | string | required | Source directory |
| `focus` | string | required | Type name, filename, or path seed |
| `depth` | int | 1 | Dependency traversal depth (max 10) |
| `excludeDirectories` | string[] | null | Directory names to skip |
| `excludeFiles` | string[] | null | File names to exclude |
| `excludePatterns` | string[] | null | Glob patterns to exclude |
| `excludeTestProjects` | bool | false | Exclude test project directories |
| `all` | bool | false | Apply all C# reduction options |
| `maxTokens` | int | null | Hard token limit |
| `trackTopTokenFiles` | bool | false | Append stats comment |

Seed resolution order: relative path, filename, type name, directory prefix.

Example:

```
fuse_focus(path="./src", focus="OrderService", depth=2, maxTokens=150000)
```

Example transcript:

```
Agent: The skeleton showed OrderService depends on IPaymentGateway.
[calls fuse_focus(path="./src", focus="OrderService", depth=1, maxTokens=150000)]
Agent: Got OrderService.cs, IPaymentGateway.cs, PaymentGateway.cs, and
StripePaymentAdapter.cs. Enough context to implement the refund feature.
```

---

## fuse_search

BM25 query-scoped fusion. Ranks files by relevance to a query, then expands through the dependency graph.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `path` | string | required | Source directory |
| `query` | string | required | Natural-language or keyword query |
| `queryTop` | int | 10 | Number of top-ranked seed files |
| `depth` | int | 1 | Dependency traversal depth after seed selection |
| `excludeDirectories` | string[] | null | Directory names to skip |
| `excludeFiles` | string[] | null | File names to exclude |
| `excludePatterns` | string[] | null | Glob patterns to exclude |
| `excludeTestProjects` | bool | false | Exclude test project directories |
| `all` | bool | false | Apply all C# reduction options |
| `maxTokens` | int | null | Hard token limit |
| `trackTopTokenFiles` | bool | false | Append stats comment |

Example:

```
fuse_search(path="./src", query="authentication middleware JWT", queryTop=10, depth=1)
```

Example transcript:

```
Agent: I need to find where JWT validation happens.
[calls fuse_search(path="./src", query="JWT token validation middleware", queryTop=8)]
Agent: Top hits include JwtMiddleware.cs, AuthExtensions.cs, and
TokenValidationParameters.cs. Dependency expansion pulled in ITokenService.
```

---

## fuse_changes

Change-scoped fusion for PR review. Returns files changed since a git ref plus optional dependents.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `path` | string | required | Source directory (must be a git repo) |
| `changedSince` | string | required | Git ref: branch, commit, HEAD~N |
| `includeDependents` | bool | true | Include first-degree dependents |
| `excludeDirectories` | string[] | null | Directory names to skip |
| `excludeFiles` | string[] | null | File names to exclude |
| `excludePatterns` | string[] | null | Glob patterns to exclude |
| `excludeTestProjects` | bool | false | Exclude test project directories |
| `all` | bool | false | Apply all C# reduction options |
| `maxTokens` | int | null | Hard token limit |
| `trackTopTokenFiles` | bool | false | Append stats comment |

Example:

```
fuse_changes(path="./src", changedSince="main", includeDependents=true, maxTokens=100000)
```

Example transcript:

```
Agent: Review the changes on this feature branch.
[calls fuse_changes(path="./src", changedSince="origin/main", maxTokens=100000)]
Agent: 8 files changed. OrderService.cs and OrderServiceTests.cs modified.
Dependent IPaymentGateway.cs included via dependency expansion.
```

---

## fuse_dotnet

Full-control .NET fusion with all options available in one call. Use when workflow tools are too narrow or you need to combine flags.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `path` | string | required | Source directory |
| `excludeDirectories` | string[] | null | Directory names to skip |
| `excludeFiles` | string[] | null | File names to exclude |
| `excludePatterns` | string[] | null | Glob patterns to exclude |
| `includeExtensions` | string[] | null | Extensions added to DotNet defaults |
| `excludeExtensions` | string[] | null | Extensions removed from DotNet defaults |
| `onlyExtensions` | string[] | null | Extensions exclusively |
| `maxFileSizeKb` | int | 0 | Max file size in KB (0 = unlimited) |
| `excludeTestProjects` | bool | false | Exclude all test project directories |
| `excludeUnitTestProjects` | bool | false | Exclude only unit test directories |
| `removeCSharpComments` | bool | false | Remove C# comments |
| `removeCSharpUsings` | bool | false | Remove C# using directives |
| `removeCSharpNamespaces` | bool | false | Remove C# namespace declarations |
| `removeCSharpRegions` | bool | false | Remove C# region directives |
| `aggressive` | bool | false | Aggressive C# reduction |
| `all` | bool | false | Set all reduction options to true |
| `skeleton` | bool | false | Emit structural skeleton only |
| `semanticMarkers` | bool | false | Prepend structural annotation comments |
| `focus` | string | null | Type name, filename, or path to scope around |
| `depth` | int | 1 | Dependency traversal depth |
| `changedSince` | string | null | Git ref to scope to changed files |
| `includeDependents` | bool | true | Include dependents of changed files |
| `query` | string | null | BM25 query to scope fusion |
| `queryTop` | int | 10 | Top-ranked seed files for query scoping |
| `patternSummary` | bool | false | Append cross-codebase pattern summary |
| `maxTokens` | int | null | Hard token limit |
| `trackTopTokenFiles` | bool | false | Append stats comment |
| `gitStats` | bool | false | Include git churn stats in manifest |

Note: `focus`, `changedSince`, and `query` are mutually exclusive. Combining two or more returns a validation error.

Example:

```
fuse_dotnet(path="./src", all=true, patternSummary=true, excludeTestProjects=true, maxTokens=200000)
```

---

## fuse_generic

Template-based fusion for any supported project type.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `path` | string | required | Source directory |
| `template` | string | null | Template name: Python, Go, Rust, etc. |
| `excludeDirectories` | string[] | null | Directory names to skip |
| `excludeFiles` | string[] | null | File names to exclude |
| `excludePatterns` | string[] | null | Glob patterns to exclude |
| `includeExtensions` | string[] | null | Extensions added to template defaults |
| `excludeExtensions` | string[] | null | Extensions removed from template defaults |
| `onlyExtensions` | string[] | null | Extensions exclusively |
| `maxFileSizeKb` | int | 0 | Max file size in KB |
| `excludeTestProjects` | bool | false | Exclude test project directories |
| `changedSince` | string | null | Git ref to scope to changed files |
| `includeDependents` | bool | true | Include dependents of changed files |
| `maxTokens` | int | null | Hard token limit |
| `trackTopTokenFiles` | bool | false | Append stats comment |
| `gitStats` | bool | false | Include git churn stats in manifest |

Template names match the `ProjectTemplate` enum (case-insensitive). See [templates.md](templates.md).

Example:

```
fuse_generic(path="./api", template="Python", maxTokens=100000)
```

---

## MCP resources

Passive reads via the `fuse://` URI scheme. Resources use default reduction options and no token limits.

| URI pattern | Equivalent tool | Description |
|-------------|-----------------|-------------|
| `fuse://skeleton/{path}` | `fuse_skeleton` | Skeleton overview |
| `fuse://focus/{path}/{seed}` | `fuse_focus` | Focus-scoped content (depth 1) |
| `fuse://search/{path}/{query}` | `fuse_search` | Query-scoped content (top 10, depth 1) |
| `fuse://changes/{path}/{since}` | `fuse_changes` | Change-scoped content |
| `fuse://{template}/{path}` | `fuse_generic` | Template-based fusion with defaults |

Examples:

- `fuse://skeleton/C:/Projects/MyApp/src`
- `fuse://focus/C:/Projects/MyApp/src/OrderService`
- `fuse://search/C:/Projects/MyApp/src/authentication`
- `fuse://changes/C:/Projects/MyApp/src/main`
- `fuse://dotnet/C:/Projects/MyApp/src`
- `fuse://python/C:/Projects/my-api`

Prefer tools over resources when you need token limits, reduction flags, or exclusion control.

---

## Response format

Tool responses return XML-formatted file content by default:

```xml
<!-- fuse:manifest ... -->
<file path="src/Services/OrderService.cs">
public class OrderService { }
</file>
```

When `trackTopTokenFiles` is true, a stats comment is appended:

```xml
<!-- fuse: 47/52 files | ~84k tokens | 2.3s | top: OrderService.cs (12k) -->
```

Error responses return plain text starting with `Error:`.

---

## Agent guidelines

1. Start with `fuse_skeleton` on unfamiliar .NET codebases.
2. Use `fuse_focus` when you know the type or file; use `fuse_search` when you have a topic but not a name.
3. Use `fuse_changes` for PR review, not full codebase reads.
4. Set `maxTokens` to fit your remaining context budget (100000-200000 is a reasonable starting range).
5. Pass `excludeTestProjects: true` unless the task involves tests.
6. Use absolute paths when possible; relative paths resolve from the MCP server's working directory.
7. Concurrent tool calls are safe; the orchestrator is stateless.

Example agent prompt snippet:

```
When you need to read a codebase, use Fuse MCP tools instead of reading
files individually. For .NET projects: start with fuse_skeleton, then
fuse_focus or fuse_search, then fuse_changes for PR review. Set maxTokens
to fit your context budget. Exclude test projects unless tests are relevant.
```

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Directory not found | Invalid or relative path from wrong cwd | Pass an absolute path |
| Unknown template | Typo in template name | Use enum names from [templates.md](templates.md) |
| No files found | Empty directory or aggressive exclusions | Check exclusions and template extensions |
| Server not starting | `fuse` not on PATH | Install `Fuse` global tool |
| Empty response | All files filtered as trivial or binary | Check `maxFileSizeKb` and exclusions |
| Git not available | Git not installed for change scoping | Install git or avoid `fuse_changes` |
| Not a git repository | Source path is not a git repo | Run from repo root |
| Validation error | focus + query or focus + changedSince combined | Use one scoping mode at a time |
