# Fuse: project overview and orientation

## About this document

This is a single self-contained overview of Fuse: what it is, how it is built, the algorithms
with their real constants, the measured results, the honest gaps, and the roadmap history. It
was originally assembled to brief a radical-roadmap design session, and it doubles as a general
orientation: a new contributor, a user deciding whether to adopt Fuse, or anyone picking up the
current plan can read it top to bottom and know the whole shape of the project.

Everything here was assembled from the live codebase rather than from memory, so the file and
line references are real and current as of Fuse version 3.2.0. Where a described behavior is
being changed, the section says so and points at the item in
[v3.3-plan.md](v3.3-plan.md).

Two conventions this project enforces, visible throughout: plain ASCII prose only (no em
dashes, no smart quotes, no emoji outside code fences), and every performance number sourced to
`tests/benchmarks/results` (never fabricated or rounded). The rationale for both is in the
measured-results section; the honesty convention is itself a product value.

---

## 1. What Fuse is, in one paragraph and then in full

Fuse is a .NET-native semantic context engine for AI coding agents. It keeps a warm, persistent
Roslyn-backed index of a workspace and resolves what the code actually runs (which
implementation is injected for an interface, which handler processes a request, which endpoint
serves a route, what a git diff impacts), then hands the agent the exact context a task needs,
reduced to fit the context window. It ships two ways: a .NET global tool (`fuse`) for the
terminal and CI, and a Model Context Protocol (MCP) server (`fuse mcp serve`) with a small set
of tools an agent calls in a loop. There is also a VS Code extension that talks to a resident
host process over JSON-RPC.

The problem it exists to solve: an AI agent working on an unfamiliar .NET codebase burns
enormous token budget and many tool round-trips just exploring. It greps, opens files, greps
again, follows a using directive, opens more files. Most of what it reads is noise for the task
at hand, and it still misses the file where the real wiring lives because that file shares no
lexical tokens with the task description. Fuse attacks this on three axes at once:

1. **Token efficiency** - it reduces source (skeletons, public-API views, format minification)
   so the same information costs far fewer tokens, while keeping the public contract intact and
   verifiable.
2. **Precision scoping** - it returns only the files a task needs, ranked, with provenance,
   instead of a dump.
3. **Semantic resolution** - it answers "what actually runs here" from a typed graph built with
   Roslyn, which is the part lexical and embedding tools cannot do. This is the moat.

The deliberate strategic stance (from the V3.1 plan): Fuse is an MCP server in a loop with a
model, so it does not have to win the vague one-shot query. When a request lacks a usable anchor
it refuses and routes (hands back a navigation map and asks for a symbol, route, service,
config, or git base) rather than returning a low-precision guess. The calling model, which is
exploring anyway, then issues a better-anchored query.

Where this is heading: the v3.3 plan repositions Fuse from a retrieval tool that happens to
hold a Roslyn compilation into the .NET agent's ground-truth oracle, the tool that answers the
questions only a compiler can answer at edit speed. Section 12 summarizes that direction.

---

## 2. Repository layout

Solution file: `Fuse.slnx`. Target framework: `net10.0` across all assemblies. SDK pinned to
`10.0.100` in `global.json`. Single version source of truth: `Directory.Build.props`
`<Version>` (currently 3.2.0), mirrored into the VS Code extension, the MCP registry manifest,
and the docs site, all bumped together by `build/set-version.ps1`.

- `src/Core` - the pipeline libraries:
  - `Fuse.Collection` - file discovery, filtering, gitignore, security guards.
  - `Fuse.Reduction` - reduction tier sequencing, caching, token counting, secret redaction.
  - `Fuse.Emission` - payload/manifest emission, budgeting, provenance rendering.
  - `Fuse.Fusion` - the orchestrator that wires Collection to Reduction to Emission, plus
    scoping (BM25F, dependency graph, change detection, context planning, packing).
  - `Fuse.Indexing` - the SQLite index store, schema, text-processing primitives.
  - `Fuse.Semantics` - syntax-tier extraction, the language-provider seam, the Roslyn typed
    graph analyzers, git co-change mining.
  - `Fuse.Retrieval` - candidate generation, scoring, priors, graph expansion, the
    signal-sufficiency contract.
- `src/Host` - user-facing surfaces:
  - `Fuse.Cli` - the CLI commands, the MCP stdio server, the VS Code JSON-RPC host.
- `src/Plugins` - capability plugins resolved by file extension:
  - `Fuse.Plugins.Abstractions` - the capability registry and options records.
  - `Fuse.Plugins.Languages.CSharp` - the lexer-based C# reducer.
  - `Fuse.Plugins.Languages.CSharp.Roslyn` - the Roslyn skeleton extractor and analyzers.
  - `Fuse.Plugins.Formats.Web` - JSON/XML/CSS/SCSS/JS/HTML/Razor/YAML/Markdown/SQL minifiers.
  - `Fuse.Plugins.Rerank.Onnx` - the dense embedding model (all-MiniLM-L6-v2, ONNX).
