# Fuse Features

Reference for all Fuse 2.0 capabilities, grouped by implementation tier and pipeline stage.

---

## Core pipeline

These features apply to every fusion run regardless of template or language.

### Collection and filtering

- Recursive directory scan with `.gitignore` support
- 26 project templates with default extensions and exclusions ([templates.md](templates.md))
- Extension overrides: `--include-extensions`, `--exclude-extensions`, `--only-extensions`
- Directory, filename, and glob exclusions
- Binary detection (null-byte heuristic), empty file skip, auto-generated marker skip
- Test project exclusion (`--exclude-test-projects`, `--exclude-unit-test-projects`)
- Max file size limit (`--max-file-size`)

### Reduction

- Whitespace trim and blank-line condensing (default on)
- Format reducers: HTML, Razor, XML, JSON, YAML, CSS, SCSS, JavaScript, Markdown
- C# reducers: comment/usings/namespace/region removal, aggressive compression (`--all`)
- Trivial content filter (`{}`, `[]`, whitespace-only) dropped before emission
- Language plugins resolve reducers by extension via `CapabilityRegistry<IContentReducer>`

### Emission

- XML (default), Markdown, or JSON output (`--format`)
- Token counting with selectable tokenizer (`--tokenizer`, default `o200k_base`)
- Hard token limit (`--max-tokens`) and multi-part splitting (`--split-tokens`)
- Descending token-count ordering (largest files first)
- Top token file reporting (`--track-top-token-files`)
- File metadata option (`--include-metadata`)
- Disk or in-memory output (MCP tools always in-memory)

---

## Performance (Phases 2-3)

### Parallel pipelines

Collection, reduction, and dependency graph building run with configurable parallelism:

```bash
fuse dotnet --directory ./src --parallelism 8
```

Default is processor count. Set `--parallelism 1` for serial execution.

### Single-read content provider

`ISourceContentProvider` reads each file once per run and shares content across graph building, query indexing, and reduction. Eliminates duplicate I/O from the pre-2.0 design.

### Reduction cache

Per-file reduction results cache in `.fuse/cache` keyed by content hash (XXHash64) and reduction options:

```bash
fuse dotnet --directory ./src          # cache on (default)
fuse dotnet --directory ./src --no-cache
fuse dotnet --directory ./src --clear-cache
```

Cache statistics appear in CLI output: `cache: N hit / M miss`.

### Watch mode

Re-run fusion when files change (debounced):

```bash
fuse dotnet --directory ./src --watch
```

Disabled automatically when stdin/stdout are redirected (MCP stdio).

---

## Tier 1: Trust and scoping (Phase 4)

### Secret redaction (default ON)

Before token counting, `ISecretRedactor` replaces detected secrets in place:

- AWS keys, generic API tokens, JWTs
- Private-key PEM headers, connection strings
- High-entropy literal heuristic

Replacements use `[REDACTED:<kind>]`. Surrounding code stays intact.

| Flag | Effect |
|------|--------|
| (default) | Redaction enabled |
| `--no-redact` | Disable redaction |
| `--redact-report` | Append redaction count summary |

Redaction state is included in cache keys. Toggling `--no-redact` produces distinct cache entries.

### BM25 query scoping

Natural-language or keyword query selects the most relevant files, then expands through the dependency graph:

```bash
fuse dotnet --directory ./src --query "order payment" --query-top 10 --depth 1
```

- Builds an inverted index over tokenized file content and identifiers
- Splits camelCase, PascalCase, and snake_case identifiers into subterms
- Ranks with BM25, takes top N seeds, expands via `FocusSeedResolver`
- Mutually exclusive with `--focus` and `--changed-since`

MCP: `fuse_search` tool.

### .NET structural maps

Opt-in emission addenda for .NET projects (regex-based, no Roslyn):

| Flag | Output |
|------|--------|
| `--route-map` | Compact verb/path/handler table from controllers and minimal APIs |
| `--public-api` | Skeleton with public and protected members only |
| `--project-graph` | Solution and `.csproj` reference dependency structure |

Maps prepend to output before file entries.

---

## Tier 2: Output intelligence (Phase 5)

### Manifest header (default ON)

Prepended before the first file entry:

- File tree with per-file token costs
- Pattern summary (when `--pattern-summary` is set)
- Git stats (when `--git-stats` is set)

Disable with `--no-manifest`.

### Inclusion provenance

When focus, query, or change scoping pulls files transitively, each entry can show why it was included:

```xml
<!-- included via: OrderService.cs -> IPaymentGateway.cs -> PaymentGateway.cs -->
```

