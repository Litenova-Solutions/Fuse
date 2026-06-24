# Changelog

All notable changes to Fuse are documented here. The format is based on Keep a Changelog. Fuse 2.0 is a structural rewrite; backward compatibility with 1.x output is not a goal.

## [Unreleased]

### Fixed

- **Whole-PEM-block redaction and additional provider key patterns (C6).** The PEM rule matched only the
  `-----BEGIN ... PRIVATE KEY-----` header line, so the base64 key body and the `END` line were left in the
  output: the secret survived. It now matches the entire block, header through matching footer, and removes it
  as one unit (RSA, EC, DSA, OPENSSH, ENCRYPTED, PGP, and plain variants). Added redaction patterns for GitHub
  tokens (`ghp_`/`gho_`/`ghu_`/`ghs_`/`ghr_` and fine-grained `github_pat_`), Google API keys (`AIza...`),
  Slack tokens (`xox[baprs]-...`), and Stripe live keys (`sk_live_`/`rk_live_`). These matter more now that
  skeleton and slice output is redaction-correct (C1) and goes straight to an agent.
- **Collision-free symbol identity for member operations (C5).** `SymbolChunk` exposed only a `QualifiedName`
  (`ParentType.SymbolName`), which collides for overloads, nested types that share a simple parent name, the
  same member name across namespaces, and partial-class members. Operations keyed on it could conflate
  distinct members: body deduplication, for instance, could collapse a sibling overload's unique body when
  another overload was a duplicate, because both shared the same display name. Each chunk now carries a
  collision-free `StableId` (namespace, full containing-type chain with generic arity, member name, and for
  methods and constructors the generic arity and parameter type list), and member selection, thin-skeleton
  assembly, and body deduplication key on it. `QualifiedName` is retained for display (provenance, markers).
- **SQLite pending-write race in the cache flush (C4).** `SqliteKeyValueStore.FlushAsync` snapshotted the
  buffered writes, committed them, then removed the flushed keys by key alone. A `Set` on a snapshot key while
  the commit was in flight was therefore dropped: the just-flushed (older) value's key was removed, discarding
  the newer pending value. Removal is now by value (the `KeyValuePair` overload), so a concurrent update with a
  different array reference stays pending for the next flush instead of being lost. Covered by a test that
  overlaps a hot-key writer with repeated flushes over a large batch. (The related per-call store pooling, where
  the serve path opens a fresh store per call and so does not share the pending buffer, is folded into item 24's
  repo-scoped index cache rather than fixed in isolation, since the two share a lifecycle.)
- **Concurrency hazards under the MCP server (C3).** The orchestrator is a singleton and the MCP server can run
  tool calls concurrently against different repositories, which exposed three shared-state races, now fixed.
  (1) `GitIgnoreFilter` held a mutable pattern set updated per run via `SetPatterns`; concurrent collection runs
  could apply one repository's ignore rules to another. The filter is now immutable, and the collection pipeline
  builds a fresh per-run instance carrying that run's patterns. (2) Pattern detectors accumulate mutable state
  during a detection pass but were registered as singletons and shared across runs; they are now transient and
  the post-reduction pipeline resolves a fresh detector batch per run through a factory. (3) The collection
  pipeline no longer mutates any shared filter, so being captured by the singleton orchestrator is safe. Covered
  by concurrency tests that interleave runs with differing `.gitignore` rules and with the pattern summary on.
- **Strict total-token accounting for `--max-tokens` / `maxTokens` (C2).** The token budget is now a hard cap
  on the complete payload, not just the file bodies. Two issues are fixed. First, emission charged an entry to
  the budget and only then checked the limit, so the one entry that crossed `MaxTokens` was still written;
  emission now rejects and stops before writing the over-budget entry (the single most-relevant entry is still
  emitted unconditionally so a scoped run never returns nothing). Second, the manifest, route and project maps,
  redaction report, pattern summary, and the session, header, and review preambles were appended after packing
  and never charged, so a scoped payload could overrun the requested budget by the size of its framing. The
  packer now reserves room for that framing (measured against the packed file set in a tight two-pass scheme),
  so the full payload an MCP client receives fits the budget. Verified by tests asserting the complete
  in-memory payload, including the manifest, stays within `MaxTokens`.