- `tests/` - unit, golden-output, integration tests. `tests/benchmarks/` - the eval harness,
  corpus manifest, recorded results, peer harness.
- `site/` - the documentation website (Next.js + Fumadocs), published at fuse.codes. All prose
  docs are MDX under `site/content/docs`.
- `ext/vscode/` - the VS Code extension (TypeScript client for the JSON-RPC host).
- `roadmap/` - the internal engineering plans (v3, v3.1, v3.2, v3.3) and this overview.
- `assets/`, `mcp-registry/` - the benchmark figure and the MCP Registry manifest.

---

## 3. The pipeline (Collection to Reduction to Emission)

The classic Fuse pipeline is a four-stage flow orchestrated by `FusionOrchestrator`
(`src/Core/Fuse.Fusion/FusionOrchestrator.cs`). Everything is run-scoped: per call it opens
fresh collaborators (content provider, SQLite reduction cache, analysis index) so concurrent
runs never share mutable state.

### 3.1 Collection (`Fuse.Collection`)

`FileCollectionPipeline.CollectAsync` has three input modes: explicit-file mode (bypasses
enumeration and filters, used by `reduce`), candidate-set mode (caller supplies the listing,
filters still run), and directory walk (default). Filtering runs in parallel; a file is kept
only if every `IFileFilter.Include` returns true, then results are re-sorted deterministically
by enumeration index then normalized path.

Security and scope guards during enumeration: rejects paths escaping the root via `..` or a
different drive, skips symlink/junction files and directories reached through them (stat cached
per directory), and prunes files belonging to nested VCS roots (worktrees, submodules, embedded
clones). Filters include gitignore, binary, auto-generated (reads the first 5 lines for
`<auto-generated`), empty, excluded-dir, excluded-filename, extension, size, glob, and
test-project. Gitignore walks up from each source directory to the repo root, compiling each
pattern into a `DotNet.Globbing.Glob`. About 25 per-language project templates supply default
extensions and exclusions (DotNet, TypeScript, Python, Go, Rust, and more).

### 3.2 Reduction (`Fuse.Reduction` plus the language/format plugins)

One `ReductionLevel` dial expands to per-transform booleans:

- **None** - whitespace normalization plus orthogonal flags only.
- **Standard** - removes C# comments, using directives, namespace wrappers, and #region.
- **Aggressive** - Standard plus whitespace/syntax compression (output not guaranteed to
  compile; it maximizes token savings).
- **Skeleton** - type and member signatures, bodies removed.
- **PublicApi** - Skeleton, but only public and protected members.

`ContentReductionPipeline.ReduceAsync` processes files in parallel with a fixed per-file stage
order: normalize whitespace, apply the extension-specific reducer, apply skeleton, prepend
semantic markers, and (always, after caching) redaction. An optional per-file level selector
lets one run mix tiers (seeds full, neighbors skeletonized). Caching keys on XXHash64 of raw
content plus a hash of the reduction options that folds in a schema version but deliberately
excludes emission-only flags so toggling them does not fragment the cache. On a cache hit the
reducer stages are skipped but redaction still re-runs.

How token savings are achieved while the public API survives:

- **Roslyn skeleton reducer** (`RoslynSkeletonExtractor`): parses the syntax tree and runs a
  `BodyStrippingRewriter` that replaces method/constructor/operator bodies with a semicolon,
  strips accessor bodies, and collapses expression-bodied properties to auto-getters. In
  public-API-only mode it drops non-public/protected members (never interface members, which
  are implicitly public). Working from the parsed tree, not regex, means conditional
  compilation, partial classes, and braces-in-strings cannot desync a depth counter.
  Signatures survive, bodies vanish: a large token cut with the contract intact.
- **Lexer-based C# reducer** (`CSharpReducer`): masks every string/char literal (regular,
  verbatim, interpolated, raw) with opaque placeholders before any transform, so comment
  stripping can never reach into literal contents; then removes comments/directives/usings/
  namespaces via source-generated regex, optionally applies aggressive optimization, and
  restores the literals.
- **Generated-code collapse**: detects EF Core migrations and model snapshots and replaces the
  bodies of Up/Down/BuildModel/BuildTargetModel with a marker while keeping the signatures.
- **Format reducers** (`Fuse.Plugins.Formats.Web`): whitespace minifiers for JSON, XML, CSS,
  SCSS, JS, HTML, Razor, YAML, Markdown, SQL.

Secret redaction (`DefaultSecretRedactor`) is regex plus entropy: named patterns (AWS keys,
JWT, PEM private-key blocks, GitHub/Google/Slack/Stripe tokens, generic api_key/token
assignments), connection strings (only a quoted literal with two or more semicolon-delimited
pairs and a connection keyword), and a high-entropy heuristic (quoted literal 32 or more chars,
Shannon entropy 4.5 or higher, mixed case, digit, and symbol). It can classify which redactions
fall inside C# string literals as a diagnostic.

### 3.3 Emission (`Fuse.Emission`)