Enable with `--provenance`. Provenance chains come from `FocusSeedResolver.ExpandPaths`.

### Selectable tokenizers

`TokenizerFactory` resolves encodings by model name:

```bash
fuse dotnet --directory ./src --tokenizer o200k_base
fuse dotnet --directory ./src --tokenizer cl100k_base
```

Default is `o200k_base` (GPT-4o class). Use `--tokenizer` to match your target model for accurate budget planning.

### Output format adapters

| Format | CLI | Structure |
|--------|-----|-----------|
| XML | `--format xml` (default) | `<file path="...">` blocks |
| Markdown | `--format markdown` | `## path` headers with fenced code |
| JSON | `--format json` | Manifest + file entries as JSON |

Manifest and provenance render in the chosen format.

### Project config files

`fuse.json` or `.fuserc` discovered upward from the working directory:

```bash
fuse init    # creates fuse.json scaffold
```

Precedence: explicit CLI flag > config file > built-in default.

Supported config keys: `directory`, `output`, `name`, `format`, `tokenizer`, `noManifest`, `provenance`, `gitStats`, `maxTokens`, `splitTokens`, `recursive`, `includeMetadata`.

---

## Tier 3: Agent surface (Phase 6)

### Focused MCP tools

Six tools replace the monolithic 1.x MCP surface:

| Tool | Purpose |
|------|---------|
| `fuse_skeleton` | Architecture review (skeleton only) |
| `fuse_focus` | Dependency scoping around a seed |
| `fuse_search` | BM25 query scoping |
| `fuse_changes` | Git diff scoping |
| `fuse_dotnet` | Full-control .NET fusion |
| `fuse_generic` | Template-based fusion for any language |

See [mcp.md](mcp.md) for parameters.

### MCP resources

Passive reads via `fuse://` URIs:

- `fuse://skeleton/{path}`
- `fuse://focus/{path}/{seed}`
- `fuse://search/{path}/{query}`
- `fuse://changes/{path}/{since}`
- `fuse://{template}/{path}`

Resources use default options. Prefer tools when you need token limits or reduction flags.

### Git enrichment

Optional per-file git stats in the manifest:

```bash
fuse dotnet --directory ./src --git-stats
```

Surfaces commit churn and last-modified date. Degrades gracefully outside a git repository.

---

## Agentic features (.NET)

These features target AI agent workflows on large .NET codebases.

### Skeleton mode

Emit type and method signatures only, no bodies. Typically 80-90% token reduction vs full fusion.

```bash
fuse dotnet --directory ./src --all --skeleton
```

C# only (via `CSharpSkeletonExtractor`). Other extensions pass through normal reduction.

### Semantic markers

Prepend structural annotation comments per type:

```xml
<!-- fuse:type OrderService | kind:class | implements:IOrderService -->
```

```bash
fuse dotnet --directory ./src --semantic-markers
```

### Focus scoping

Scope to a type name, filename, or directory plus dependency traversal:

```bash
fuse dotnet --directory ./src --focus OrderService --depth 1
```

Seed resolution order: relative path, filename, type name, directory prefix.

### Change scoping

Scope to files changed since a git ref:

```bash
fuse dotnet --directory ./src --changed-since main --include-dependents
```

Requires git on PATH and a git repository at the source directory.

### Pattern summary

Detect cross-codebase conventions and append a summary block:

```bash
fuse dotnet --directory ./src --pattern-summary
```

Detectors: DI registration, exception handling, logging, async patterns, CQRS/MediatR, repository pattern.

---

## Feature composition rules

| Combination | Valid? | Notes |
|-------------|--------|-------|
| `--focus` + `--changed-since` | No | Validator rejects |
| `--focus` + `--query` | No | Validator rejects |
| `--changed-since` + `--query` | No | Validator rejects |
| `--skeleton` + `--all` | Yes | Primary architecture workflow |
| `--skeleton` + `--semantic-markers` | Yes | Markers from skeleton content |
| `--pattern-summary` + `--skeleton` | Yes | Conventions plus structure |
| `--provenance` + any scoping mode | Yes | Shows inclusion chains |
| `--git-stats` + `--no-manifest` | Partial | Stats only appear in manifest |

---

## Suggested token budgets

| Workflow | Suggested `--max-tokens` |
|----------|--------------------------|
| Skeleton overview | 50000-100000 |
| Focus or query drill-down | 100000-200000 |
| PR change review | 50000-150000 |
| Full reduction (`--all`) | 200000-800000 |
| Pattern summary add-on | +5000 overhead |

See [agentic-workflows.md](agentic-workflows.md) for MCP examples.