- **Secret redaction now covers post-reduction source rewrites (C1).** The thin-skeleton path (query scoping,
  which keeps the query-matched members verbatim) and the symbol-slice path (a `Type.Member` focus seed)
  rebuild a file's content from raw source after the reduction stage has already run, and secret redaction
  runs inside the reduction stage. A secret in a kept member body, field initializer, attribute argument, or
  const literal therefore bypassed the redactor and could reach an agent in clear text. Both rewrites now
  re-run the redactor on the assembled content before emission, and the reported per-kind redaction counts
  describe what was actually emitted. The invariant (any content rebuilt from source after reduction must
  pass the redactor) is enforced in code and covered by tests on both paths.

### Added

- **Latency benchmark layer (B13).** A new `layer-latency.ps1` measures the end-to-end wall clock of a scoped
  query call per corpus repo, cold (no reduction cache, no persistent index) versus warm (both, after a warmup),
  reporting p50/p95 over repeated samples plus peak working set. It is wired into `run-all.ps1`. This is the
  latency an agent waits on; absolute times are machine-dependent, so the committed figures are a reference and
  the warm-versus-cold ratio is the portable signal (warm runs land at roughly 0.3 to 0.7 of cold on the pinned
  corpus, consistent with the persistent index amortizing the Roslyn parse). Per-stage timing (for example
  reduction time) is a follow-on once the pipeline surfaces it.
- **Layer 2A benchmark diagnostics: wasted tokens, change-set-size strata, and cost-adjusted recall.** The
  scoping benchmark now reports, alongside recall@budget, the budget spent on emitted files outside the truth
  set (B8, a proportional estimate), recall broken out by change-set size (B10: small 1-3, medium 4-9, large
  10-plus, where the token budget truncates large change sets the mean hides), and a cost-adjusted recall equal
  to mean recall times mean precision (B11, which punishes buying recall with a wide low-precision set). These
  are reporting-only additions over the existing per-PR measurements; recall, precision, and token figures are
  unchanged. The large stratum makes the budget wall explicit: at the 50k headline budget, changes recall is 97
  percent on small change sets but 12 percent on large ones.
- **Typed experimental options recorded in the run report.** The experimental scoring knobs (graph-centrality
  weight, pseudo-relevance feedback query expansion) are now a typed `ExperimentalOptions` record carried on
  `FusionRequest` rather than ambient process state read deep in the orchestrator. `FUSE_CENTRALITY_WEIGHT` and
  `FUSE_QUERY_EXPANSION` are still honored, but only as an override applied when the orchestrator resolves the
  request's configured values, and the environment is consulted at exactly one point. The resolved knobs are
  written into the machine-readable run report (`--report`) under an `experimental` object, so a committed
  measurement names the configuration that produced it instead of depending on invisible environment state.
  Defaults are unchanged (centrality weight 0.15, query expansion on), so scoping behavior and benchmark
  numbers are identical.
- **Pseudo-relevance feedback query expansion (on by default, fast path).** Query scoping now runs a second
  BM25F ranking pass seeded with recurring declared-symbol terms harvested from the first pass's top files,
  so a sparse natural-language query (a PR title, a task sentence) is rewritten in the codebase's own
  vocabulary before files are selected. The pass is entirely lexical: no model inference and no network, so
  the default scoping path stays fast. Several guards keep it from the classic feedback failure mode of
  broadening or drifting away from a weak first pass: candidate terms come only from the high-signal
  declared-symbol field; a term must recur across at least two feedback files; a term must clear a corpus
  inverse-document-frequency floor, so boilerplate names shared across most files are dropped; expansion
  terms are blended in at a low weight (0.2) relative to the original query, so they nudge ranking toward
  co-occurring concepts without displacing incidental first-pass hits when a query's title is poorly aligned
  with its change; and the expanded ranking is merged with the first pass rather than replacing it, so a
  first-pass seed is never dropped. Tunable through `QueryExpansionOptions`; set `FUSE_QUERY_EXPANSION=0` to
  disable and reproduce the single-pass BM25F ordering exactly.

### Research notes