`EmissionPipeline.EmitAsync` builds a token budget, orders entries (most-relevant-first when any
entry has a relevance score, else descending token count), writes an optional manifest prefix,
then loops entries charging token count plus a 30-token marker overhead against the budget. The
budget has two limits, `MaxTokens` (hard) and `SplitTokens` (per part), and checks before
committing so an over-budget entry is never charged or emitted. If nothing fits, it force-emits
the single most-relevant entry so a scoped run never returns empty.

Token counting: exact offline counts via `Microsoft.ML.Tokenizers` Tiktoken (default
`o200k_base`), or a deterministic approximate counter for providers without a local tokenizer.
The manifest carries per-file token counts and, when git stats are available, commit count and
last date. Provenance (the inclusion chain: why each file is here, and which members were
selected) is rendered by the entry formatters. Disk output streams to temp files then renames to
token-tagged names; a table-of-contents mode degrades detail (Full to PathsOnly to Directories)
until it fits.

### 3.4 Scoping and packing (in `Fuse.Fusion`)

Three mutually exclusive scoping modes (`Stages/FusionScopingStage.cs`): Focus (resolve seed
paths or a Type.Member symbol slice, build a dependency graph, compute centrality, expand
outward with provenance), Changes (git changed files, optional dependents expansion, and a
diff-plus-callers preamble in Review mode), and Query (delegate to the BM25F pipeline over the
dependency graph).

BM25F relevance (`Scoping/Bm25RelevanceIndex.cs`) is a fielded inverted index with four fields
(Body, Symbols, Path, Comments), each with its own length-normalization b and boost. A term in
a declared symbol name counts 5x, in the path 3x, in comments 1.5x, versus the body at 1x.
K1 = 1.2. This is the in-memory BM25F used by the classic fusion path; the persistent index uses
a separate FTS5 bm25 weighting (section 5), and the two historically did not agree on whether
path or symbol name should rank highest (section 8, issues 2 and 3). The v3.3 plan retires the
in-memory ranker and unifies on one weight table (items N1 and N2).

Context planning (`Scoping/ContextPlanBuilder.cs`) labels each file Seed / Dependency / Changed
and assigns a reduction tier once: tiered emission downgrades non-seed dependencies to Skeleton
while seeds keep the requested level. Strict total-token accounting does a two-pass pack: pass
one packs bodies into the whole budget, then it measures the framing (manifest, maps, redaction
report, preambles) for that set, and pass two re-packs into MaxTokens minus that reserve, so the
complete payload the client receives fits the cap. The packer (`Scoping/ReductionAwarePacker.cs`)
is a greedy 0/1-knapsack over real reduced token cost by descending relevance-per-token density,
admitting the single most-relevant entry unconditionally.

---

## 4. The persistent semantic index (`Fuse.Indexing`)

All cache and index data live in a single SQLite file at `.fuse/fuse.db` in WAL mode. This is
the warm store the MCP tools and the host read from.

Schema version `WorkspaceIndexSchema.TargetVersion = 14`. Pragmas: WAL, synchronous NORMAL.
Relational tables: `files` (with `normalized_path` unique, `content_hash`, `language`,
`is_generated`, `is_test`, `project_id`), `projects`, `nodes` (typed-graph node: kind,
display_name, symbol_id, stable_key, span), `symbols` (assembly-qualified id,
fully_qualified_name, is_public_api), `chunks` (token_estimate, reduced_token_estimate,
outline), `edges` (from/to node, edge_type, weight, confidence, evidence file plus span, unique
on from/to/type/evidence), `routes`, `di_registrations`, `options_bindings`, `git_cochange`
(path pair, count, pmi, jaccard, last_seen), `chunk_embeddings` (chunk_id, dim, vector blob).

An FTS5 virtual table `chunk_fts` is created separately so a runtime lacking FTS5 still builds
the relational schema. Its columns, in this order (order is load-bearing for the bm25 weights):
`chunk_id UNINDEXED, path, name, symbols, signature, comments, body, subtokens, stems`,
tokenizer `unicode61`.

Two independent freshness mechanisms:

1. **Schema version** - `WorkspaceIndexMigrator` reads `schema_version`; if below target it
   drops every Fuse-owned object (so stale tables from old schemas go too) and recreates. There
   is no incremental migration in V3; it rebuilds from scratch.
2. **Build compatibility** - `FuseBuildInfo.Current` reads the assembly informational version;
   `IsCompatible` compares by major.minor only (a patch keeps the extraction contract, a
   minor/major may not) and treats null as compatible so a pre-stamp index is not wiped. The
   store stamps `fuse_version` into `index_meta` on every pass; on init, if incompatible, it
   rebuilds. `EnsureTablesAsync` runs every init (all IF NOT EXISTS) to self-heal additive
   changes within a version.

Edge ids are a stable xxHash64 of (from, to, type, evidence file) so re-index replaces rather
than accumulates. Embeddings are stored as little-endian float blobs. Store status is Cold when
file count is 0, else Warm.

---

## 5. Retrieval and ranking (`Fuse.Retrieval`)

Orchestrated by `SemanticRetrievalEngine`. `LocalizeAsync` flow: low-signal gate (refuse and
navigate), then generate candidates, score, apply the centrality prior, apply the co-change
prior, grade the signal, select by state, optionally expand through the graph, dedupe, truncate.

