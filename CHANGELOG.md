# Changelog

All notable changes to Fuse are documented here. Fuse 2.0 is a structural rewrite; backward compatibility with 1.x output is not a goal.

## [2.0.0] - 2026-06-19

### Breaking changes

- **Solution restructure.** Monolithic layers replaced by axis-based projects: Collection, Analysis, Reduction, Emission, Fusion, plus language plugins (`Fuse.Languages.CSharp`, `Fuse.Formats`).
- **MCP tools.** Legacy `get_optimized_context` removed. Replaced by `fuse_skeleton`, `fuse_focus`, `fuse_search`, `fuse_changes`, `fuse_dotnet`, and `fuse_generic`.
- **Default tokenizer.** Token counting now defaults to `o200k_base` (was `cl100k_base`). Counts will differ from 1.x for the same content.
- **Secret redaction default ON.** API keys, JWTs, connection strings, and high-entropy literals are redacted before token counting. Use `--no-redact` to disable.
- **Manifest header default ON.** Output is prefixed with a file tree and per-file token costs. Use `--no-manifest` to disable.
- **Emission ordering.** Files emit in descending token-count order (largest first) to help agents prioritize within a budget.

### Added

#### Core architecture (Phases 0-1)

- Language plugin model with `ILanguageCapability` and `CapabilityRegistry<T>`.
- `IEntryFormatter` with XML, Markdown, and JSON output formats (`--format`).
- `ISourceContentProvider` for single-read file access across pipeline stages.
- Registry-driven reduction, skeleton extraction, dependency extraction, and type location.

#### Performance (Phases 2-3)

- Parallel collection, reduction, and graph building (`--parallelism`, default: processor count).
- Per-file reduction cache in `.fuse/cache` (`--no-cache`, `--clear-cache`).
- Watch mode for iterative fusion (`--watch`; disabled under MCP stdio).

#### Cold start and Native AOT

- Token counting via `Microsoft.ML.Tokenizers` (`o200k_base`, `cl100k_base`).
- Source-generated JSON for config and JSON output (AOT/trim safe).
- Native AOT publish profiles and `Fuse.Runtime.{rid}` satellite packages; see [performance.md](performance.md).
- Windows installer ships AOT-compiled `fuse.exe`.

#### Tier 1 features (Phase 4)

- Secret redaction with `[REDACTED:<kind>]` placeholders (`--no-redact`, `--redact-report`).
- BM25 query-scoped fusion (`--query`, `--query-top`, `--depth`).
- .NET structural maps: route map (`--route-map`), public API skeleton (`--public-api`), project graph (`--project-graph`).

#### Tier 2 features (Phase 5)

- Manifest header with file tree, token costs, pattern summary, and optional git stats.
- Inclusion provenance annotations for dependency-expanded files (`--provenance`).
- Selectable tokenizers via `TokenizerFactory` (`--tokenizer`, default `o200k_base`).
- Project config discovery: `fuse.json` and `.fuserc` with flag > config > default precedence.
- `fuse init` command to scaffold config.

#### Tier 3 features (Phase 6)

- Focused MCP tools: `fuse_skeleton`, `fuse_focus`, `fuse_search`, `fuse_changes`.
- Git enrichment in manifest (`--git-stats`: churn and last-modified per file).
- MCP resources for skeleton, focus, search, and change workflows.

#### Agentic features (carried forward and extended)

- C# skeleton mode (`--skeleton`), semantic markers (`--semantic-markers`).
- Dependency-aware focus scoping (`--focus`, `--depth`).
- Git change scoping (`--changed-since`, `--include-dependents`).
- Cross-codebase pattern summary (`--pattern-summary`).

### Migration from Fuse 1.x

| Area | 1.x behavior | 2.0 behavior | Action |
|------|--------------|--------------|--------|
| MCP tool | `get_optimized_context` | Six focused tools (see [docs/mcp.md](docs/mcp.md)) | Update agent prompts and MCP config |
| Token counts | `cl100k_base` | `o200k_base` default | Re-baseline `--max-tokens` budgets; or pass `--tokenizer cl100k_base` |
| Output prefix | None | Manifest header | Use `--no-manifest` if agents expect raw file blocks only |
| Secrets | Passed through | Redacted by default | Use `--no-redact` only when secrets are intentional test fixtures |
| Options object | Monolithic `FuseOptions` | `CollectionOptions`, `ReductionOptions`, `EmissionOptions` in `FusionRequest` | Update programmatic callers |
| Reducers | Static switch in `ContentProcessor` | `CapabilityRegistry<IContentReducer>` | Register reducers via DI |
| Temp files | `{baseFileName}.tmp` collision risk | GUID-based temp names | No action needed |

If your workflow depended on exact 1.x byte output, pin the 1.x tool version or pass explicit flags to approximate prior behavior:

```bash
fuse dotnet --directory ./src --no-manifest --no-redact --tokenizer cl100k_base
```

For MCP agents, replace single-tool calls with the recommended workflow: skeleton, then focus or search, then changes for PR review. See [docs/agentic-workflows.md](docs/agentic-workflows.md).
