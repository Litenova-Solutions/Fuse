# Changelog

All notable changes to Fuse are documented here. Fuse 2.0 is a structural rewrite; backward compatibility with 1.x output is not a goal.

## [Unreleased]

Phase 1 (AOT-safe retrieval) and Phase 2 (token, trust, and developer experience). Every figure below comes from the benchmark harness over the pinned corpus, counted with `o200k_base`; see [benchmarks.md](benchmarks.md). Layer 1 reduction and fidelity are unchanged, so the retrieval gains were not bought by dropping API.

### Added

#### Retrieval (Phase 1)

- Reverse edges in scoping: focus and `--changed-since` now pull a seed's dependents (files that reference the types it declares), not only its dependencies.
- Fielded ranking (BM25F): the relevance index weights declared type and member names and path tokens above the body, so the file that declares a concept ranks above files that mention it.
- Comment and string stripping before dependency extraction, removing false graph edges from type names that appear only in prose or text.
- Budget-aware, rank-decayed expansion: best-first traversal scores neighbours by parent score times a per-hop decay and stops at an optional token budget; seeds are always admitted.
- Query normalization: shared tokenizer splits camelCase and snake_case, drops stopwords, and applies a light suffix stemmer to both documents and queries.
- Relevance-ordered truncation: emission writes most-relevant first under `--max-tokens` for scoped runs, so the seed survives the cut.
- Measured effect: Layer 2A recall rose for changes (71 to 88 percent), focus (26 to 43), and query (37 to 54), with changes precision 21 to 61; Layer 2B accuracy rose for query (25 to 67) and focus (25 to 42). All three scoping modes now clear the grep baseline.

#### Output, trust, and developer experience (Phase 2)

- Compact output envelope (`--format compact`): a single header line per file and no closing marker, for fewer envelope tokens. XML stays the default.
- Header dedup (`--dedup-headers`): identical leading comment headers shared by two or more files are emitted once in a preamble and replaced with a marker; preprocessor directives and code are untouched.
- Anthropic and Gemini tokenizers: deterministic estimators selected by model name (`claude*`/`anthropic*`, `gemini*`/`google*`); OpenAI encodings remain exact.
- `fuse verify`: reports the preserved percent of public types, methods, and routes after a fusion (Roslyn syntax-only in the framework-dependent tool, AOT-clean regex fallback in the Native AOT build).
- `fuse explain`: dry run listing included and excluded files with a token estimate; writes nothing.
- Machine-readable JSON run report (`--report <path>` or `--report -`): source-generated and AOT-safe; always names the tokenizer used.

### Fixed

- `FusionResult` reconstructions dropped per-file token data and cache statistics, leaving the JSON run report, `fuse explain`, and `fuse verify` with no file list; these now propagate through the pipeline.
- `--format` and `--tokenizer` were inferred as required by the command framework; documented invocations such as `fuse dotnet --directory ./src` now work without them.

## [2.0.0] - 2026-06-19

### Breaking changes

- **Solution restructure.** Monolithic layers replaced by axis-based projects: Collection, Analysis, Reduction, Emission, Fusion, plus language plugins (`Fuse.Plugins.Languages.CSharp`, `Fuse.Plugins.Formats.Web`).
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