### 5.1 Candidate generation

Default generators: Exact, Lexical (FTS), Path, Diff, plus Dense when an embedder is available.

- **Lexical / BM25F over FTS5**: `WorkspaceIndexStore.SearchAsync` runs FTS5 `bm25()` with
  positional per-column weights on chunk_fts. Query-time expansion (`BuildMatchExpression`)
  tokenizes the query then adds each term's subword splits and their Porter stems, joined with
  OR, with the raw term kept first so exact-name matches rank highest. Note: the executing
  weight vector currently weights the `path` column highest, which contradicts the adjacent
  intent comment; v3.3 item N1 corrects this and lands a ranking regression suite so the class
  of bug becomes measurable.
- **Subword and stem index-time fields**: `subtokens` splits camelCase/snake_case/acronyms;
  `stems` is Porter 1980; the `comments` field is the only source for the comments column and
  also feeds the stems bridge. This is the deterministic fix for the vocabulary gap: a prose
  word like "rounding" can now match a code identifier like `ApplyRoundingMode`.
- **Dense**: `DenseCandidateGenerator` embeds the query, cosine against persisted chunk vectors,
  best per file, top 20, normalized. The model is all-MiniLM-L6-v2 (384-dim, quantized ONNX,
  BERT WordPiece, max 256 tokens, mean-pooled and L2-normalized). It is fetched once on the
  indexing path only (never at query time) from HuggingFace `Xenova/all-MiniLM-L6-v2` (about
  23 MB), SHA-256 pinned. On by default (opt out via `FUSE_DENSE`); when absent the path is
  byte-identical to the lexical fallback.

### 5.2 Scoring and priors

`CandidateScorer.Score` groups candidates per node (or file) and combines source scores with
noisy-or `1 - product(1 - score)`, so corroboration raises toward but never past 1. Base source
weights: DiffChangedFile and RouteExact 1.00; Symbol/Service/Request/StackTrace 0.95; ConfigExact
0.90; FtsSymbol 0.75; Dense 0.72; FtsPath 0.70; FtsBody 0.55; Cochange 0.45; GraphNeighbor 0.40.

- **Dependency-centrality prior** (`GraphCentralityPrior`): centrality is normalized node degree
  (in plus out) over the semantic graph; boost is score x (1 + 0.10 x centrality), capped at 1,
  top 30 only. A no-op in syntax mode (no edges).
- **Git co-change prior** (`GitCoChangePrior`): for the top 8 seed files, a candidate that
  co-changes with a strictly-higher-scored seed gets score x (1 + 0.15 x jaccard), capped at 1,
  top 30 only. Mining (`GitCoChangeCollector`) runs one bounded `git log --no-merges --name-only`
  (max 1000 commits), skips commits touching more than 40 source files, keeps pairs seen twice or
  more, computes PMI and Jaccard, has a 20 s hard timeout, and degrades to no rows.

### 5.3 Graph expansion and navigation

`ExpandSeedsThroughGraphAsync` (opt-in) expands the top 2 seeds one hop and admits up to 5
neighbor files at decayed scores. `EdgeWeightProvider` gives per-edge traversal weights with
HopDecay 0.65: route_handles 1.00, mediatr_handles 0.95, di_resolves_to 0.95, implements 0.90,
di_depends_on_impl 0.85, options_binds 0.85, config_impacts 0.80, inherits 0.75, di_injects 0.75,
options_consumes 0.75, sends_request 0.70, tests 0.65, calls 0.60, cochanges 0.45,
project_references 0.30, path_proximity 0.20, references 0.15. `GraphNeighborhoodExplorer`
provides standalone navigation primitives (neighborhood, callers/implementers, central files),
with a same-folder cohesion fallback in syntax mode.

### 5.4 The signal-sufficiency contract (the strategic centerpiece)

Two stages, both model-free and reproducible:

1. **Low-signal gate** (`QuerySignalClassifier`): before candidate generation. Any structured
   signal (route/focus/service/request/config/changed-since/selected-paths) is immediately
   HighSignal, never rejected. Only free-text-only queries are judged, and only against three
   precise noise regexes (merge, dependency-bump, ci/chore) plus empty. Deliberately conservative
   to keep the false-rejection rate near zero: an answerable prose title is not downgraded.
2. **Score grading** (`SignalGrader.Grade`): from the score distribution alone, fixed thresholds.
   Confident needs top at or above 0.55 and a leading cluster (within a 0.15 gap) of at most 3
   that stands clear of the tail; top below 0.30 is Insufficient; otherwise Partial. Confident
   returns only the leading cluster (the precision win); Partial returns a small best-effort set;
   Insufficient returns nothing under strict mode, else best-effort, plus a `NavigationMap`
   (candidate areas, entry points, nearest symbols, and an ask). `NavigationMapBuilder` builds a
   real map even for an insufficient result so the refusal is a navigation step, not a wall.