- **Source:** RM3 / pseudo-relevance feedback (classical IR; recent survey "Query Expansion in the Age of
  Pre-trained and Large Language Models", arXiv 2509.07794, and "A Systematic Study of Pseudo-Relevance
  Feedback with LLMs", arXiv 2603.11008). **Idea:** mine top first-pass documents for expansion terms and
  re-rank. **Fit:** maps to the roadmap's "query expansion from the symbol index" item and Layer 2A/4 query
  recall; stays on the fast lexical path. **Decision:** adopt, constrained to the declared-symbol field with a
  multi-document-frequency and corpus-IDF gate, blended at a low weight and merged with (not replacing) the
  first pass. The literature's headline caveat (PRF degrades recall when the first pass is weak) reproduced in
  A/B spikes over the pinned corpus: an aggressive symbol-field PRF lifted FluentValidation and Newtonsoft.Json
  but regressed AutoMapper, entirely on one PR whose title ("Handling lower case") is disconnected from its
  change (Licensing files), where expansion correctly sharpened toward casing and dropped an incidental hit.
  **Rejected** a seed-overlap drift guard: measuring overlap on the disagreeing PRs showed the harmful case
  (overlap 0.50) sits between helpful cases (0.30 and 0.90), so no overlap threshold separates them, and a
  guard at 0.5 rejected a beneficial low-overlap expansion while keeping the harmful one. **Adopted** instead
  a low expansion weight (swept per PR: 0.2 preserves the off-topic AutoMapper hit while keeping the
  FluentValidation and Newtonsoft.Json gains), the IDF gate to drop corpus-wide boilerplate symbols, and a
  seed-preserving merge so expansion is recall-additive at the seed level.
- **Source:** LARGER (Lexically Anchored Repository Graph Exploration and Retrieval, arXiv 2605.16352) and
  recent SWE-bench localization work, which anchor lexical search into a repository graph and expand to
  structurally related evidence (callers, tests). **Idea:** follow reverse edges from query seeds to reach the
  users and tests of a matched concept. **Fit:** Layer 2A/4 query recall on the fast graph path. **Decision:**
  rejected. A measured A/B over the pinned corpus (query mode, headline budget) dropped mean recall from 51 to
  45 percent: FluentValidation rose (51 to 55) but MediatR (94 to 89), AutoMapper (29 to 25), and especially
  Newtonsoft.Json (30 to 13) regressed, because dependents of common types flood the candidate set and displace
  the real targets under the token budget. The existing forward-only query expansion is retained. A
  confidence-scored or seed-restricted reverse hop (LARGER-style) might recover the FluentValidation gain
  without the broad regression and is left for future work.
- **Source:** BM25 + small code-embedder rerank (multiple 2025 code-search papers report recall lifts to the
  low 70s percent at small K). **Idea:** rerank BM25 top-K with a learned model. **Fit:** Phase C opt-in
  hybrid rerank. **Decision:** deferred; remains opt-in only per the design invariant (no mandatory model
  bundle on the default path).
- **Planned (Phase A, scoped): tiered emission.** Emit expansion-neighbor files (provenance hop >= 1, the
  non-seed context) as skeletons rather than fully reduced, so each costs fewer tokens and the
  relevance-per-token packer (`ReductionAwarePacker`) fits more files under a fixed budget. Expected to lift
  recall most on large change sets where the budget currently truncates truth files (for example
  Newtonsoft.Json PR sets of 20+ files). Approach: thread seed-vs-neighbor provenance into a per-file
  reduction level (the pipeline currently applies one global `ReductionOptions`) or reuse the post-reduction
  thin-skeleton path that query member selection already uses. Needs golden-output coverage and a full
  Layer 2A/4 regeneration; left for a focused iteration.

### Breaking changes

- **Removed `--rerank` and `--embeddings` CLI flags.** Query scoping is BM25F-only again.
- **Removed `FUSE_EMBEDDINGS` and `FUSE_EMBEDDINGS_MODEL_PATH`.** No embedding backend or model resolution remains.
- **Removed MCP tool parameter `rerank` on `fuse_search` and `fuse_dotnet`.**
- **Removed bundled ONNX model from NuGet and runtime packages.** Release artifacts no longer ship a `models/` directory.
- **Removed the entire hybrid retrieval stack** (`IEmbeddingModel`, hashing rerank, vector cache in `.fuse/fuse.db`).

## [2.4.0]

### Added

- **`fuse reduce` CLI command and `fuse_reduce` MCP tool.** Compacts a caller-supplied set of files, or raw content, by running Fuse's reduction without collecting a whole directory. The agent (or a script) compacts context it has already identified instead of re-scoping. The CLI takes `--files` (a path list, written to stdout) or `--stdin` (piped content, with `--ext` selecting the reducer); the MCP tool takes `files` (paths) or `content` (with `extension`). Both accept a reduction `level` and a token ceiling. Content mode materializes a temporary file so the reducer is selected by extension, then deletes it. Backed by a new explicit-file collection mode (`CollectionOptions.ExplicitFiles`, `FusionRequestBuilder.WithExplicitFiles`) that reuses the full reduction and emission pipeline; missing paths are skipped rather than failing the run.

## [2.3.0]

### Added

- **`fuse mcp install --rules`**: opt-in flag that, alongside registering the MCP server, writes a short rule biasing the agent toward the `fuse_*` tools into each client's instruction file (Claude `CLAUDE.md`, Cursor `.cursor/rules/fuse.mdc`, GitHub Copilot `.github/copilot-instructions.md`). The rule is conservative: prefer Fuse for surveying and context-gathering, use grep for exact-string and symbol lookups. Freeform files (Claude, Copilot) get a marker-delimited block that re-runs replace in place rather than duplicate, preserving surrounding content. Rules are project-scoped; under `--scope user` only Claude has a global equivalent (`~/.claude/CLAUDE.md`) and the others are skipped with a note. A normal `fuse mcp install` now prints a tip pointing at the flag.

### Changed

- **More directive MCP tool guidance.** The `fuse_toc` and `fuse_search` tool descriptions and the server instruction block now tell the agent to prefer the `fuse_*` tools over raw grep or reading files one by one when surveying or scoping, and to use grep only for exact-string or symbol lookups. This biases clients (especially Cursor and Copilot, which lean on tool descriptions) toward Fuse without changing any behavior.

## [2.2.1]

### Fixed

- **`fuse mcp install` no longer requires `--command`.** The optional `--command` option was inferred as required by the command framework, so `fuse mcp install` failed with `Option '--command' is required` unless a value was passed. It is now declared optional and defaults to the running fuse binary, as documented. (2.2.0 shipped with this regression; 2.2.0 is unusable for install without an explicit `--command`.)

## [2.2.0]

Registering Fuse with an AI client is now one command, and the MCP surface is grouped under `fuse mcp`. The change is about setup ergonomics; the reduction, scoping, and emission paths are unchanged.

### Breaking changes

- **`fuse serve` moved to `fuse mcp serve`.** The stdio MCP server (the long-running process your client launches) is now a subcommand of the new `fuse mcp` group. Client configuration that launched `fuse serve` must change its arguments from `["serve"]` to `["mcp", "serve"]`. Re-running `fuse mcp install` rewrites them for you; for hand-written config, edit the `args` array. The MCP Registry manifest and the published package arguments are updated to match.

### Added

- **`fuse mcp install`**: registers Fuse as an MCP server with Claude Code, Cursor, or GitHub Copilot in one command, replacing per-tool JSON editing. `--client` targets one client or `all` (default); `--scope` writes project-local config (default, commit it so the team inherits Fuse) or user-global config (every project you open); `--command` overrides the launched executable. Claude Code user scope is registered through the Claude CLI; the other clients have their config file written directly. The installer merges into an existing config without dropping a co-located server's `env` or `cwd` block or top-level keys such as Copilot's `inputs` array, and it resolves the per-OS VS Code user profile directory rather than a path VS Code does not read.
- **`fuse mcp` command group**: parents `install` and `serve`, separating the MCP server surface from the one-shot CLI.

## [2.1.0]

### Breaking changes

- **Single reduction level replaces the C# reduction flag cluster.** `--all`, `--skeleton`, `--public-api`, `--aggressive`, and the `--remove-csharp-*` switches are removed in favor of one `--level` option (and a matching `level` MCP parameter) with the values `none`, `standard`, `aggressive`, `skeleton`, and `publicApi`. The CLI commands default to `none`; the scoped MCP tools (`fuse_focus`, `fuse_search`, `fuse_changes`, `fuse_dotnet`) default to `standard`, so an agent gets the standard removals (which preserve 99 to 100 percent of the public API surface on the benchmark corpus) without naming a level. Migrate `--all` to `--level aggressive` (add `--collapse-generated` if you relied on `--all` collapsing generated code), `--aggressive` to `--level aggressive`, `--skeleton` to `--level skeleton`, `--public-api` to `--level publicApi`, and any `--remove-csharp-*` flag to `--level standard`. Redaction, generated-code collapse, semantic markers, pattern summary, route map, project graph, and minification stay orthogonal to the level.

### Added

- **Opt-in local embedding model for hybrid rerank** (`FUSE_EMBEDDINGS`): the `--rerank` vector path can use a real local ONNX embedding model, realizing the `IEmbeddingModel` plug point that shipped in 2.0 behind a deterministic lexical fallback. The model assembly is excluded from the Native AOT package, matching the isolation of the Roslyn precision tier, so the default AOT binary stays reflection-free.
- **Chunk-granular query retrieval.** A `SymbolChunk` model and member-level chunk extractors let query scoping rank and pack at member granularity rather than whole files, feeding a thin-skeleton packing path that keeps the matched members in full while reducing the rest. Member selection is decoupled from file ranking so that packing at the member level does not lower file recall.
- **Reduction-aware single-pass packing.** Packing fits content to a token budget in one pass with reduction accounted for, instead of reducing and then re-fitting.
- **Near-duplicate member-body deduplication.** Members whose bodies are near-identical are collapsed, so repeated boilerplate bodies cost their tokens once.
- **Persistent BM25 body-tokenization cache.** Body tokenization for the relevance index is cached by content hash, so repeated scoped runs skip re-tokenizing unchanged files. This is separate from the persistent analysis index added in 2.0.
- **Tokenizer calibration harness.** A harness and a gated accuracy test calibrate the estimating tokenizers (the Anthropic and Gemini estimators) against reference counts.
- **Redaction fidelity reporting.** The redaction report distinguishes secrets found in code literals from those in configuration, so a run can show where redaction acted.

## [2.0.0]

Fuse 2.0 replaces the monolithic 1.x engine with axis-based projects and adds a Roslyn precision tier, hybrid retrieval, survey and round-trip tools, and a reproducible benchmark suite. Every measured figure below comes from the benchmark harness over the pinned corpus, counted with `o200k_base`; see [the benchmarks page](https://fuse.codes/docs/project/benchmarks). The precision tier and the survey, round-trip, and retrieval-rerank features are opt-in and do not change the default reduction or scoping path, so the default Layer 1 reduction and fidelity and the Layer 2 recall and precision are stable across runs.

### Breaking changes

- **Solution restructure.** Monolithic layers replaced by axis-based projects: Collection, Reduction, Emission, Fusion, plus language plugins (`Fuse.Plugins.Languages.CSharp`, `Fuse.Plugins.Formats.Web`).
- **MCP tools.** Legacy `get_optimized_context` removed. Replaced by eight focused tools: `fuse_toc`, `fuse_skeleton`, `fuse_focus`, `fuse_search`, `fuse_changes`, `fuse_ask`, `fuse_dotnet`, and `fuse_generic`.
- **Default tokenizer.** Token counting now defaults to `o200k_base` (was `cl100k_base`). Counts will differ from 1.x for the same content.
- **Secret redaction default ON.** API keys, JWTs, connection strings, and high-entropy literals are redacted before token counting. Use `--no-redact` to disable.
- **Manifest header default ON.** Output is prefixed with a file tree and per-file token costs. Use `--no-manifest` to disable.
- **Emission ordering.** Files emit in descending token-count order (largest first) to help agents prioritize within a budget.
- **Options model.** Monolithic `FuseOptions` replaced by `CollectionOptions`, `ReductionOptions`, and `EmissionOptions` carried in a `FusionRequest`.

### Added

#### Retrieval and scoping

- Dependency-aware focus scoping (`--focus`, `--depth`): scope a fusion to a type, file, or path and its dependency neighborhood.
- Git change scoping (`--changed-since`, `--include-dependents`): scope to files changed since a git ref, optionally pulling their first-degree dependents.
- BM25 query-scoped fusion (`--query`, `--query-top`): rank files by relevance to a natural-language or keyword query and expand from the top seeds.
- Reverse edges in scoping: focus and `--changed-since` pull a seed's dependents (files that reference the types it declares), not only its dependencies.
- Fielded ranking (BM25F): the relevance index weights declared type and member names and path tokens above the body, so the file that declares a concept ranks above files that merely mention it.
- Comment and string stripping before dependency extraction, removing false graph edges from type names that appear only in prose or text.
- Budget-aware, rank-decayed expansion: best-first traversal scores neighbours by parent score times a per-hop decay and stops at an optional token budget; seeds are always admitted.
- Query normalization: a shared tokenizer splits camelCase and snake_case, drops stopwords, and applies a light suffix stemmer to both documents and queries.
- Relevance-ordered truncation: emission writes most-relevant first under `--max-tokens` for scoped runs, so the seed survives the cut.
- Measured effect over the pinned corpus: Layer 2A recall rose for changes (71 to 88 percent), focus (26 to 43), and query (37 to 54), with changes precision 21 to 61; Layer 2B accuracy rose for query (25 to 67) and focus (25 to 42). All three scoping modes now clear the grep baseline.

#### Survey and round-trips

- Table-of-contents mode (`--toc`, `fuse_toc`): a directory tree with per-file token costs and a symbol outline instead of file bodies. A cheap first call for surveying a codebase before fetching files. Backed by a new `ISymbolOutlineExtractor` capability.
- `fuse_ask` MCP tool: takes a task and a token budget, deterministically picks skeleton, focus, or search from the task text, and packs the result to budget. Focus falls back to search when the named type does not resolve.
- Review-shaped change emission (`--review`, `fuse_changes` `review`): prepends a review map pairing each changed file's unified diff hunks with its direct callers.
- Session-delta emission (`--session`, the `session` parameter on `fuse_focus` and `fuse_search`): omits files whose identical content was already emitted under a session id, with a note listing them; a changed file is resent. Backed by a process-scoped session tracker.

#### Precision tier (opt-in, AOT-isolated)

- Roslyn semantic plugin (`--semantic`, `FUSE_SEMANTIC`): Roslyn syntax-tree implementations of the C# skeleton, dependency, type-name, and outline capabilities, registered after the regex defaults so they win for `.cs`. Fixes the regex skeleton collapse on conditional compilation and partial classes and captures references the regex misses. Shipped in a separate assembly the Native AOT package does not reference; the AOT build stays regex-only and IL2026/IL3050 clean.
- Symbol-level scoping: with the precision tier, a `Type.Member` focus seed scopes the seed file to that member (full body) with the rest of the file reduced to signatures. Backed by a new `ISymbolSliceExtractor` capability.
- Persistent analysis index (`--index`, on by default in watch and serve): caches per-file dependency and symbol analysis under `.fuse/index`, keyed by content hash and analyzer tier, shared across a run. Amortizes the Roslyn parse cost; measured roughly halving warm-call wall-clock on MediatR.
- Hybrid retrieval (`--rerank`, `fuse_search` `rerank`): reranks BM25 candidates by blending the normalized BM25 score with embedding-vector cosine similarity. The bundled embedding is a deterministic, AOT-clean lexical hashing model; the `IEmbeddingModel` interface is the plug point for a learned model. Vectors cached under `.fuse/index/vectors`.
- Cross-language reduction: the JavaScript reducer now covers TypeScript and the JSX, TSX, and ESM variants; a new SQL reducer handles `.sql`.
- Generated-code collapse (`--collapse-generated`, included in `--all`, `fuse_dotnet` `collapseGenerated`): collapses EF Core migration and model-snapshot generated bodies to their signatures, which the default generated-file exclusion patterns miss.

#### Structural maps and patterns

- .NET structural maps: route map (`--route-map`), public API skeleton (`--public-api`), and project graph (`--project-graph`).
- C# skeleton mode (`--skeleton`) and semantic markers (`--semantic-markers`).
- Cross-codebase pattern summary (`--pattern-summary`).

#### Output, trust, and developer experience

- Manifest header with file tree, token costs, pattern summary, and optional git stats.
- Compact output envelope (`--format compact`): a single header line per file and no closing marker, for fewer envelope tokens. XML stays the default.
- Header dedup (`--dedup-headers`): identical leading comment headers shared by two or more files are emitted once in a preamble and replaced with a marker; preprocessor directives and code are untouched.
- Secret redaction with `[REDACTED:<kind>]` placeholders (`--no-redact`, `--redact-report`).
- Inclusion provenance annotations for dependency-expanded files (`--provenance`).
- Anthropic and Gemini tokenizers: deterministic estimators selected by model name (`claude*`/`anthropic*`, `gemini*`/`google*`); OpenAI encodings remain exact.
- `fuse verify`: reports the preserved percent of public types, methods, and routes after a fusion (Roslyn syntax-only in the framework-dependent tool, AOT-clean regex fallback in the Native AOT build).
- `fuse explain`: dry run listing included and excluded files with a token estimate; writes nothing.
- Machine-readable JSON run report (`--report <path>` or `--report -`): source-generated and AOT-safe; always names the tokenizer used.
- Git enrichment in the manifest (`--git-stats`: churn and last-modified per file).
- Project config discovery: `fuse.json` and `.fuserc` with flag over config over default precedence; `fuse init` scaffolds a config file.

#### Core architecture

- Language plugin model with `ILanguageCapability` and `CapabilityRegistry<T>`.
- `IEntryFormatter` with XML, Markdown, and JSON output formats (`--format`).
- `ISourceContentProvider` for single-read file access across pipeline stages.
- Registry-driven reduction, skeleton extraction, dependency extraction, and type location.
- MCP resources for skeleton, focus, search, and change workflows.

#### Performance and Native AOT

- Parallel collection, reduction, and graph building (`--parallelism`, default: processor count).
- Per-file reduction cache in `.fuse/cache` (`--no-cache`, `--clear-cache`).
- Watch mode for iterative fusion (`--watch`; disabled under MCP stdio).
- Token counting via `Microsoft.ML.Tokenizers` (`o200k_base`, `cl100k_base`).
- Source-generated JSON for config and JSON output (AOT and trim safe).
- Native AOT publish profiles and `Fuse.Runtime.{rid}` satellite packages; see [the performance page](https://fuse.codes/docs/project/performance).
- Windows installer ships an AOT-compiled `fuse.exe`.

### Fixed

- `FusionResult` reconstructions dropped per-file token data and cache statistics, leaving the JSON run report, `fuse explain`, and `fuse verify` with no file list; these now propagate through the pipeline.
- `--format` and `--tokenizer` were inferred as required by the command framework; documented invocations such as `fuse dotnet --directory ./src` now work without them.

### Migration from Fuse 1.x

| Area | 1.x behavior | 2.0 behavior | Action |
|------|--------------|--------------|--------|
| MCP tool | `get_optimized_context` | Eight focused tools (see [MCP tools reference](https://fuse.codes/docs/reference/mcp-tools)) | Update agent prompts and MCP config |
| Token counts | `cl100k_base` | `o200k_base` default | Re-baseline `--max-tokens` budgets, or pass `--tokenizer cl100k_base` |
| Output prefix | None | Manifest header | Use `--no-manifest` if agents expect raw file blocks only |
| Secrets | Passed through | Redacted by default | Use `--no-redact` only when secrets are intentional test fixtures |
| Options object | Monolithic `FuseOptions` | `CollectionOptions`, `ReductionOptions`, `EmissionOptions` in `FusionRequest` | Update programmatic callers |
| Reducers | Static switch in `ContentProcessor` | `CapabilityRegistry<IContentReducer>` | Register reducers via dependency injection |
| Temp files | `{baseFileName}.tmp` collision risk | GUID-based temp names | No action needed |

If your workflow depended on exact 1.x byte output, pin the 1.x tool version or pass explicit flags to approximate prior behavior:

```bash
fuse dotnet --directory ./src --no-manifest --no-redact --tokenizer cl100k_base
```

For MCP agents, replace single-tool calls with the recommended workflow: survey with `fuse_toc` or `fuse_skeleton`, then `fuse_focus` or `fuse_search`, then `fuse_changes` for PR review. See [Context for an agent](https://fuse.codes/docs/scenarios/context-for-an-agent).