This contract is the load-bearing honesty mechanism of the whole design. The v3.3 plan
generalizes it into the oracle's availability contract: when a compiler-backed answer cannot be
produced (the owning project did not load), the tool says "cannot verify" rather than guessing.

---

## 6. The semantic (typed-graph) tier: Roslyn edge extraction

This is the deterministic moat and the part no lexical or embedding tool reproduces.

Roslyn via MSBuild. `RoslynWorkspaceLoader` registers MSBuildLocator once per process, opens the
solution/projects via MSBuildWorkspace, and produces loaded projects carrying the Compilation.
Any failure (no SDK, unrestored packages) sets `SemanticLoadSucceeded = false` and the caller
falls back to syntax indexing. This soft fallback is why most cloned repos load partial or
syntax rather than full semantic today; making it robust is the largest product item in the
v3.3 plan (N4), because the moat is only as valuable as how often it actually loads.

`SemanticAnalysisRunner.CreateDefault` wires the analyzers in order, each implementing
`ISemanticAnalyzer.Analyze`:

- **DiRegistrationAnalyzer** - AddScoped/Singleton/Transient and TryAdd variants, generic
  `<TService,TImpl>`, self-registration, typeof pairs, factory-lambda implementation recovery.
  Emits `di_resolves_to` and Scrutor `di_decorates`.
- **ConstructorInjectionAnalyzer** - `di_injects`, `di_depends_on_impl`.
- **MediatRAnalyzer** - matches handler/request interfaces by simple name (real MediatR or a
  local equivalent both work); emits `mediatr_handles` and `sends_request`.
- **AspNetRouteAnalyzer** and **EndpointAnalyzer** - `route_handles`.
- **OptionsBindingAnalyzer** - `options_binds`, `options_consumes`.
- **InterfaceImplementationAnalyzer** - `implements`, `inherits`.
- **HostedServiceAnalyzer** - `hosted_service`.
- **PipelineBehaviorAnalyzer** - `pipeline_behavior`.
- **EfCoreAnalyzer** - `ef_entity`, `ef_configures`.

These ten analyzers target first-party Microsoft and common-library patterns. Third-party
containers and frameworks (Autofac, Lamar, Wolverine, FastEndpoints, Carter, source-generated
DI) are not yet covered; widening that coverage is the community on-ramp in the v3.3 plan (G2).

The seam story: extraction is provider-driven at the syntax tier (`ILanguageSyntaxProvider`
claims extensions and extracts symbols/chunks with no compiler; C# is `CSharpSyntaxProvider`,
plus Python and JS/TS regex providers), and each file carries a `language` tag. The semantic
(typed-graph) tier is still C#/Roslyn-only: the semantic-tier provider seam is not yet
load-bearing. So today a second language reaches the syntax floor but not the wiring graph.

### 6.1 The tiers: cold-start syntax versus full semantic

Three indexer entry points:

- `IndexAsync` - full synchronous pass (semantic, or syntax fallback, plus co-change mining).
- `IndexSyntaxFirstAsync` - syntax tier only, no MSBuild load, no embedding, serves in a few
  seconds; sets index_mode syntax and semantic_pending 1.
- `UpgradeToSemanticAsync` - calls IndexAsync, which clears the pending flag when the full graph
  and embeddings land.

Host orchestration reads `FUSE_BG_UPGRADE` (off by default). When enabled in the serve host, a
cold read serves syntax-first then schedules a fire-and-forget upgrade on a fresh store handle,
guarded per root so two cold calls do not both upgrade. It is off by default because a detached
background task proved easy to orphan and race teardown; the CLI `fuse index` is always a
synchronous full pass. Documented cold first answer: about 20 s syntax versus about 70 s full.
The v3.3 plan (N3) makes syntax-first the default, moves the upgrade under a supervised
resident-workspace lifecycle, and gives incremental re-index a semantic path so an edited edge
is fresh within about a second.

---

## 7. The surfaces: CLI, MCP server, VS Code host

One executable (`Fuse.Cli`) hosts three surfaces over one shared engine (`SemanticRetrievalEngine`
plus `WorkspaceIndexStore`), so agent, developer UI, and CLI see identical data. The command
framework is DotMake.CommandLine.

### 7.1 CLI commands

`index`, `map`, `localize`, `resolve`, `context`, `review`, `find`, `diagnostics`, `reduce`,
`init`, `models`, `eval`, `update`, `host`, and `mcp` (with `mcp install` and `mcp serve`).
`update` solves the self-update file-lock problem for a global tool: a running tool locks its own
files on Windows, so it stops other running Fuse hosts and launches a detached platform-native
updater script that waits for the process to exit before replacing files.

### 7.2 The MCP tools (nine)

All defined on the partial class `FuseTools`, each `[McpServerTool]`, returning descriptive error
strings rather than throwing, and building the index on first use.

- `fuse_index` (not read-only) - build or refresh the index; returns a text summary.
- `fuse_map` - workspace map of symbols, routes, counts.
- `fuse_find` - exact lookup by symbol, path, or text.
- `fuse_reduce` - compact a known file set or raw content at a chosen level.
- `fuse_localize` - rank candidate files/symbols for a task, no bodies; carries the graded
  signal-sufficiency contract.
- `fuse_neighbors` - graph neighborhood of a file, callers/implementers of a symbol, or the
  central files of an area.
- `fuse_resolve` - deterministic wiring resolution: service to impl, request to handler, route to
  action, config to options, symbol to declaration. No bodies.
- `fuse_context` - plan and emit scoped, reduced source (bodies, mixed tiers, manifest,
  provenance) for selected seeds; a `sessionId` elides files already sent.
- `fuse_review` - diff-first semantic impact since a git ref: changed files plus blast radius
  (callers, DI consumers, route/request handlers, options consumers, tests) plus packed context.

A deprecation shim (`FuseDeprecatedTools`) keeps the retired V2 tool names registered (fuse_toc,
fuse_skeleton, fuse_search, fuse_focus, fuse_changes, fuse_ask, fuse_dotnet, fuse_generic) as
no-ops that return an actionable "renamed in V3" message pointing at the replacement, so an
upgrade never shows a bare "Unknown tool". MCP resources cover the four workflow reads (map,
localize, context, review) in a fixed-default addressable form. The v3.3 plan (R3) reshapes this
surface to five oracle-shaped tools (fuse_ask, fuse_check, fuse_impact, fuse_context, fuse_review)
and keeps a shim for every folded name.

### 7.3 The VS Code host (JSON-RPC)

`fuse host` is a long-lived process, one per repo root, sharing the same engine. Transport is a
named pipe on Windows, a Unix domain socket elsewhere; the endpoint address is a stable SHA-256
of the normalized repo-root path (8 bytes hex), mirrored in the TS client so a second editor
window finds the running host. Methods (`[JsonRpcMethod]`, `fuse/` namespace): handshake, stats,
index, graph, scope, explain, diagnostics, shutdown. Every method except handshake is gated by a
per-session random token. A debounced file watcher broadcasts `fuse/invalidated` to connected
editors.

Change-safety invariant: any change to a `Fuse.Cli.Rpc` DTO or `[JsonRpcMethod]` signature must
bump both `FuseHostService.ProtocolVersion` and `ext/vscode/src/host/protocol.ts`
`PROTOCOL_VERSION` in the same change, and update the extension client and the contract test. The
version constant exists to surface a stale-extension or new-host mismatch cleanly. As of 3.2.0
both sides declare protocol 3 (the earlier drift where the extension lagged at protocol 2 is
resolved).

---

## 8. Known issues and open questions

These were found while assembling this overview. Each is tagged with its current status and, if
open, the v3.3 item that addresses it.

1. **Host protocol version drift.** RESOLVED in 3.2.0. Both `FuseHostService.ProtocolVersion` and
   the TypeScript `PROTOCOL_VERSION` now declare 3, and the contract test guards them. The v3.3
   plan proposes generating the TS mirror from the C# source so the invariant becomes a build
   failure rather than a code-review catch, but the immediate drift is gone.
2. **BM25 weight vector versus intent comment.** OPEN, addressed by v3.3 N1. The FTS5 bm25 column
   weight for `path` is the highest, while the adjacent comment and the classic in-memory BM25F
   describe name/signature/symbols as the fields that should outrank path. The literal is what
   executes. N1 corrects the vector to match the documented intent and lands a ranking regression
   suite.
3. **Two disagreeing lexical rankers.** OPEN, addressed by v3.3 N2. The classic fusion path uses
   the in-memory BM25F (symbol boost 5x, path 3x) while the persistent path uses FTS5 bm25 with a
   different weighting. N2 deletes the in-memory ranker and routes the classic query mode through
   the persistent index, leaving one weight table guarded by N1's suite.
4. **Corpus generational gap in recorded results.** OPEN, addressed by v3.3 N2 and N5. Several
   result files still reference the prior 5-library corpus (AutoMapper, FluentValidation, MediatR,
   Newtonsoft, Serilog) that predates the V3.1 rebuild. Under the project's own convention a
   recorded number whose corpus no longer exists is a fabrication with provenance; N2 regenerates
   what is cheap and archives the rest, and N5 removes the parallel legacy harness that produced
   some of them.
5. **Incremental re-index does not refresh semantic edges.** OPEN, addressed by v3.3 N3.
   `ReindexFileAsync` re-extracts a file's syntax rows only; cross-file graph edges (DI, route,
   MediatR, EF) are recomputed only by a full `IndexAsync`. N3 adds a dependency-scoped semantic
   re-index over a resident compilation.

---

## 9. Measured results (source of truth)

All numbers come from `tests/benchmarks/results`, counted with `o200k_base`. The eval driver is
`tests/benchmarks/Fuse.Benchmarks`, invoked as
`fuse eval semantics|review|localize|agent|reduce|performance`. Never fabricate or weaken a
number. The honesty convention is a deliberate product value: the docs publish weaknesses, and no
head-to-head claim is made that the harness does not back.

- **Suite A, semantic resolution (`semantics.json`)**: on the OrderingApp wiring fixture the
  extracted graph matches the hand-built edge ground truth exactly, 22 of 22 edges, recall and
  precision 1.0. Covers DI registration and constructor injection, MediatR, ASP.NET routes,
  options binding, EF Core, Scrutor decoration, factory registration, hosted services, pipeline
  behaviors, minimal-API/gRPC/SignalR, plus precision edge cases (an explicit open generic,
  TryAdd, and a multiple-implementation ambiguity where only the registered impl resolves). This
  is the moat. It is proven in kind on a hand-built fixture; coverage over the diversity of real
  third-party wiring is the acknowledged gap (section 6 and v3.3 G2).
- **Suite B, change impact (`review.json`, 53 PRs, 25k budget, --restore)**: changed-file recall
  100 percent (by construction, changed files are must-keep seeds), precision 79.8 percent, F1
  0.89, median 958 and mean 1,095 returned tokens. Index modes across PRs: partial 27, semantic
  1, syntax 25. A grep baseline reaches 53 percent recall at 14 percent precision. The
  discriminating signal here is precision, and it is strong.
- **Suite C, open-ended localization (`localize.json`, 53 PRs, title only, --restore, dense on)**:
  overall changed-file recall about 15 percent at 8.1 percent precision, median 1,033 tokens; by
  bucket identifier-rich 21 percent, natural-language domain 17 percent. Contract metrics:
  low-signal detection F1 1.0, false-rejection on answerable queries 0.0 percent, precision when
  confident 9.3 percent. Dense lifts recall over the lexical fallback (13.3 to 15.1 percent
  overall). The acknowledged ceiling: the localize main checkout loads mostly syntax (partial 2,
  syntax 2), so the graph-dependent recall is barely exercised. Whether recall is bounded by index
  mode is a hypothesis the harness has not yet tested with semantic mode on; v3.3 N4 forces that
  test.
- **Suite D, agent context sufficiency (`agent.json`, model-dependent, not byte-reproducible)**:
  one Claude Code CLI driver (claude-sonnet-4-6) over 12 PRs in two arms (native file tools, Fuse
  MCP tools), one rollout each. Fuse 30 percent mean file recall at a median 211,502 cumulative
  tokens; native 26 percent at 209,182; precision 44 versus 43 percent. A small, wide-CI sample,
  and the token cost was effectively flat: per-payload reduction did not reduce the session total,
  because the agent still explores and still runs builds. v3.3 R4 rebuilds this suite to measure
  iterations-to-green and build-invocations, the metrics the oracle thesis actually moves.
- **Peer comparison (`layer6-peers.json`, 12 PRs; model-driven arms bounded to 4)**: fuse 19
  percent recall at 19 percent precision, codegraph 9 percent at 11 percent, coa-codesearch 9
  percent at 1 percent, serena 34 percent at 27 percent. Token columns are not comparable (fuse and
  codegraph return source; coa and serena return path/snippet pointers). Serena's aggregate is
  dominated by a tiny-repo outlier on its 4-PR sample; on the substantive PRs Fuse leads or ties.
  No head-to-head ranking is claimed beyond this caveated reading.
- **Suite E, token reduction and fidelity (offline)**: the Roslyn skeleton keeps every public and
  protected type and 99 to 100 percent of public methods while removing 37 to 55 percent of tokens
  at skeleton level and 47 to 60 percent at public-API level. Fidelity is counted by parsing raw
  source with Roslyn as independent ground truth, so it is not circular.
- **Warm latency and cold start (`performance.json`, NodaTime, syntax mode)**: cold syntax tier
  about 19.9 s, semantic-ready after a further about 94.9 s; cold full semantic index about
  69.7 s. Warm localize P50 60.9 / P95 71.2 ms; resolve sub-millisecond; review plan P50 109.9 ms;
  single-file incremental re-index P50 22.8 ms. Timings are environment-dependent.

Honest gaps stated in the docs: the model-driven peer comparison at full scale (50 to 100 PRs) and
full task resolution (apply a patch, run a test oracle, score pass@1 across arms) are
compute-bounded and not run at scale. Most corpus repos load syntax or partial in this
environment, so the corpus suites sit below the Suite A semantic ceiling.

---

## 10. The corpus and eval methodology

Corpus (`tests/benchmarks/corpus.json`, tokenizer o200k_base): Scrutor (45 cs files, DI and
decoration wiring, the moat), Ardalis Specification (233 files, the specification pattern over
EF), NodaTime (488 files, a large PR history), eShopOnWeb (254 files, the only full application,
exercises the semantic blast radius), and the in-repo SampleShop and OrderingApp fixtures. Repos
are cloned --no-checkout then detached at a pinned commit.

Ground truth (`prs.json`): real merged PRs reconstructed by walking merge commits (parent 1 base,
parent 2 head), keeping diffs of 2 to 25 C# files, dropping maintenance and dependency-bump titles
that cannot locate their own diff. Titles are classified into signal buckets (no-signal,
dependency-bump, config-ci, formatting, route-api, test-only, identifier-rich, nl-domain); only
no-signal is treated as low-signal (the honest answer there is to abstain). Metrics include a
deterministic percentile bootstrap CI (seed 1469, 2000 iterations) for small-N confidence.

Two harnesses currently coexist: the C# `Fuse.Benchmarks` library driven by `fuse eval`
(the authoritative one), and a legacy PowerShell harness (`harness/layerN.ps1`, `run-all.ps1`)
that still owns a few metrics and the peer run. The peer harness (`harness/layer6-peers.ps1`)
runs fuse versus codegraph (offline graph) versus coa-codesearch (Lucene, MCP) versus serena
(LSP-backed); deterministic arms scale to any sample, model-driven arms run one bounded claude
rollout per PR (haiku-4-5). Peers are omitted, never stubbed, when absent; all MCP calls are
bounded with a wall-clock backstop and process-tree kill so no server orphans. The v3.3 plan (N5)
consolidates onto the single C# harness.

---

## 11. The roadmap so far

The internal plans live under `roadmap/`, newest last. Convention: one item equals engine plus
tests plus website docs plus a benchmark in a single change, three gates green (build, test,
format), every number sourced, weaknesses published.

- **v3-plan.md** (the big wave, R0 through R9): built the .NET semantic moat (wiring resolution),
  hybrid retrieval, the abstention contract, the wider analyzer set, warm millisecond latency, and
  the peer and agent runs. Carries the archived V3 overhaul record.
- **v3.1-plan.md**: sharpened the open-ended floor. Strategic pivots: refuse-and-route on low
  signal (graded confident/partial/insufficient), and retire the no-model floor (bundle a local
  dense embedder, on by default, offline at query time, lexical as fallback). Landed the
  signal-sufficiency contract, subword indexing, stemming plus a comment bridge, graph-aware
  discovery, exploration primitives, dense-by-default, the peer harness at scale, the corpus
  rebuild, and the git co-change signal. Deferred the resident workspace, dependency-scoped
  freshness, the semantic-tier seam, tree-sitter, learning-to-rank, and a thesaurus.
- **v3.2-plan.md**: the warm-server finishing wave. Shipped the host-on-semantic-index migration
  (protocol 3) and the rich index panel. Left two of its highest-value items only partly landed:
  the resident Roslyn workspace with dependency-scoped freshness (W1) and semantic-mode corpus
  coverage (W4). Those are picked up in v3.3.
- **v3.3-plan.md** (current forward plan): the compiler-oracle release. Repositions Fuse from a
  retrieval tool into the .NET agent's ground-truth oracle, in three phases under one version: a
  trustworthy floor (fix the ranking inversion, unify the rankers, land the resident oracle,
  make semantic mode load on real checkouts, retire the legacy harness), the oracle itself
  (speculative diagnostics, blast radius, a reshaped tool surface, a loop-measuring benchmark),
  and a moonshot (a speculative staging area that typechecks and test-selects a change before it
  touches disk). Its rationale is summarized in section 12 below and captured in each item's
  own "Why" paragraph in the plan.

The stance the plans keep returning to: open-ended recall is bounded by index mode (whether the
checkout loads semantically), not by ranking cleverness; the moat is the deterministic wiring
graph; and the product does not have to win the vague one-shot because it is in a loop with a
model. The v3.3 plan adds one more: token efficiency per payload is not the product, fewer and
shorter agent iterations is, and the way to get there is to answer the questions the agent would
otherwise run a build to answer.

---

## 12. Where Fuse is going (the thesis)

An adversarial critique and roadmap, now carried into [v3.3-plan.md](v3.3-plan.md), argue one
thesis:

Fuse should stop being a retrieval tool that happens to hold a Roslyn compilation and become the
.NET agent's ground-truth oracle, the tool that answers the questions only a compiler can answer,
at edit speed. The one-line pitch: Fuse gives your agent the compiler.

The reasoning, from the evidence in this overview: retrieval is crowded and Fuse is not winning it
(about 15 percent open-ended recall), but the unique asset is the warm, typed, whole-solution
Roslyn compilation, which today is used only to extract wiring edges into SQLite at index time.
That same compilation can answer, in milliseconds, does this proposed edit typecheck, what breaks
if this signature changes, which tests cover this symbol. Every .NET agent user lives the loop
that oracle collapses: edit, guess an API shape, run `dotnet build`, wait tens of seconds, parse
errors, retry. Suite D's flat token result is exactly what "context in, but the verify loop
unchanged" looks like. The way to change the session-level number is not better input context; it
is replacing build-loop iterations with millisecond oracle calls.

The thesis subsumes the current strengths rather than discarding them (resolve and review are
already oracle queries), and it forces the real fix for the real defect: if the product is the
compilation, then making MSBuild load reliably on an arbitrary cloned repo is core engineering,
not benchmark plumbing. The honest ceiling is stated plainly in the plan: every oracle answer is
gated on the semantic tier actually loading, so the realized value is the theoretical gain times
the load-success rate, and lifting that rate (item N4) is the release's dominant uncertainty.

For the concrete engine work, the way each piece is measured honestly, and the risk that kills
each one, read [v3.3-plan.md](v3.3-plan.md). Every item there traces its rationale back to the
analysis in this overview.
