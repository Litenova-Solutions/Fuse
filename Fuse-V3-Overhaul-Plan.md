# Fuse V3 Overhaul Plan

## Execution checklist

Work top to bottom. Each item has a stable ID. Check its box here when done, and append a timestamped entry for it to the Progress Log at the end of this file. Do not start an item until the one above it is green, unless the plan marks it independent.

Phase 0 - Foundation
- [x] P0.1 Create projects: Core/Fuse.Indexing, Core/Fuse.Semantics, Core/Fuse.Retrieval (and Fuse.Context if separate)
- [x] P0.2 Wire project references; add Microsoft.CodeAnalysis.Workspaces.MSBuild and Microsoft.Build.Locator
- [x] P0.3 Add new test projects (16.1) wired into Fuse.slnx; solution builds, tests run

Phase 1 - SQLite schema and store
- [x] P1.1 WorkspaceIndexStore + schema creation/migration (drop-and-rebuild below version 10)
- [x] P1.2 Table models + transactional upsert/delete; reuse FuseStorePaths and WAL patterns
- [x] P1.3 FTS5 indexing + search
- [x] P1.4 Fuse.Indexing.Tests: insert files/symbols/chunks, FTS finds OrderService, reindex changed file, edges persist

Phase 2 - Syntax-level semantic batch
- [x] P2.1 File discovery + hash records (reuse FileCollectionPipeline)
- [x] P2.2 Syntax symbol + chunk extraction into store (reuse RoslynSymbolChunkExtractor)
- [x] P2.3 Route syntax extraction into records (reuse RoslynRouteMapGenerator logic)
- [x] P2.4 fuse index a simple repo; fuse map --detail symbols shows DB symbols; FTS returns chunks

Phase 3 - MSBuild/Roslyn semantic indexing
- [x] P3.1 DotNetWorkspaceDiscoverer (discovery order, ignore rules)
- [x] P3.2 RoslynWorkspaceLoader; MSBuildLocator guarded once; syntax fallback on load failure
- [x] P3.3 Semantic symbol extraction + SymbolIdBuilder (IDs stable across runs)
- [x] P3.4 Project records + file linkage; index status Semantic or Partial on a real .sln/.csproj

Phase 4 - Core semantic edges
- [x] P4.1 Shared semantic fixture app (16.2)
- [x] P4.2 InterfaceImplementationAnalyzer
- [x] P4.3 DiRegistrationAnalyzer
- [x] P4.4 ConstructorInjectionAnalyzer
- [x] P4.5 MediatRAnalyzer
- [x] P4.6 AspNetRouteAnalyzer
- [x] P4.7 OptionsBindingAnalyzer
- [x] P4.8 Fixture edge assertions pass; fuse resolve --service/--request/--route return correctly

Phase 5 - Retrieval engine v2
- [x] P5.1 LocalizationRequest + candidate generators (exact symbol/route/service/request/config, FTS, path, changed files)
- [x] P5.2 Score normalization
- [x] P5.3 Graph expansion with typed edge weights + pruning
- [x] P5.4 Context plan output; fuse localize and fuse context acceptance pass; no QueryScopingPipeline needed

Phase 6 - Review/change impact
- [x] P6.1 Wire GitChangeDetector/GitDiffParser; changed files become must-keep seeds
- [x] P6.2 Per-changed-file semantic impact (symbols, callers, DI consumers, routes, handlers, options, tests, co-change)
- [x] P6.3 Review preamble + packed context; fuse review --changed-since works and explains every non-changed file

Phase 7 - Context rendering
- [x] P7.1 SemanticContextRenderer with tiered reduction
- [x] P7.2 Semantic manifest preamble + per-file provenance
- [x] P7.3 XML/Markdown/JSON + structural maps; redaction on; token budget respected

Phase 8 - CLI rewrite
- [ ] P8.1 New command set (index/map/localize/resolve/context/review/diagnostics/find/reduce/host/mcp/models)
- [ ] P8.2 Remove or alias old commands; Section 9.1 manual examples run end to end

Phase 9 - MCP rewrite
- [ ] P9.1 Replace tools with the eight (10.1); rewrite server instructions (10.3)
- [ ] P9.2 Compact localize/resolve; context consumes localize/resolve seeds; add session support
- [ ] P9.3 Update mcp-registry manifest to the new tool list

Phase 10 - Tests, docs, benchmarks
- [ ] P10.1 Tests overhaul complete (Section 16.5 definition of done)
- [ ] P10.2 Docs overhaul complete (Section 17.6 definition of done)
- [ ] P10.3 fuse eval + suites A-D; offline PowerShell layers updated to new surface (Section 18)
- [ ] P10.4 Regenerate benchmark figure; resync AGENTS.md and docs numbers (18.12, 18.13)

Phase 11 - Publish V3
- [ ] P11.1 CI green: build, test (count risen), format, publish smoke for win-x64/linux-x64 incl. FTS5 and MSBuild-absent fallback
- [ ] P11.2 Finalize 3.0.0 + changelog; RPC ProtocolVersion and protocol.ts PROTOCOL_VERSION and extension client in sync
- [ ] P11.3 Update MCP Registry manifest and install flow
- [ ] P11.4 Open PR via gh for review (no merge, no self-approve, no auto-merge)

## Status and intent

Fuse already carries the version `3.0.0` in `src/Host/Fuse.Cli/Fuse.Cli.csproj`, but V3 has never been published. This plan finishes V3: it overhauls what Fuse is, then ships it. There is no backward-compatibility requirement. We can remove commands, replace the MCP tool surface, change the on-disk schema, and break the output format.

The goal is to turn Fuse from a "context optimizer with BM25 and reduction" into a warm, persistent, Roslyn/MSBuild-backed .NET semantic context engine for agents, centered on:

1. PR/change impact.
2. Semantic wiring resolution.
3. Iterative localize -> resolve -> expand -> emit workflows.
4. Persistent indexing.
5. Typed graph retrieval.
6. Token reduction as a rendering/transport feature, not the product core.

This document is the single source of truth for the overhaul. It covers the engine, the CLI, the MCP surface, the warm host, and equally the test suite, the documentation site, and the benchmark harness, because all four must move together for V3 to be publishable.

### What already exists (do not rebuild blindly)

The exploration that produced this plan confirmed the current state. Reuse these; do not assume a clean slate:

- A SQLite cache already lives at `.fuse/fuse.db` in WAL mode, via `SqliteKeyValueStore` (`src/Core/Fuse.Reduction/Caching`), `SqliteRelevancePostingsStore` (`src/Core/Fuse.Fusion/Indexing`), and `FuseStorePaths`. The overhaul replaces this schema with a richer semantic schema; it is a migration of an existing store, not a greenfield add.
- A warm host already exists: `FuseHostService` plus named-pipe/Unix-socket RPC, an invalidation watcher, and graph/scope/explain endpoints (`src/Host/Fuse.Cli/Host/Rpc`). The overhaul promotes it to the primary runtime.
- Roslyn structural assets exist and are strong: `RoslynSkeletonExtractor`, `RoslynSymbolSliceExtractor`, `RoslynSymbolChunkExtractor`, `RoslynRouteMapGenerator`, `ThinSkeletonAssembler`. Reuse them.
- Reduction and emission exist: `ContentReductionPipeline`, `EmissionPipeline`. Keep them as rendering, not retrieval.
- Git plumbing exists: `GitChangeDetector`, `GitDiffParser`, `GitStatsProvider` (already chunks its git calls to stay under OS arg limits).
- `ContextPlanBuilder`, `CommentExtractor`, `DependencyGraphBuilder`, `QueryScopingPipeline`, `PseudoRelevanceExpander` all exist in `src/Core/Fuse.Fusion/Scoping`. The overhaul replaces the query-scoping path; the bare-type dependency graph survives only as a syntax fallback.

The new system should answer:

```text
What handles POST /api/billing?
What implementation is injected for IFoo here?
What MediatR handler processes CreateOrderCommand?
What files does this PR semantically impact?
What tests likely cover this changed service?
What minimal context should an agent read before editing this feature?
```

---

# 0. North-star product

Fuse becomes:

> A Roslyn-backed .NET semantic context engine for AI agents. Fuse resolves what code actually runs, what gets injected, what endpoint handles a route, what handler processes a request, and what a git diff semantically impacts. It serves precise, provenance-backed context from a warm persistent index.

Not:

> A generic codebase token compressor.

---

# 1. Target architecture

## 1.1 High-level architecture

```text
                         +--------------------------+
                         | CLI / MCP / IDE / CI     |
                         +------------+-------------+
                                      |
                                      v
                         +------------+-------------+
                         | Fuse Host / Engine       |
                         | warm per workspace       |
                         +------------+-------------+
                                      |
                +---------------------+----------------------+
                |                                            |
                v                                            v
   +------------+-------------+                 +------------+-------------+
   | Index Coordinator        |                 | Retrieval / Context      |
   | - file discovery         |                 | - localize               |
   | - hashing                |                 | - resolve semantic node  |
   | - project loading        |                 | - graph expand/prune     |
   | - incremental updates    |                 | - pack/render            |
   +------------+-------------+                 +------------+-------------+
                |
                v
   +------------+-------------+
   | Semantic Index Store     |
   | SQLite/WAL               |
   | - files                  |
   | - projects               |
   | - symbols                |
   | - chunks                 |
   | - routes                 |
   | - DI registrations       |
   | - options bindings       |
   | - graph edges            |
   | - FTS index              |
   | - co-change graph        |
   +--------------------------+
```

## 1.2 Major conceptual change

Old model:

```text
collect files -> build graph in memory -> build BM25 in memory
-> rank files -> one-hop expand -> reduce -> emit
```

New model:

```text
warm persistent index -> semantic localize -> typed graph resolve
-> weighted expansion with pruning -> context plan
-> render source/skeleton/map/review
```

---

# 2. New top-level capabilities

## 2.1 `fuse_review`

Diff-first semantic impact. The flagship.

Input: `root`, `changedSince`, `maxTokens`, `includeTests`, `includeConfig`, `includeCallers`.

Output: changed files, diff hunks, semantic blast radius, direct callers, DI consumers, route impact, MediatR/request impact, options/config impact, likely tests, final packed context, and provenance for every included file.

## 2.2 `fuse_resolve`

Deterministic semantic resolver.

```text
fuse_resolve --symbol IFoo
fuse_resolve --route "POST /api/billing"
fuse_resolve --request CreateOrderCommand
fuse_resolve --service IOrderService
fuse_resolve --config Payments
```

Output: matched semantic node(s), resolved implementations/handlers/consumers, file paths, signatures, evidence, confidence. No source bodies by default.

## 2.3 `fuse_localize`

Cheap candidate discovery. The first step of iterative agent workflows.

Input: `task/query`, optional `route`, `symbol`, `changedSince`, `stackTrace`, `selectedFiles`, `maxCandidates`.

Output: ranked files/symbols/chunks, snippets/signatures, reasons, estimated token costs, suggested next expansions.

## 2.4 `fuse_context`

Read/expand selected semantic context.

Input: `root`, `seeds` (files/symbols/routes/services/requests), `depth`, `maxTokens`, `renderMode` (source|reduced|skeleton|publicApi|mixed), `includeTests`, `includeConfig`.

Output: reduced/full source payload, semantic map preamble, provenance, packed under budget.

## 2.5 `fuse_map`

Workspace map. Cheap first call.

Input: `root`, `detail` (directories|projects|routes|di|symbols|all), `maxTokens`.

Output: project graph, route map, DI map, public API skeleton, token costs, semantic hot spots.

---

# 3. Repository/project restructuring

Current solution:

```text
Core/Fuse.Collection
Core/Fuse.Reduction
Core/Fuse.Emission
Core/Fuse.Fusion
Host/Fuse.Cli
Plugins/...
```

New structure:

```text
Core/Fuse.Indexing
Core/Fuse.Semantics
Core/Fuse.Retrieval
Core/Fuse.Context
Core/Fuse.Collection
Core/Fuse.Reduction
Core/Fuse.Emission
Host/Fuse.Cli
Plugins/...
```

`Fuse.Fusion` is retired as the orchestrator; its surviving pieces (reduction integration, git plumbing, comment extraction) move to the new projects or stay as named fallbacks. For a full overhaul, keep these as separate projects rather than namespaces under one.

## 3.1 `Fuse.Indexing`

SQLite schema and migrations; workspace DB lifecycle; file hashes and Merkle tree; persistent FTS; graph edge storage; query APIs over the index.

```csharp
public interface IWorkspaceIndexStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct);
    Task<WorkspaceIndexState> GetStateAsync(CancellationToken ct);
    Task UpsertFilesAsync(IReadOnlyList<IndexedFileRecord> files, CancellationToken ct);
    Task UpsertProjectsAsync(IReadOnlyList<ProjectRecord> projects, CancellationToken ct);
    Task UpsertSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken ct);
    Task UpsertChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct);
    Task UpsertEdgesAsync(IReadOnlyList<SemanticEdgeRecord> edges, CancellationToken ct);
    Task UpsertRoutesAsync(IReadOnlyList<RouteRecord> routes, CancellationToken ct);
    Task UpsertDiRegistrationsAsync(IReadOnlyList<DiRegistrationRecord> registrations, CancellationToken ct);
    Task UpsertOptionsBindingsAsync(IReadOnlyList<OptionsBindingRecord> bindings, CancellationToken ct);
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct);
    Task<IReadOnlyList<SemanticEdgeRecord>> GetOutgoingEdgesAsync(string nodeId, CancellationToken ct);
    Task<IReadOnlyList<SemanticEdgeRecord>> GetIncomingEdgesAsync(string nodeId, CancellationToken ct);
}
```

This supersedes `SqliteKeyValueStore` and `SqliteRelevancePostingsStore` as the canonical store. Keep their connection-pooling and WAL retry patterns; reuse `FuseStorePaths` for path resolution.

## 3.2 `Fuse.Semantics`

Project discovery; MSBuild/Roslyn workspace loading; syntax fallback if semantic load fails; symbol extraction; semantic edge extraction; route/DI/MediatR/options/test analyzers.

```csharp
public interface ISemanticIndexer
{
    Task<SemanticIndexBatch> IndexAsync(
        WorkspaceIndexRequest request,
        CancellationToken cancellationToken);
}

public sealed record WorkspaceIndexRequest(
    string RootDirectory,
    IReadOnlyList<string>? ChangedPaths = null,
    bool ForceFull = false);

public sealed record SemanticIndexBatch(
    IReadOnlyList<IndexedFileRecord> Files,
    IReadOnlyList<ProjectRecord> Projects,
    IReadOnlyList<SymbolRecord> Symbols,
    IReadOnlyList<ChunkRecord> Chunks,
    IReadOnlyList<SemanticEdgeRecord> Edges,
    IReadOnlyList<RouteRecord> Routes,
    IReadOnlyList<DiRegistrationRecord> DiRegistrations,
    IReadOnlyList<OptionsBindingRecord> OptionsBindings,
    IReadOnlyList<DiagnosticRecord> Diagnostics);
```

## 3.3 `Fuse.Retrieval`

Localize, resolve, graph expansion, candidate scoring, packing.

```csharp
public interface IContextRetrievalEngine
{
    Task<LocalizationResult> LocalizeAsync(LocalizationRequest request, CancellationToken ct);
    Task<ResolveResult> ResolveAsync(ResolveRequest request, CancellationToken ct);
    Task<ContextPlanResult> PlanContextAsync(ContextRequest request, CancellationToken ct);
    Task<ReviewContextResult> ReviewAsync(ReviewRequest request, CancellationToken ct);
}
```

## 3.4 `Fuse.Context`

Convert `ContextPlan` into emitted context: mixed rendering, provenance comments, semantic map preambles, reduction integration. May stay folded into the renderer rather than a separate project if that proves simpler.

---

# 4. SQLite schema

Use `.fuse/fuse.db`. Set:

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 30000;
```

## 4.1 Schema versioning

```sql
CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);
```

Initial overhaul schema version: `10`. Since no backward compatibility is required, the migration is: if `schema_version < 10`, drop all Fuse-owned tables and rebuild. The existing cache DB carries a lower or absent version, so it is dropped on first V3 run.

## 4.2 Files

```sql
CREATE TABLE files(
  file_id INTEGER PRIMARY KEY,
  path TEXT NOT NULL UNIQUE,
  normalized_path TEXT NOT NULL UNIQUE,
  extension TEXT NOT NULL,
  size_bytes INTEGER NOT NULL,
  mtime_utc_ticks INTEGER NOT NULL,
  content_hash TEXT NOT NULL,
  project_id INTEGER NULL,
  is_generated INTEGER NOT NULL DEFAULT 0,
  is_test INTEGER NOT NULL DEFAULT 0,
  indexed_at_utc TEXT NOT NULL
);
CREATE INDEX idx_files_hash ON files(content_hash);
CREATE INDEX idx_files_project ON files(project_id);
CREATE INDEX idx_files_extension ON files(extension);
```

## 4.3 Projects

```sql
CREATE TABLE projects(
  project_id INTEGER PRIMARY KEY,
  path TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  assembly_name TEXT NULL,
  target_framework TEXT NULL,
  project_hash TEXT NOT NULL,
  indexed_at_utc TEXT NOT NULL
);
```

## 4.4 Semantic nodes

Unify symbols/routes/config/files as graph nodes.

```sql
CREATE TABLE nodes(
  node_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  display_name TEXT NOT NULL,
  file_id INTEGER NULL,
  project_id INTEGER NULL,
  symbol_id TEXT NULL,
  stable_key TEXT NOT NULL,
  start_line INTEGER NULL,
  end_line INTEGER NULL,
  signature TEXT NULL,
  metadata_json TEXT NULL
);
CREATE INDEX idx_nodes_kind ON nodes(kind);
CREATE INDEX idx_nodes_file ON nodes(file_id);
CREATE INDEX idx_nodes_display ON nodes(display_name);
CREATE INDEX idx_nodes_symbol ON nodes(symbol_id);
```

Node kinds: `file`, `project`, `type`, `method`, `property`, `field`, `constructor`, `interface`, `route`, `service`, `config`, `test`, `chunk`.

## 4.5 Symbols

```sql
CREATE TABLE symbols(
  symbol_id TEXT PRIMARY KEY,
  file_id INTEGER NOT NULL,
  project_id INTEGER NULL,
  kind TEXT NOT NULL,
  name TEXT NOT NULL,
  fully_qualified_name TEXT NOT NULL,
  metadata_name TEXT NULL,
  containing_type TEXT NULL,
  namespace TEXT NULL,
  assembly_name TEXT NULL,
  accessibility TEXT NULL,
  signature TEXT NULL,
  start_line INTEGER NOT NULL,
  end_line INTEGER NOT NULL,
  is_public_api INTEGER NOT NULL DEFAULT 0,
  FOREIGN KEY(file_id) REFERENCES files(file_id) ON DELETE CASCADE
);
CREATE INDEX idx_symbols_name ON symbols(name);
CREATE INDEX idx_symbols_fqn ON symbols(fully_qualified_name);
CREATE INDEX idx_symbols_file ON symbols(file_id);
CREATE INDEX idx_symbols_kind ON symbols(kind);
```

Symbol ID format: `symbol:{assembly}:{kind}:{metadataName}:{signatureHash}`. Source-only fallback: `symbol:fallback:{path}:{kind}:{name}:{line}`.

## 4.6 Chunks

```sql
CREATE TABLE chunks(
  chunk_id TEXT PRIMARY KEY,
  file_id INTEGER NOT NULL,
  symbol_id TEXT NULL,
  kind TEXT NOT NULL,
  name TEXT NULL,
  stable_key TEXT NOT NULL,
  start_line INTEGER NOT NULL,
  end_line INTEGER NOT NULL,
  text_hash TEXT NOT NULL,
  token_estimate INTEGER NOT NULL,
  reduced_token_estimate INTEGER NOT NULL,
  signature TEXT NULL,
  outline TEXT NULL,
  FOREIGN KEY(file_id) REFERENCES files(file_id) ON DELETE CASCADE
);
CREATE INDEX idx_chunks_file ON chunks(file_id);
CREATE INDEX idx_chunks_symbol ON chunks(symbol_id);
CREATE INDEX idx_chunks_kind ON chunks(kind);
```

## 4.7 FTS

Use SQLite FTS5.

```sql
CREATE VIRTUAL TABLE chunk_fts USING fts5(
  chunk_id UNINDEXED,
  path, name, symbols, signature, comments, body,
  tokenize = 'unicode61'
);
```

If FTS5 is unavailable in the packaged runtime, fall back to Lucene.NET. Implement FTS5 first and prove it works under the self-contained publish (the CI smoke test must cover it).

## 4.8 Edges

```sql
CREATE TABLE edges(
  edge_id TEXT PRIMARY KEY,
  from_node_id TEXT NOT NULL,
  to_node_id TEXT NOT NULL,
  edge_type TEXT NOT NULL,
  weight REAL NOT NULL,
  confidence REAL NOT NULL,
  evidence TEXT NULL,
  evidence_file_id INTEGER NULL,
  evidence_start_line INTEGER NULL,
  evidence_end_line INTEGER NULL,
  metadata_json TEXT NULL,
  FOREIGN KEY(from_node_id) REFERENCES nodes(node_id) ON DELETE CASCADE,
  FOREIGN KEY(to_node_id) REFERENCES nodes(node_id) ON DELETE CASCADE
);
CREATE INDEX idx_edges_from ON edges(from_node_id);
CREATE INDEX idx_edges_to ON edges(to_node_id);
CREATE INDEX idx_edges_type ON edges(edge_type);
CREATE UNIQUE INDEX ux_edges_unique ON edges(from_node_id, to_node_id, edge_type, evidence_file_id);
```

Edge types: `declares`, `references`, `implements`, `inherits`, `di_registers`, `di_injects`, `di_resolves_to`, `route_handles`, `mediatr_handles`, `options_binds`, `options_consumes`, `calls`, `called_by`, `tests`, `cochanges`, `project_references`, `contains`, `path_proximity`.

## 4.9 Routes

```sql
CREATE TABLE routes(
  route_id TEXT PRIMARY KEY,
  http_method TEXT NOT NULL,
  route_pattern TEXT NOT NULL,
  handler_symbol_id TEXT NULL,
  file_id INTEGER NOT NULL,
  start_line INTEGER NOT NULL,
  end_line INTEGER NOT NULL,
  source_kind TEXT NOT NULL,
  metadata_json TEXT NULL
);
CREATE INDEX idx_routes_pattern ON routes(route_pattern);
CREATE INDEX idx_routes_method ON routes(http_method);
```

## 4.10 DI registrations

```sql
CREATE TABLE di_registrations(
  registration_id TEXT PRIMARY KEY,
  service_symbol_id TEXT NULL,
  implementation_symbol_id TEXT NULL,
  service_name TEXT NOT NULL,
  implementation_name TEXT NULL,
  lifetime TEXT NOT NULL,
  file_id INTEGER NOT NULL,
  start_line INTEGER NOT NULL,
  end_line INTEGER NOT NULL,
  registration_kind TEXT NOT NULL,
  confidence REAL NOT NULL,
  evidence TEXT NULL
);
CREATE INDEX idx_di_service ON di_registrations(service_name);
CREATE INDEX idx_di_impl ON di_registrations(implementation_name);
```

## 4.11 Options/config bindings

```sql
CREATE TABLE options_bindings(
  binding_id TEXT PRIMARY KEY,
  options_symbol_id TEXT NULL,
  options_name TEXT NOT NULL,
  config_section TEXT NULL,
  file_id INTEGER NOT NULL,
  start_line INTEGER NOT NULL,
  end_line INTEGER NOT NULL,
  binding_kind TEXT NOT NULL,
  confidence REAL NOT NULL,
  evidence TEXT NULL
);
CREATE INDEX idx_options_name ON options_bindings(options_name);
CREATE INDEX idx_options_section ON options_bindings(config_section);
```

## 4.12 Git co-change

```sql
CREATE TABLE git_cochange(
  path_a TEXT NOT NULL,
  path_b TEXT NOT NULL,
  count INTEGER NOT NULL,
  pmi REAL NOT NULL,
  jaccard REAL NOT NULL,
  last_seen_utc TEXT NULL,
  PRIMARY KEY(path_a, path_b)
);
CREATE INDEX idx_cochange_a ON git_cochange(path_a);
CREATE INDEX idx_cochange_b ON git_cochange(path_b);
```

---

# 5. Semantic indexing design

## 5.1 Project discovery

```csharp
public sealed class DotNetWorkspaceDiscoverer
{
    public Task<WorkspaceDiscoveryResult> DiscoverAsync(string root, CancellationToken ct);
}
```

Discovery order: `.sln`, then `.slnx`, then `.csproj`, then syntax-only fallback for `.cs`. Ignore `bin`, `obj`, `.git`, `.fuse`, `.vs`, `node_modules`. Prefer a single solution if exactly one exists; otherwise index all projects, or index `.csproj` directly.

## 5.2 MSBuild/Roslyn loading

Add packages:

```xml
<PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" />
<PackageReference Include="Microsoft.Build.Locator" />
```

```csharp
public sealed class RoslynWorkspaceLoader
{
    public Task<RoslynWorkspaceSnapshot> LoadAsync(string root, CancellationToken ct);
}
```

Call `MSBuildLocator.RegisterDefaults()` exactly once, guarded. On load failure: record a diagnostic, fall back to syntax index, mark index mode `Partial`.

Note the AOT/self-contained-publish tension: MSBuild loading depends on a resolvable SDK at runtime. The publish smoke test must verify behavior when MSBuild is unavailable (clean fall back to syntax mode), not just the happy path.

## 5.3 Stable symbol IDs

```csharp
public static class SymbolIdBuilder
{
    public static string Build(ISymbol symbol);
}
```

Use assembly identity, symbol kind, containing namespace/type, metadata name, method parameter type display, generic arity. Use `SymbolDisplayFormat.FullyQualifiedFormat`; include parameter types for methods, e.g. `My.Assembly|method|My.Namespace.Foo.Bar(System.String,System.Int32)`. Hash long strings.

## 5.4 Extract symbols

Per document: parse syntax root, get semantic model, collect classes, records, structs, interfaces, enums, methods, constructors, properties, fields, events. Store symbol ID, name, FQN, metadata name, accessibility, signature, source span, file ID, project ID.

## 5.5 Extract chunks

Chunk kinds: `file`, `type`, `method`, `constructor`, `property`, `route-handler`, `config`, `test`. Per chunk: stable chunk ID, file ID, symbol ID, start/end lines, signature, outline, body text or body sketch for FTS, token estimates. Index into FTS fields: `path`, `name`, `symbols`, `signature`, `comments`, `body`.

## 5.6 Comments

Reuse the existing `CommentExtractor`, but store fielded comments in FTS.

## 5.7 Generated/test detection

Test file if path contains `/Tests/`, `.Tests`, `/Test/`; references xUnit/NUnit/MSTest attributes; or class name ends with `Tests`/`Specs`. Generated if auto-generated markers, EF migration/model snapshot, or designer/generated patterns. Store flags on `files`.

---

# 6. Semantic analyzers

## 6.1 Interface and inheritance

Per `INamedTypeSymbol`: implemented interfaces and base type chain. Edges: `type -> interface : implements` (0.90), `type -> base : inherits` (0.75), and the reverse edges.

## 6.2 DI analyzer

Analyze invocation expressions. MVP must support:

```text
AddScoped<TService,TImplementation>  AddTransient<...>  AddSingleton<...>
AddScoped<TService>  AddTransient<TService>  AddSingleton<TService>
```

Also handle `TryAdd*`, `AddScoped(typeof(IFoo), typeof(Foo))`, `AddSingleton(new Foo())`, factory lambdas. For a single generic type, treat service and implementation as the same type. Edges: `registration-call-file -> service : di_registers` (0.85), `service -> implementation : di_resolves_to` (0.95). Record lifetime.

## 6.3 Constructor injection

For each constructor parameter of a class. Edges: `consumer type -> parameter service type : di_injects` (0.75), `consumer type -> registered implementation : di_depends_on_impl` (0.85) when a DI registration exists.

## 6.4 MediatR/CQRS

Detect `IRequest<TResponse>`, `IRequest`, `IRequestHandler<TRequest,TResponse>`, `IRequestHandler<TRequest>`, `INotification`, `INotificationHandler<TNotification>`. Edges: `request -> handler : mediatr_handles` (0.95). Also detect `IMediator.Send(new CreateOrderCommand(...))` and `_sender.Send(command)`: `caller -> request : sends_request` (0.70).

## 6.5 Route analyzer

Support MVC controllers (`[Route]`, `[HttpPost("{id}")]`) and minimal APIs (`app.MapGet("/orders", ...)`, route groups when statically resolvable). Route node form: `route:POST:/api/orders/{id}`. Edges: `route -> handler method : route_handles` (1.00), plus the handler's injected services.

## 6.6 Options/config

Detect `services.Configure<MyOptions>(configuration.GetSection("Payments"))`, `.Bind(options)`, `.Get<MyOptions>()`, `IOptions<>/IOptionsMonitor<>/IOptionsSnapshot<>`. Edges: `config:Payments -> MyOptions : options_binds` (0.85), `consumer -> MyOptions : options_consumes` (0.75), `config:Payments -> consumer : config_impacts` (0.80). Index `appsettings*.json` sections as config nodes.

## 6.7 EF Core analyzer (P1)

Detect `DbContext`, `DbSet<TEntity>`, `IEntityTypeConfiguration<TEntity>`, `modelBuilder.Entity<TEntity>()`, `HasOne`/`WithMany`. Edges: `DbContext -> Entity`, `Entity -> EntityConfiguration`, `Entity -> NavigationTarget`, `Repository/Service -> DbContext`.

## 6.8 Test analyzer

Detect `[Fact]`, `[Theory]`, NUnit `[Test]`, MSTest `[TestMethod]`. Create test nodes and edges by class naming (`OrderServiceTests -> OrderService`), constructor injected/mocked types, direct references, route strings in integration tests, and `WebApplicationFactory<T>`. Weight `tests`: 0.65.

---

# 7. Retrieval engine

## 7.1 Request types

```csharp
public sealed record LocalizationRequest(
    string RootDirectory,
    string? Query = null,
    string? ChangedSince = null,
    string? Focus = null,
    string? Route = null,
    string? Service = null,
    string? Request = null,
    string? ConfigSection = null,
    string? StackTrace = null,
    IReadOnlyList<string>? SelectedPaths = null,
    int MaxCandidates = 50,
    int Depth = 2,
    int? MaxTokens = null,
    bool IncludeTests = true,
    bool IncludeConfig = true);

public sealed record ContextRequest(
    string RootDirectory,
    IReadOnlyList<ContextSeed> Seeds,
    int Depth = 2,
    int? MaxTokens = null,
    ContextRenderMode RenderMode = ContextRenderMode.Mixed,
    bool IncludeTests = true,
    bool IncludeConfig = true);

public enum ContextRenderMode { Source, Reduced, Skeleton, PublicApi, Mixed }
```

## 7.2 Candidate generation

Sources: diff seeds; exact route lookup; exact symbol/service/request/config lookup; stack-trace parser; FTS query; path query; git co-change; recent/open files if the IDE supplies them.

```csharp
public sealed record CandidateNode(
    string NodeId, string FilePath, string Kind,
    double BaseScore, CandidateSource Source,
    IReadOnlyList<string> Reasons, int TokenEstimate);
```

Source weights: `diff_changed_file` 1.00, `route_exact` 1.00, `symbol_exact` 0.95, `stack_trace` 0.95, `service_exact` 0.95, `request_exact` 0.95, `config_exact` 0.90, `fts_symbol` 0.75, `fts_path` 0.70, `fts_body` 0.55, `cochange` 0.45.

## 7.3 FTS query

Search chunks, not only files. Weight fields name/signature/symbol > path > comments > body. If SQLite `bm25()` works:

```sql
SELECT chunk_id, bm25(chunk_fts, 4.0, 3.0, 2.0, 1.5, 1.0, 0.7) AS score
FROM chunk_fts WHERE chunk_fts MATCH $query ORDER BY score LIMIT $limit;
```

Note SQLite `bm25()` is lower-is-better; invert/normalize.

## 7.4 Graph expansion

Replace one-hop expansion with a weighted frontier:

```text
priority = candidateScore * edgeWeight * decay^hop + centralityBonus - ambiguityPenalty

For each seed: include as must-keep; enqueue typed outgoing/incoming edges per request mode.
While frontier not empty:
  pop highest; if score < threshold: continue
  if cannot fit budget and not must-keep: continue
  include node/file; if hop < depth: enqueue typed neighbors
```

## 7.5 Edge traversal policy

Review mode: changed file -> declared symbols -> callers / route handlers / DI consumers / tests; changed config -> options consumers; changed request -> handlers; changed service -> consumers.

Route query: route -> handler -> injected services -> DI impls -> options/config; handler -> request/DTO/entity; handler -> tests.

Service query: service/interface -> implementation -> consumers / tests / config.

MediatR query: request -> handler -> injected services / validators / tests.

## 7.6 Edge weights and decay

```text
route_handles 1.00  mediatr_handles 0.95  di_resolves_to 0.95  implements 0.90
di_depends_on_impl 0.85  options_binds 0.85  config_impacts 0.80  di_injects 0.75
tests 0.65  calls 0.60  cochanges 0.45  project_references 0.30
path_proximity 0.20  bare_references 0.15
```

Hop decay 0.65; edge-specific decay may override.

## 7.7 Ambiguity penalties

Penalize interfaces with many implementations, services with many registrations, common names, generated files, test files unless requested, large files, low-confidence syntax-fallback edges. Example: `score *= 1.0 / Math.Sqrt(1 + ambiguityCount)`.

## 7.8 Packing

```text
maximize sum(score * confidence * roleWeight) / tokenCost
subject to maxTokens
must include changed files and exact resolved seeds
```

Roles: `Changed`/`ExactSeed` must-keep; `Handler`/`Implementation` high; `Consumer` medium; `Test` optional; `Config` medium/high; `Project` low. Mixed render: changed/seed/handler/impl reduced-or-source; secondary deps skeleton; tests skeleton/reduced; small config source; huge generated files sketch.

---

# 8. Context output format

New output includes a semantic manifest, inclusion reasons, the context plan, and source entries.

```xml
<!-- fuse:semantic-context
mode: review
root: /repo
changedSince: origin/main
files: 12
estimatedTokens: 24800

seeds:
  - src/Api/BillingController.cs (changed)
  - src/Application/Billing/CreateInvoiceCommand.cs (changed)

semantic impact:
  POST /api/billing -> BillingController.Create
  CreateInvoiceCommand -> CreateInvoiceHandler
  IBillingGateway -> StripeBillingGateway (DI)
  Payments -> PaymentOptions -> StripeBillingGateway
  StripeBillingGatewayTests -> StripeBillingGateway

notes:
  3 files emitted as skeleton only.
-->

<file path="src/Api/BillingController.cs" role="changed" score="1.000">
...
</file>

<file path="src/Infrastructure/StripeBillingGateway.cs" role="di-implementation" score="0.842">
<!-- included via:
changed CreateInvoiceHandler -> injects IBillingGateway
 -> DI resolves IBillingGateway to StripeBillingGateway -->
...
</file>
```

For `fuse_localize`, return no bodies, only ranked candidates with path, reason, and token cost.

---

# 9. CLI overhaul

## 9.1 New commands

```text
fuse index [path] [--force]
fuse map [path] [--detail all|projects|routes|di|symbols|directories] [--max-tokens N]
fuse localize [path] --task "..." [--changed-since ref] [--route "..."] [--symbol "..."] [--max-candidates N]
fuse resolve [path] (--symbol X | --route X | --service X | --request X | --config X)
fuse context [path] (--seed X ... | --from-localize file.json) [--depth N] [--max-tokens N] [--render mixed|source|reduced|skeleton]
fuse review [path] --changed-since ref [--max-tokens N] [--include-tests]
fuse diagnostics [path]
fuse reduce ...
fuse host
fuse mcp serve | install
fuse models
```

## 9.2 Remove the old command set

Current commands are: `ask`, `dotnet`, `wiki`, `init`, `explain`, `verify`, `reduce`, `models`, `host`, `mcp install`, `mcp serve` (the bare `fuse` is generic fusion). Under the no-compatibility rule, remove `ask`, `dotnet`, `wiki`, `explain`, `verify`, and generic-template fusion, or reimplement the still-useful ones as thin wrappers over the new commands. Keep `reduce`, `models`, `host`, `init`, `mcp`. Decide per command in Phase 8; do not silently keep dead surface.

---

# 10. MCP overhaul

## 10.1 New tool set

Reduce from the current eleven (`fuse_toc`, `fuse_skeleton`, `fuse_focus`, `fuse_search`, `fuse_changes`, `fuse_ask`, `fuse_dotnet`, `fuse_generic`, `fuse_reduce`, `fuse_explain`, `fuse_find`) to eight:

```text
fuse_index  fuse_map  fuse_localize  fuse_resolve
fuse_context  fuse_review  fuse_find  fuse_reduce
```

## 10.2 Behavior

`fuse_map`: cheap first call, structure only. `fuse_localize`: candidate plan, signatures, reasons, no full source. `fuse_resolve`: deterministic resolver, no full source unless `includeSource=true`. `fuse_context`: source/reduced context for selected seeds. `fuse_review`: review map plus packed context. `fuse_find`: exact text/path/symbol lookup. `fuse_reduce`: compact a known set.

## 10.3 New server instructions

Replace the long instruction string in `McpServeCommand` with:

```text
Use fuse_review for PR/change work when a git base exists.
Use fuse_resolve when a task names a route, interface, service, request, handler, or config section.
Use fuse_localize for open-ended tasks.
Use fuse_context only after localize/resolve unless the user asks for one-shot context.
Use fuse_find for exact text/path/symbol lookup.
```

Update the MCP Registry manifest (`mcp-registry/`) to the new tool list.

---

# 11. Warm host as primary runtime

## 11.1 New responsibilities

```csharp
public sealed class FuseWorkspaceHost
{
    Task EnsureIndexedAsync(string root, IndexMode mode, CancellationToken ct);
    Task<LocalizationResultDto> LocalizeAsync(...);
    Task<ResolveResultDto> ResolveAsync(...);
    Task<ContextResultDto> ContextAsync(...);
    Task<ReviewResultDto> ReviewAsync(...);
    Task<MapResultDto> MapAsync(...);
}
```

## 11.2 Lifecycle

On startup: open DB, initialize schema, discover workspace, compare hashes, start background indexing, serve partial results with status. Statuses: `Cold`, `Indexing`, `Warm`, `Partial`, `Failed`, `Stale`.

## 11.3 File watcher

Debounce; ignore `.fuse`, `bin`, `obj`; compute changed paths; enqueue incremental index job; broadcast invalidation/index status. The watcher must not loop on its own `.fuse` writes (an engineering gate).

## 11.4 RPC contract bump

Any change to a `Fuse.Cli.Rpc` DTO or a `[JsonRpcMethod]` signature must bump `FuseHostService.ProtocolVersion` and `ext/vscode/src/host/protocol.ts` `PROTOCOL_VERSION` in the same change, update the extension client, and update both sides of the contract test (`tests/Fuse.Cli.Tests/Host/FuseHostContractTests.cs` and `ext/vscode/test/contract.test.mjs`). This overhaul adds localize/resolve/context/review/map endpoints, so the bump and the contract-test extension are mandatory, not optional.

## 11.5 CLI and MCP prefer the warm host

CLI: try to connect to the host for a root; if unavailable and the command needs a fast result, start an embedded engine; `fuse host` runs the long-lived service. The MCP server starts/owns the engine for the workspace; the IDE extension uses host RPC.

---

# 12. Incremental indexing

File hashing (XxHash64 or SHA256): store content hash, size, mtime; index invalid if hash changed. Optional directory Merkle (`directory_hashes` table) to skip unchanged subtrees.

Invalidation rules: `.cs` changed -> re-index that file's symbols/chunks/edges; `.csproj`/`.props`/`.targets`/`.sln` changed -> reload affected projects, recompute compilation, re-index project files as needed; `appsettings*.json` changed -> re-index config nodes and options edges; git HEAD changed -> update co-change in the background.

Batch transactions: one transaction per indexing batch. Delete old records for changed files before inserting:

```sql
DELETE FROM chunks WHERE file_id = $fileId;
DELETE FROM symbols WHERE file_id = $fileId;
DELETE FROM nodes WHERE file_id = $fileId;
DELETE FROM edges WHERE evidence_file_id = $fileId OR from_node_id IN (...) OR to_node_id IN (...);
DELETE FROM routes WHERE file_id = $fileId;
DELETE FROM di_registrations WHERE file_id = $fileId;
DELETE FROM options_bindings WHERE file_id = $fileId;
```

---

# 13. Git co-change

Build from `git log --name-only --pretty=format:%H --since="180 days ago"`. Ignore commits touching more than ~50 files. For each commit, increment co-change counts per pair. Compute `jaccard = cochange(a,b) / (changes(a) + changes(b) - cochange(a,b))` and `pmi = log((cochange(a,b) * totalCommits) / (changes(a) * changes(b)))`. Store top N neighbors per file. Use only when query signal is weak, in review mode, when `changedSince` exists, or when selected files exist. Weight 0.25 to 0.55 by PMI/Jaccard. Do not let co-change dominate. Reuse the chunked git-invocation pattern from `GitStatsProvider`.

---

# 14. Reduction/rendering overhaul

Keep existing reducers but reposition them as rendering, not retrieval. Render tiers: `FullSource`, `Reduced`, `Skeleton`, `PublicApi`, `Sketch`, `Omitted`.

Mixed rendering (review mode): changed files Reduced/FullSource; exact seeds Reduced; direct handlers/impls Reduced; secondary deps Skeleton; tests Skeleton or Reduced if budget allows; small config FullSource; generated Sketch.

Reuse `ContentReductionPipeline`, `RoslynSkeletonExtractor`, `RoslynSymbolSliceExtractor`, `ThinSkeletonAssembler`, the redactor, and emission writers. The retrieval plan, not the reducer, picks each file's tier. Replace the old `ContextPlanBuilder` with a semantic plan builder.

---

# 15. Context plan model

```csharp
public sealed record ContextPlan(
    string Mode,
    IReadOnlyList<ContextPlanItem> Items,
    IReadOnlyList<ContextPlanEdge> ExplanationEdges,
    int EstimatedTokens,
    IReadOnlyList<string> Warnings);

public sealed record ContextPlanItem(
    string Path, string? NodeId, string Role, RenderTier Tier,
    double Score, int EstimatedTokens, bool MustKeep,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> ProvenanceChain);

public sealed record ContextPlanEdge(
    string From, string To, string EdgeType, double Weight, string? Evidence);
```

Roles: `changed`, `exact-seed`, `route-handler`, `request-handler`, `di-implementation`, `consumer`, `config`, `test`, `cochange`, `dependency`, `project`.

---

# 16. Tests overhaul

The current suite is 11 unit test projects (about 175 files, xUnit), 7 golden-output files, and a bidirectional RPC contract test (`tests/Fuse.Cli.Tests/Host/FuseHostContractTests.cs` plus `ext/vscode/test/contract.test.mjs`, wired through `ext/vscode/package.json` `test:contract`). The overhaul changes what Fuse is, so the suite must change with it. The invariant from AGENTS.md holds: a test the runner does not discover and execute is dead; a green gate that ran zero new tests is not coverage.

## 16.1 New test projects

Add and wire into `Fuse.slnx` and the `dotnet test` run:

- `Fuse.Indexing.Tests`: schema creation and migration (drop-and-rebuild at version 10), upsert/delete transactionality, FTS5 search correctness, store disposal, WAL behavior under interruption, incremental re-index of a changed file.
- `Fuse.Semantics.Tests`: discovery order, MSBuild load plus syntax fallback, stable symbol IDs across runs, symbol/chunk extraction, and one fixture per analyzer (interface, DI, constructor injection, MediatR, route, options, EF, test) asserting the exact edges in 6.x.
- `Fuse.Retrieval.Tests`: candidate generation per source, score normalization, graph expansion with typed weights, pruning, packing under budget (must-keep honored), and the resolve/localize/review request paths.
- `Fuse.Context.Tests` (or fold into retrieval): mixed rendering by tier, provenance chains, manifest contents, budget adherence.

## 16.2 Shared semantic fixture

Build one in-repo fixture app that exercises every analyzer (the worked example in Phase 4 below: `IOrderService`/`OrderService`, `OrdersController` with a route and constructor injection, `CreateOrderCommand`/`CreateOrderHandler`, `OrderOptions` bound to an `Orders` config section, plus a test class). Assert the full expected edge set against it. This fixture is the spine of `Fuse.Semantics.Tests` and the target of the resolve/review acceptance tests.

## 16.3 Golden output

Replace the 7 current golden files (default/skeleton fusion, project graph, route map, TOC, formatters) with golden files for the new output shapes: a `fuse_review` payload, a `fuse_context` mixed-render payload, a `fuse_localize` candidate list, and a `fuse_map`. Keep the `UPDATE_GOLDEN_FILES=1` regeneration mechanism and CRLF normalization. Delete golden files for removed commands.

## 16.4 Contract test

Extend both sides of the contract test for the new host endpoints (localize/resolve/context/review/map DTOs), bump `ProtocolVersion` and `PROTOCOL_VERSION` together, and confirm the new tests actually run (the count goes up) under `test:contract`.

## 16.5 Definition of done

- `dotnet test Fuse.slnx -c Release --no-build` green, with the new projects discovered and executed (test count rises).
- `dotnet format Fuse.slnx --verify-no-changes` clean.
- Every analyzer has a fixture-backed edge assertion.
- The contract test covers every new endpoint on both sides.

---

# 17. Documentation overhaul

Docs live as MDX under `site/content/docs` (Next.js + Fumadocs, published at fuse.codes), about 55 pages across `start/`, `concepts/`, `scenarios/`, `reference/`, `internals/`, `project/`. The product changes, so the docs must be rewritten, not patched. Writing style per AGENTS.md: plain ASCII, no em dashes, no emoji; user-facing pages outcome-first; reference pages dense; define coined terms once; numbers exact and sourced.

## 17.1 Reference (rewrite)

- `reference/mcp-tools.mdx`: replace the 11-tool reference with the 8 new tools and their parameters.
- `reference/commands.mdx`: replace the old command surface with `index`/`map`/`localize`/`resolve`/`context`/`review`/`diagnostics`/`reduce`/`host`/`mcp`/`models`.
- `reference/mcp-resources.mdx`: update resource URIs to the new workflows.
- `reference/options.mdx`, `output-specification.mdx`: align with the new render tiers and the semantic-context output format.
- `reference/reducers.mdx`, `tokenizers.mdx`, `pattern-detectors.mdx`, `secret-redaction-kinds.mdx`, `templates.mdx`, `configuration-keys.mdx`: keep where reused; trim what the overhaul removes.

## 17.2 Concepts (rewrite the model)

The mental model shifts from "explore-phase token reduction" to "warm semantic index + resolve/review". Rewrite `the-explore-phase.mdx`, `how-fuse-works.mdx`, `scoping.mdx`, and add new pages: the semantic graph and edge types, resolve vs localize vs review, the warm index lifecycle and statuses. Keep `reduction-levels.mdx`, `precision-tier.mdx`, `sessions-and-deltas.mdx`, `glossary.mdx` updated with new terms (node, edge, localize, resolve, blast radius, render tier).

## 17.3 Scenarios (rewrite around new workflows)

Lead each with the new flagship workflow. Rewrite `scope-a-pr.mdx` around `fuse_review`; `context-for-an-agent.mdx` around localize -> resolve -> context; add scenarios for "resolve a route to its handler", "find the implementation injected for an interface", "trace a config section to its consumers". Retire or fold scenarios tied to removed commands.

## 17.4 Internals

Rewrite `pipeline.mdx` (new stage order), `scoping-internals.mdx` (graph expansion and packing), `caching-internals.mdx` (the new schema, FTS5, incremental index). Update `host-rpc.mdx` for the new endpoints and protocol version. Keep `capability-model.mdx`, `options-model.mdx`, and the `extending/` guides aligned.

## 17.5 Start and project

Update `what-is-fuse.mdx`, `why-fuse.mdx`, `install.mdx`, `quickstart.mdx`, `connect-your-ai.mdx`, `vscode-extension.mdx` to the new positioning and tool set. Rewrite `project/benchmarks.mdx` and `project/performance.mdx` from the regenerated results (Section 18). Add a V3 entry to `project/changelog.mdx` and update `project/roadmap.mdx`.

## 17.6 Definition of done

- No page documents a removed command or tool.
- Every measured number is sourced from regenerated results; illustrative claims are labeled.
- `meta.json` sidebars updated for added/removed pages.
- Site builds.

---

# 18. Benchmark overhaul

The benchmark question changes with the product. It is no longer "did Fuse return fewer tokens?" or "did BM25 find the changed files?" It is whether Fuse is becoming a .NET semantic context engine:

- Can it resolve .NET wiring correctly?
- Can it identify PR/change blast radius?
- Can it localize files/symbols better than lexical search and bare agent tools?
- Can it give an agent enough context to make correct edits with fewer wasted tokens?
- Is warm indexing fast enough for iterative workflows?

The existing harness lives in `tests/benchmarks`: `corpus.json` pins 7 repos counted with `o200k_base`; offline layers 1, 2A, 2B, 4, ranking, and latency; model-dependent layers 5 (agent) and 6 (peer scopers); .NET tools TokenCount/Fidelity/BodyIntegrity; `setup-corpus.ps1`, `gen-prs.ps1`, and a regression-baseline mechanism. The AGENTS.md rule is absolute: never fabricate or weaken a number; reproduce with `pwsh -File tests/benchmarks/harness/run-all.ps1`; verify before quoting. The overhaul keeps that infrastructure and reframes what it measures.

## 18.1 Principles

- Separate the layers. Do not collapse everything into one fuzzy "agent success" number. Measure graph correctness, localization quality, context sufficiency, and efficiency separately, and report ablations.
- Keep claims narrow. Report by mode: review/change, semantic resolve, query/localize, focus, context emission, agent end-to-end. Do not average across modes without also reporting per-mode results.
- Use serious baselines: bare filesystem agent (read/grep/glob); a lexical index baseline (coa-codesearch / Lucene-style path+snippet); the frozen old Fuse commit; Repomix/full dump as a cost baseline only; optionally CodeGraph/Serena; and human or strong-model adjudicated ground truth.
- Distinguish returned candidates from returned source. Fuse returns reduced source; coa returns path/snippets. Precision must be reported in a shape-aware way (18.10) so comparisons are fair.

## 18.2 The five suites

```text
A. Semantic resolution        (the moat; deterministic wiring)
B. PR/change impact           (the flagship; blast radius)
C. Open-ended localization    (the old weak spot; query without a base)
D. Agent context sufficiency  (model-dependent; does it help a real agent)
E. Performance/indexing       (is the warm service fast enough)
```

## 18.3 Suite A: Semantic resolution

Answers: can Fuse deterministically resolve .NET implicit wiring (IFoo -> FooImpl, route -> action, request -> handler, config section -> options -> consumer, subject -> tests, DbContext -> entity -> configuration)?

Dataset: hand-built fixture repos are essential here because they give exact ground truth. Cover DI (open/closed generics, `TryAdd*`, `typeof` pairs, factory lambdas, single-type self-registration), multiple-implementation ambiguity, MediatR requests/handlers, MVC routes, minimal APIs, route groups, options bindings, and test-to-subject. Supplement with the pinned OSS corpus and the eShopOnWeb application via sampled adjudication (18.10).

Metrics per resolver type: precision@1, precision@3, recall@k, MRR, exact-match accuracy, ambiguous-case handling, false-positive rate. For the graph as a whole: edge precision, recall, F1.

Initial targets: DI P@1 >= 0.85; MediatR P@1 >= 0.95; route P@1 >= 0.85; options P@1 >= 0.80; interface-impl recall@3 >= 0.90. Test-to-subject may be lower because it is heuristic.

## 18.4 Suite B: PR/change impact (flagship)

Answers: given a PR diff, does `fuse_review` return the files/symbols an agent needs to understand or modify the change, with good precision and provenance?

Dataset: reuse the existing 6-repo corpus and 108 merged PRs first; expand toward 10-20 repos and 200-500 PRs later.

Use more than one ground truth and label which is which:

1. Changed files only (good for recall, incomplete: omits unchanged context).
2. Changed files plus derived semantic dependencies (referenced symbols, DI impls/consumers, route handlers, MediatR handlers, options consumers, related tests). Derived, not human truth.
3. Human/LLM-adjudicated sufficiency set on a smaller subset ("which files would a developer need to review this?"). Mark as adjudicated.
4. Actual agent edit/read/test dependencies from Suite D. Useful but noisy.

Modes: old Fuse change mode; new review mode; review without semantic graph; review without co-change; review without tests; changed-files-only; coa/Lucene from the PR title; bare agent; Repomix cost baseline. Inputs: title, body, base ref, diff hunks. Review gets the base ref; query mode does not unless testing a hybrid.

Metrics: file-level recall/precision/F1 at budget, changed-file recall, support-file precision; symbol-level recall (handlers, routes, DI consumers, tests); token-level (returned source tokens, tokens per relevant file, irrelevant-token ratio); ranking (MRR, NDCG@10/@20, average rank of changed files); review-specific (changed-file inclusion percent, affected route/service/test recall, blast-radius precision).

Headline metrics: review recall and precision at 25k and 50k tokens, relevant files per 10k tokens, warm latency. The goal is not higher recall alone; it is equal-or-higher recall with materially better precision and provenance at equal-or-fewer tokens than old change mode.

## 18.5 Suite C: Open-ended localization

Answers: given a task title/body with no git base, can `fuse_localize` localize relevant files better than lexical search and agent grep?

Dataset: the same PRs with the diff hidden. Inputs: title only, title+body, issue text, labels, optional stack trace. Ground truth is the changed file set. Critically, bucket tasks by signal: identifier-rich, natural-language domain query, route/API query, config/CI query, dependency bump, formatting/nitpick, test-only, no-signal. Query retrieval cannot solve no-signal cases; the benchmark must prove Fuse detects low signal rather than returning overconfident junk.

Modes: new localize; localize without graph; FTS only; FTS + semantic graph; FTS + co-change; FTS + optional dense/reranker; coa/Lucene; bare grep agent; old Fuse query mode.

Metrics: recall/precision@K files, recall/precision at token budget, MRR, NDCG, and low-signal detection accuracy. Low-signal detection: true positive when Fuse says "low signal, provide a base/stack trace/route/symbol" on a query that would otherwise fail; false positive when it downgrades a query it could have solved. Targets: beat FTS-only and old Fuse on identifier-rich; beat lexical clearly on route/service/request/config queries; detect no-signal instead of returning junk; overall precision materially better than the recorded one-shot floor.

## 18.6 Suite D: Agent context sufficiency (model-dependent)

Answers: does Fuse help a real coding agent solve or review tasks better, faster, or with fewer tokens/tool calls? Expensive and not byte-reproducible; label it as such, as the current layer 5 already is.

Dataset: start with about 24 tasks (8 PR-review, 8 bugfix/edit, 4 route/API, 4 DI/MediatR/options), then scale to 50-100. Use real merged PRs where possible; build synthetic route/DI/wiring tasks because they test the moat directly.

Toolboxes: bare filesystem (read/grep/glob); old Fuse MCP; new Fuse MCP (localize/resolve/context/review); new Fuse one-shot context only; lexical-search MCP + read. Optionally Serena, CodeGraph, coa-codesearch.

Protocol: one model, temperature 0, fixed max turns and wall-clock, identical system prompt, tools per toolbox. Review tasks ask for impacted paths and review risks (no edits); bugfix tasks ask for the change and a test run.

Metrics: for edits, success rate, patch-applies, tests-pass, edited-file precision, read-file recall vs ground truth, tokens, tool calls, wall-clock, graded score; for reviews, impact recall, risk-identification score, false-concern rate, tokens, tool calls; for sufficiency, a 0/1/2 judge or human score, not relied on alone.

Run two variants and expect different winners: "Fuse-first" (agent gets one `fuse_review`/`fuse_context` payload up front) should be strong for PR/change tasks; "agent-driven" (agent calls map/localize/resolve/context/review iteratively) should be stronger for ambiguous tasks.

## 18.7 Suite E: Performance/indexing

Answers: is Fuse usable as a warm service? Size tiers: small (<500 files), medium (500-5,000), large (5,000-25,000), monorepo (25,000+ if available).

Cold indexing: file-collection time, MSBuild load time, semantic extraction time, DB write time, FTS build time, total index time, peak memory, DB size. Warm operations: resolve/localize/review/context-render latency P50/P95. Incremental: single-file edit debounce, hash detection, re-index, edge update, stale window; project-file edit reload and re-index scope.

Targets (adjust by repo size): resolve warm P95 < 100 ms; localize warm P95 < 500 ms; review plan warm P95 < 2 s excluding reduction/render; context render P95 < 5 s for 50k tokens; single-file incremental update < 1 s on small/medium; medium cold index < 60 s; medium peak memory < 1.5 GB.

## 18.8 Ablation matrix (mandatory)

Without ablations you cannot tell what helped. Run and report deltas:

```text
Retrieval stack:
  A0 FTS only
  A1 FTS + bare type graph
  A2 FTS + semantic graph
  A3 + typed edge weights
  A4 + pruning
  A5 + co-change
  A6/A7/A8 + reranker / dense / both

Review mode:
  R0 changed files only
  R1 + old dependents
  R2 + semantic graph
  R3 + tests
  R4 + co-change
  R5 + pruning/packing

Context rendering:
  C0 full source
  C1 standard reduction
  C2 mixed tiering
  C3 skeleton-only neighbors
  C4 mixed + session delta
```

## 18.9 The evaluation tool

Add a `fuse eval` subcommand (or a separate `Fuse.Eval` executable under `tools/`) as the driver for suites A-D. Keep the existing PowerShell layers for the offline token/fidelity/reduction continuity (layers 1, 2A, 2B, 4) and update them to call the new commands; `fuse eval` owns the semantic, localization, and agent evaluations.

Dataset format (per repo, per task):

```json
{
  "name": "dotnet-pr-localization-v1",
  "repos": [{
    "id": "repo1", "name": "MediatR", "path": "/bench/repos/MediatR",
    "tasks": [{
      "id": "pr-123", "kind": "pull_request",
      "baseRef": "abc123", "headRef": "def456",
      "title": "Fix pipeline behavior registration", "body": "...",
      "changedFiles": ["src/MediatR/Registration/ServiceRegistrar.cs",
                       "test/MediatR.Tests/RegistrationTests.cs"],
      "labels": ["di", "registration"], "category": "di",
      "groundTruth": {
        "files": [
          {"path": "src/MediatR/Registration/ServiceRegistrar.cs", "role": "changed"},
          {"path": "test/MediatR.Tests/RegistrationTests.cs", "role": "test"}],
        "symbols": ["MediatR.Registration.ServiceRegistrar"],
        "routes": [], "services": []
      }
    }]
  }]
}
```

Outputs: `results.jsonl`, `summary.json`, `summary.md`, `failures.md`. Per-task result records mode, recall, precision, F1, tokens, latency, and the included/missed/irrelevant file lists with reasons. The failure report is the roadmap driver: for every miss, print the missed file, its ground-truth role, what was retrieved nearby and via which edge, the query signal, the graph signal, and the suggested missing edge (options/test/co-change). Suggested command surface:

```text
fuse eval index    --dataset bench/repos.json --output bench/results/index
fuse eval semantics --fixtures bench/semantic-fixtures --output bench/results/semantics
fuse eval review   --dataset bench/prs.json --modes changed-only,old-fuse,new-semantic,new-semantic-cochange --budgets 10000,25000,50000 --output bench/results/review
fuse eval localize --dataset bench/prs.json --inputs title,title-body --modes fts,semantic,semantic-cochange,dense,rerank --k 5,10,20,50 --output bench/results/localize
fuse eval agent    --dataset bench/agent-tasks.json --toolboxes bare,new-fuse,old-fuse,lexical --model claude-sonnet-4-6 --rollouts 3 --output bench/results/agent
```

## 18.10 Ground truth: edge gold files and spot checks

For fixtures, store edge gold files and compare predicted edges to gold, normalizing node IDs by display name:

```json
{ "edges": [
  {"from": "type:App.IOrderService", "to": "type:App.OrderService", "type": "di_resolves_to"},
  {"from": "route:POST:/api/orders/{id}", "to": "method:App.OrdersController.Create", "type": "route_handles"}
]}
```

For real repos where exact gold is infeasible, use sampled adjudication: randomly sample about 100 predicted DI edges for human/LLM verification, sample about 100 known interface/implementation pairs to check recall, and report confidence intervals.

Return-shape-aware precision, reported in three forms so tools that return different shapes compare fairly:

- Candidate precision: relevant candidate files / candidate files (path/snippet tools).
- Source-token precision: tokens in relevant files / total source tokens returned (source tools like Fuse).
- Workflow precision: relevant files read / total files read (agent workflows).

## 18.11 Reporting rules

- Always report bootstrap confidence intervals for recall, precision, sufficiency, and agent success, especially at small task counts.
- Report medians and distributions (median, P75, P95, worst 10 tasks), not just means; means hide bad tails.
- Categorize by task type (api-route, di, mediatr, options, config, test, dependency-bump, ci, formatting, generic-library, aspnet-app) so easy identifier-rich tasks do not mask failures on config/CI/no-signal tasks.
- Freeze old baselines: the old Fuse commit, the old corpus, and the model version, so a change's effect is attributable.
- Always distinguish returned candidates from returned source (18.10).

Every run produces a top-level human-readable scorecard, for example:

```text
Fuse Eval Summary
Semantic resolution:   DI P@1 0.91   Route P@1 0.88   MediatR P@1 0.98   Options P@1 0.83
Review/change:         Recall@25k 0.90   Precision@25k 0.61   Tokens/relevant 3.2k   Warm P95 1.4s
Open-ended localize:   Recall@20 0.58   Precision@20 0.24   Low-signal F1 0.79
Agent:                 Success 0.44   Sufficiency 0.37   Median tokens 142k   Median calls 12
Performance:           Medium cold index 42s   Warm resolve P95 38ms   Warm localize P95 320ms
```

## 18.12 Regenerate and resync

Regenerate the benchmark figure (`assets/fuse-benchmarks.png`/`.svg` via the chart script) from committed results. Resync `project/benchmarks.mdx`, `project/performance.mdx`, the Measured Results section of AGENTS.md, and any docs pages that quote figures, in the same change that lands the numbers. The AGENTS.md headline is rewritten from token-savings framing to the semantic-engine framing in 18.13.

## 18.13 North star and definition of done

The new Fuse is judged not by "tokens saved" but by:

```text
Can it resolve .NET wiring correctly?
Can it improve PR/change-context precision without losing recall?
Can it tell agents what actually runs?
Can it avoid overconfident junk on low-signal queries?
Can it serve this fast from a warm persistent index?
```

If those improve, Fuse has a real moat. If only token count improves, it does not.

Definition of done:

- `fuse eval` runs suites A-D and the offline PowerShell layers run green on the new surface.
- Semantic resolution accuracy reported per edge type with stated ground truth and confidence intervals.
- Review precision improves over the recorded old change mode at equal-or-better recall and equal-or-fewer tokens; results bucketed by task type.
- Low-signal detection reported; no overconfident junk on no-signal queries.
- Warm latency targets in 18.7 met on a medium repo.
- All quoted numbers trace to `tests/benchmarks/results`; AGENTS.md and docs match; the figure is regenerated; old baselines frozen.

---

# 19. Implementation sequence

Suggested order. Each phase ends green (build, test, format) and does not silently break the prior surface until the new one replaces it.

## Phase 0: Foundation

Create `Core/Fuse.Indexing`, `Core/Fuse.Semantics`, `Core/Fuse.Retrieval` (and `Fuse.Context` if separate). Wire references: Indexing -> Collection/Reduction as needed; Semantics -> Indexing + Roslyn packages + plugin abstractions; Retrieval -> Indexing/Semantics/Reduction/Emission. Add `Microsoft.CodeAnalysis.Workspaces.MSBuild` and `Microsoft.Build.Locator`; keep `Microsoft.CodeAnalysis.CSharp`. Add the new test projects (Section 16.1) wired into `Fuse.slnx` so they run from the start, even if empty.

Done: solution builds; tests run; new projects compile; existing CLI still compiles temporarily. (Do not chase the `SelectQueryMembers` "floor" or `PseudoRelevanceExpander` document-frequency items from the original draft; verification showed both are correct, not bugs.)

## Phase 1: SQLite schema and store

Implement `WorkspaceIndexStore`, schema creation/migration (drop-and-rebuild below version 10), table models, upsert/delete, FTS5 indexing, simple FTS search. Reuse `FuseStorePaths` and the WAL/connection patterns from the existing stores. Test in `Fuse.Indexing.Tests` against a temp repo: insert files/symbols/chunks, FTS finds `OrderService`, delete/reinsert a changed file, edges persist. Done: DB created, FTS works, upserts transactional, store disposes safely, WAL enabled.

## Phase 2: Syntax-level semantic batch

Before full MSBuild resolution, a syntax batch indexer populates files/symbols/chunks/routes approximately, using `FileCollectionPipeline`, file hash records, Roslyn parse-tree symbol extraction, `RoslynSymbolChunkExtractor`, and `RoslynRouteMapGenerator` logic adapted to records. Done: `fuse index` indexes a simple repo; `fuse map --detail symbols` shows DB symbols; FTS returns DB chunks.

## Phase 3: MSBuild/Roslyn semantic indexing

Implement `DotNetWorkspaceDiscoverer`, `RoslynWorkspaceLoader`, project records, semantic symbol extraction (types, members, signatures, accessibility), `SymbolIdBuilder`, file linkage, graceful syntax fallback. Done: indexes a real `.sln`/`.csproj`; symbol IDs stable across runs; status `Semantic` or `Partial`.

## Phase 4: Core semantic edges

Implement `InterfaceImplementationAnalyzer`, `DiRegistrationAnalyzer`, `ConstructorInjectionAnalyzer`, `MediatRAnalyzer`, `AspNetRouteAnalyzer`, `OptionsBindingAnalyzer`. Each returns `SemanticAnalyzerResult(Nodes, Edges, Routes, DiRegistrations, OptionsBindings, Diagnostics)`. Build the shared fixture (Section 16.2) and assert this expected edge set:

```text
IOrderService -> OrderService          : di_resolves_to
OrdersController -> IOrderService      : di_injects
OrdersController -> OrderService       : di_depends_on_impl
CreateOrderCommand -> CreateOrderHandler : mediatr_handles
POST api/orders/{id} -> OrdersController.Create : route_handles
Orders config -> OrderOptions          : options_binds
OrderService -> OrderOptions           : options_consumes
```

Done: all edges present in DB; `fuse resolve --service IOrderService` returns `OrderService`; `--request CreateOrderCommand` returns the handler; `--route "POST /api/orders/{id}"` returns the action.

## Phase 5: Retrieval engine v2

Implement `LocalizationRequest`, candidate generators (exact symbol/route/service/request/config, FTS, path, changed files), score normalization, typed graph expansion, pruning, context plan output. Done: `fuse localize --task "billing endpoint"` returns route/controller/service candidates; `fuse context --seed IOrderService` includes interface, implementation, consumers, optional tests; no old `QueryScopingPipeline` required.

## Phase 6: Review/change impact

Reuse `GitChangeDetector`/`GitDiffParser`. Changed files become must-keep seeds; for each, find declared symbols, incoming edges, DI consumers, route handlers, MediatR handlers/requests, options consumers, tests, co-change neighbors. Build the review preamble and packed context. Done: `fuse review --changed-since HEAD~1` works in a fixture git repo; output explains every non-changed file; changed files always included unless missing from collection.

## Phase 7: Context rendering

Implement `SemanticContextRenderer`; integrate reduction by render tier; add semantic manifest preamble and per-file provenance; support XML/Markdown/JSON; keep redaction on by default; add route/DI/options/project-graph structural maps. Done: `fuse context` emits bodies; `fuse localize` emits none; manifest includes semantic reasons; token budget respected.

## Phase 8: CLI rewrite

Replace the command structure with `index`/`map`/`localize`/`resolve`/`context`/`review`/`diagnostics`/`find`/`reduce`/`host`/`mcp serve`/`mcp install`/`models`. Remove `ask`, `dotnet`, `wiki`, `explain`, `verify`, generic fusion (or wrap the worthwhile ones). Done: the manual examples in Section 9.1 run end to end.

## Phase 9: MCP rewrite

Replace tools with the eight in Section 10.1; rewrite the server instructions (10.3); make localize/resolve compact; make context consume localize/resolve seeds; add session support to avoid resending unchanged files; update `mcp-registry/`. Done: agents can call the new tools; descriptions discourage blind full context; review is recommended for PR work; resolve for route/service/request/config.

## Phase 10: Tests, docs, benchmarks (Sections 16, 17, 18)

Land the new test projects with real assertions, rewrite the docs, and extend the benchmark harness with semantic resolution accuracy and the V3 review/localize/latency layers. Regenerate results and the figure; resync AGENTS.md and the docs. Done: the definitions of done in 16.5, 17.6, and 18.13 all hold.

## Phase 11: Publish V3

- Confirm CI green: build, test (count risen), format, and the self-contained publish smoke test for win-x64 and linux-x64, including FTS5 availability and MSBuild-unavailable fallback.
- Finalize the `3.0.0` version and changelog; confirm the RPC protocol version bump and the VS Code extension client are in sync.
- Update the MCP Registry manifest and the install flow.
- Open a PR via `gh` for review. Do not merge, self-approve, or enable auto-merge; leave the merge and the package/extension publish to reviewers.

Lower-priority follow-ups after publish: git co-change (Section 13), test edges, EF Core edges, optional reranking/dense retrieval.

---

# 20. Quality gates

## 20.1 Functional

Index a non-trivial ASP.NET Core app; resolve route -> handler, interface -> implementation, request -> handler, options -> consumers; review a real diff and emit semantic impact; MCP tools work.

## 20.2 Retrieval (private benchmark targets)

- Change/review recall stays at or above 90 percent.
- Precision improves meaningfully over the recorded 53 percent for change mode when semantic expansion is used.
- Query/localize precision improves over the recorded one-shot floor.
- ASP.NET focus recall improves from the recorded 55 percent toward 75+ percent.
- Warm localize latency under 500 ms after index; warm resolve under 100 ms.

These are targets, not facts. Quote only regenerated, recorded numbers in docs and AGENTS.md.

## 20.3 Engineering

DB survives interruption; file watcher does not loop on `.fuse`; incremental index updates changed files; full rebuild works; semantic fallback diagnostics are clear; redaction on by default; new tests actually run; RPC protocol version and extension client in sync.

---

# 21. Notes on current code

- Replace `QueryScopingPipeline` (PRF, thesaurus, dense rerank, member retrieval, git churn, centrality, proximity, fielded comments) with `SemanticRetrievalEngine`, `GraphExpansionEngine`, `CandidateGenerator`, and a semantic `ContextPlanBuilder`. Do not preserve old behavior.
- Keep `DependencyGraphBuilder` only as a syntax fallback; the new graph is DB-backed with stable node IDs.
- Keep `ContentReductionPipeline` as rendering; it must not drive retrieval.
- Keep `EmissionPipeline`, but add the semantic-context manifest.
- Keep `GitChangeDetector`; review mode now consumes the semantic graph.
- Keep `RoslynSkeletonExtractor` and `RoslynSymbolChunkExtractor` (use chunks initially, then migrate to semantic symbol spans).
- The two "obvious bugs" in the original draft are not bugs: `SelectQueryMembers` defines its `floor` as `best * 0.4`, and `PseudoRelevanceExpander` uses document frequency correctly. No fix needed.

---

# 22. Suggested namespaces and classes

```text
Fuse.Indexing
  WorkspaceIndexStore  WorkspaceIndexSchema  WorkspaceIndexMigrator
  WorkspaceIndexConnectionFactory  FileHashService  FtsSearchService
  GraphStore  CochangeStore

Fuse.Semantics
  DotNetWorkspaceDiscoverer  RoslynWorkspaceLoader  SemanticIndexer
  SyntaxFallbackIndexer  SymbolIdBuilder  SourceSpanMapper  SymbolRecordBuilder

Fuse.Semantics.Analyzers
  InterfaceImplementationAnalyzer  DiRegistrationAnalyzer
  ConstructorInjectionAnalyzer  MediatRAnalyzer  AspNetRouteAnalyzer
  OptionsBindingAnalyzer  EfCoreAnalyzer  TestAnalyzer  DirectCallAnalyzer

Fuse.Retrieval
  SemanticRetrievalEngine  CandidateGenerator  ExactResolver
  FtsCandidateGenerator  DiffCandidateGenerator  StackTraceCandidateGenerator
  GraphExpansionEngine  EdgeWeightProvider  CandidateScorer
  ContextPlanner  ContextPacker  ReviewBuilder

Fuse.Context
  SemanticContextRenderer  SemanticManifestBuilder
  ContextEntryRenderer  ProvenanceFormatter
```

---

# 23. Final priority order

If time is constrained:

1. Persistent SQLite schema + FTS.
2. Syntax indexer.
3. MSBuild/Roslyn semantic symbols.
4. DI/interface/MediatR/route/options edges.
5. `fuse_resolve`.
6. `fuse_review`.
7. Graph expansion/pruning.
8. `fuse_localize`.
9. `fuse_context`.
10. MCP rewrite.
11. Tests, docs, benchmarks (Sections 16-18).
12. Publish V3 (Phase 11).
13. Co-change graph, test edges, EF Core edges, optional reranking.

The single most important thing remains: build the resolved semantic graph and expose it through resolver/review workflows. Everything else serves that. V3 is not done until tests, docs, and benchmarks reflect it and it is published.

---

## Progress Log

### P1.1 WorkspaceIndexStore + schema creation/migration - 2026-06-26 09:02
- Status: done
- Result: Added the SQLite index lifecycle to `Core/Fuse.Indexing`: `WorkspaceIndexConnectionFactory` (pooled connections, per-connection `busy_timeout`/`foreign_keys` pragmas), `WorkspaceIndexSchema` (target version 10, database-level WAL/synchronous pragmas, full relational DDL for all Section 4 tables except FTS), `WorkspaceIndexMigrator` (drop-and-rebuild below version 10, driven off `sqlite_master` so legacy tables like the old `kv` cache are dropped too), `WorkspaceIndexState`/`WorkspaceIndexStatus`, `IWorkspaceIndexStore` (lifecycle members only this phase), and `WorkspaceIndexStore`. Added `WorkspaceIndexSchemaTests` (3 tests) in `Fuse.Indexing.Tests`. New solution test total 624 (Fuse.Indexing.Tests rose 1 -> 4).
- Verification: `dotnet build Fuse.slnx -c Release` green (0 errors; pre-existing NU1902 warnings only); `dotnet test Fuse.slnx -c Release --no-build` 624 passed, 0 failed; `dotnet format Fuse.slnx --verify-no-changes` clean.
- Blockers/issues: Initial test used xUnit v3 `TestContext.Current.CancellationToken`; repo is xUnit 2.9.2, switched to `CancellationToken.None`.
- Lessons: FTS5 virtual table is deliberately left out of `CreateTablesDdl` and added in P1.3 so a runtime lacking FTS5 can still build the relational schema. The migrator preserves the `schema_version` table across drops and resets its row. The full `IWorkspaceIndexStore` upsert/search surface is grown incrementally in P1.2/P1.3 rather than stubbed now.
- Time: ~35 min

### P1.2 Table models + transactional upsert/delete - 2026-06-26 09:18
- Status: done
- Result: Added `WorkspaceIndexRecords.cs` (record models: `IndexedFileRecord`, `ProjectRecord`, `NodeRecord`, `SymbolRecord`, `ChunkRecord`, `SemanticEdgeRecord`, `RouteRecord`, `DiRegistrationRecord`, `OptionsBindingRecord`). Extended `IWorkspaceIndexStore`/`WorkspaceIndexStore` with one-transaction-per-batch upserts for all nine record types plus `DeleteFileDataAsync` (incremental per-file clear keeping the `files` row). Files/projects use `ON CONFLICT(...) DO UPDATE` to preserve the integer key across re-index; symbols/chunks/routes/DI/options resolve their `file_id` from the normalized path (skip if the file is not yet indexed); edges derive a stable id (XxHash64 of from|to|type|evidence-file) so re-index replaces rather than duplicates. Added `WorkspaceIndexStoreUpsertTests` (5 tests). New solution test total 629 (Fuse.Indexing.Tests 4 -> 9).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Indexing.Tests` 9 passed; `dotnet format Fuse.slnx --verify-no-changes` clean.
- Blockers/issues: xUnit 2.9.2 `IAsyncLifetime` uses `Task` (not v3 `ValueTask`); fixed the lifecycle method return types.
- Lessons: Records carry the natural key (normalized path) rather than the DB `file_id`, and the store resolves the integer FK per batch; this keeps the per-table upsert methods order-tolerant (files first). `DeleteFileDataAsync` deletes evidence-only edges explicitly before deleting the file's nodes, because the node delete cascades only edges whose endpoints are this file's nodes. `ChunkRecord` already carries `Body`/`Comments`/`SymbolsText` (relationally unused) so P1.3 FTS can populate them without a record change.
- Time: ~30 min

### P1.3 FTS5 indexing + search - 2026-06-26 09:34
- Status: done
- Result: Added `WorkspaceIndexSchema.CreateFtsDdl` (the `chunk_fts` FTS5 virtual table over path/name/symbols/signature/comments/body, created separately from the relational schema). Store now probes FTS5 at init (`TryCreateFtsAsync`, exposes `FullTextSearchAvailable`), populates `chunk_fts` inside `UpsertChunksAsync` (delete-then-insert per chunk_id so re-index does not duplicate), clears FTS rows in `DeleteFileDataAsync`, and implements `SearchAsync` (`SearchQuery`/`SearchHit`) with positional `bm25()` weights (name/signature/symbols > path > comments/body), negated to higher-is-better, plus a safe MATCH-expression builder. Updated the migrator to drop virtual tables first so their shadow tables do not error on rebuild. Added `WorkspaceIndexFtsTests` (5 tests). New solution test total 634 (Fuse.Indexing.Tests 9 -> 14).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Indexing.Tests` 14 passed (FTS confirmed available in the bundled e_sqlite3); `dotnet format Fuse.slnx --verify-no-changes` clean.
- Blockers/issues: None. FTS5 is present in `SQLitePCLRaw.bundle_e_sqlite3`, so the availability test passes locally; the publish smoke test (P11.1) must confirm it under self-contained publish.
- Lessons: FTS5 standalone tables do not cascade and must be maintained manually on upsert and delete. The `bm25()` weight list needs one entry per declared column including the UNINDEXED `chunk_id` (weight 0). `bm25()` is lower-is-better, so scores are negated. Dropping a virtual table removes its shadow tables, so the migrator must drop virtual tables before the generic `DROP ... IF EXISTS` sweep.
- Time: ~25 min

### P1.4 Fuse.Indexing.Tests end-to-end Phase 1 - 2026-06-26 09:45
- Status: done
- Result: Added `WorkspaceIndexIntegrationTests` (2 tests): a full Phase 1 flow over a 2-file OrderService workspace (insert files/symbols/chunks/nodes/edges, FTS finds OrderService, edges persist, reopen the store and confirm data survives disposal, reindex a changed file and confirm symbols/hash replaced) and a WAL journal-mode assertion. Removed the Phase 0 `PlaceholderTests`. Phase 1 done criteria all covered across P1.2/P1.3/P1.4 tests: DB created, FTS works, upserts transactional, store disposes safely, WAL enabled. New solution test total 635 (Fuse.Indexing.Tests now 15: placeholder -1, integration +2).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test Fuse.slnx -c Release --no-build` 635 passed, 0 failed; `dotnet format Fuse.slnx --verify-no-changes` clean.
- Blockers/issues: None.
- Lessons: Reopening a `WorkspaceIndexStore` on an existing v10 DB correctly skips migration (version already at target) and re-creates the FTS table idempotently (`IF NOT EXISTS`), so persistence across disposal works without a rebuild.
- Time: ~15 min

### P2.1 File discovery + hash records - 2026-06-26 10:05
- Status: done
- Result: Added `FileHashService` (XxHash64 content hash, 16-char hex) to `Fuse.Indexing`. Added `WorkspaceFileScanner` + `FileScanRequest` and `FileClassifier` to `Fuse.Semantics`: the scanner reuses `FileCollectionPipeline` for discovery (gitignore/extension/exclusion rules, excludes bin/obj/.git/.fuse/.vs/node_modules), hashes each file once, decodes a 2 KB UTF-8 prefix (BOM-aware), and builds `IndexedFileRecord`s with test/generated flags. `FileClassifier` uses path + content-prefix heuristics (test: path markers, `*Tests.cs`/`*Specs.cs`, xUnit/NUnit/MSTest markers; generated: `.g.cs`/`.Designer.cs`/`.generated.cs`, EF ModelSnapshot, `<auto-generated>`). Added direct `Fuse.Collection` project reference to `Fuse.Semantics`. Added `WorkspaceFileScannerTests` (4 tests), removed the Phase 0 placeholder. New solution test total 638 (Fuse.Semantics.Tests 1 -> 4).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test Fuse.slnx -c Release --no-build` 638 passed; `dotnet format Fuse.slnx --verify-no-changes` clean (one auto-format whitespace fix applied to the options call).
- Blockers/issues: None. Generated/test files are intentionally included and flagged (excludeAutoGenerated=false) rather than dropped, so retrieval can reason about them.
- Lessons: `FileCollectionPipeline` needs IFileSystem + GitIgnoreParser + an `IFileFilter` list; tests construct it directly with `PhysicalFileSystem` and the core filter set. The scanner uses `SourceFile.NormalizedRelativePath` as the cross-record linkage key (matching the store's `normalized_path` FK resolution from P1.2).
- Time: ~25 min

### P2.2 Syntax symbol + chunk extraction into store - 2026-06-26 10:22
- Status: done
- Result: Added `SyntaxSymbolExtractor` (+ `SyntaxExtractionResult`) and `SyntaxIndexer` (+ `SyntaxIndexResult`) to `Fuse.Semantics`. The extractor walks `BaseTypeDeclarationSyntax` for type symbols/chunks (kind, FQN, namespace, accessibility, signature, public-API flag) and reuses `RoslynSymbolChunkExtractor` for member chunks (deriving member symbols from them, keyed on the extractor's collision-free identity). Symbol ids use the source-only fallback form `symbol:fallback:{path}:{kind}:{stableKey}:{line}`; chunk bodies populated for FTS; tokens estimated at ~4 chars/token. `SyntaxIndexer` orchestrates scan -> upsert files -> extract+upsert symbols/chunks per .cs file. Added `SyntaxIndexerTests` (3 tests). New solution test total 641 (Fuse.Semantics.Tests 4 -> 7).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Semantics.Tests` 7 passed (type+member symbols stored, FTS finds OrderService, reindex after edit trims symbols); `dotnet format Fuse.slnx --verify-no-changes` clean.
- Blockers/issues: None.
- Lessons: The chunk extractor only yields member chunks, so type-level symbols/chunks are walked separately and members reuse the extractor's `Identity` for stable keys. Reindex correctness depends on `DeleteFileDataAsync` (P1.2) clearing prior symbols/chunks/FTS before re-extraction. The indexer re-reads file text (the scanner already read bytes for hashing) - a candidate optimization for incremental indexing later.
- Time: ~25 min

### P2.3 Route syntax extraction into records - 2026-06-26 10:38
- Status: done
- Result: Added `SyntaxRouteExtractor` to `Fuse.Semantics`, mirroring the route-map generator's detection (HttpVerb attributes, controller `[Route]` prefixes, minimal-API `Map*` calls) but emitting `RouteRecord`s with normalized leading-slash patterns, `route:{METHOD}:{pattern}` ids, source kind (`mvc`/`minimal-api`), and the handler name in `metadata_json`. Wired into `SyntaxIndexer` (now upserts routes; `SyntaxIndexResult` gains `RouteCount`). Added `SyntaxRouteExtractorTests` (3) and an indexer route-storage test (1). New solution test total 645 (Fuse.Semantics.Tests 7 -> 11).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Semantics.Tests` 11 passed; `dotnet format Fuse.slnx --verify-no-changes` clean.
- Blockers/issues: First indexer route test asserted GET `/api/orders` for a verb-only `[HttpGet]` action; the inherited generator heuristic falls back to the handler name, so the real pattern is `/api/orders/List`. Corrected the test to document that behavior.
- Lessons: `HandlerSymbolId` is left null at the syntax stage; the route-to-handler edge is wired semantically in Phase 4 (`AspNetRouteAnalyzer`), which can read the handler name from `metadata_json`. Route patterns are normalized to a leading slash so `route:METHOD:/pattern` node ids match what resolve/retrieval will look up.
- Time: ~20 min

### P2.4 fuse index + fuse map acceptance - 2026-06-26 11:05
- Status: done
- Result: Added the first V3 CLI commands `IndexCommand` (`fuse index [path] [--force]`) and `MapCommand` (`fuse map [path] [--detail symbols|routes|all] [--max-rows N]`), wired into `Program.cs`. Added `WorkspaceMapRenderer` + `MapDetail` to `Fuse.Semantics` and read methods `ListSymbolsAsync`/`ListRoutesAsync` (+ `SymbolListItem`/`RouteListItem` DTOs) to the store. Registered the stateless indexing components in `AddFuse` (`AddSemanticIndexing`); the per-workspace store is constructed in the command from `FuseStorePaths.ResolveDatabasePath`. Added `Fuse.Indexing`/`Fuse.Semantics` project references to `Fuse.Cli`. Verified end-to-end against a sample repo: `fuse index` -> "Indexed 2 files: 6 symbols, 6 chunks, 1 routes"; `fuse map --detail all` lists symbols (public-API first) and the POST /api/orders/{id} route. Added `WorkspaceMapRendererTests` (3 tests). New solution test total 648 (Fuse.Semantics.Tests 11 -> 14).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test Fuse.slnx -c Release --no-build` 648 passed; `dotnet format Fuse.slnx --verify-no-changes` clean; manual CLI run of `fuse index`/`fuse map` succeeds.
- Blockers/issues: DotMake auto-generated subcommand short forms collided (`index` vs `init` both -> `-i`): "An item with the same key has already been added. Key: i". Fixed by `ShortFormAutoGenerate = CliNameAutoGenerate.None` on both new commands. The source generator also cannot read a `private const` used as a `[CliOption]` default, so the default is an inline literal.
- Lessons: The CLI assembly name is `fuse` (`fuse.exe`/`fuse.dll`), not `Fuse.Cli.dll`. For a non-git workspace `FuseStorePaths` falls back to `~/.fuse/fuse.db`; per-workspace isolation is the warm host's job (Phase 11). The first V3 run drops the pre-V3 cache db (expected v10 migration). These minimal commands are formalized/expanded in Phase 8.
- Time: ~40 min

### P3.1 DotNetWorkspaceDiscoverer - 2026-06-26 11:18
- Status: done
- Result: Added `DotNetWorkspaceDiscoverer` + `WorkspaceDiscoveryResult` + `WorkspaceKind` to `Fuse.Semantics`. Discovery walks the tree pruning bin/obj/.git/.fuse/.vs/node_modules, then picks: a unique `.sln` (preferred), else a unique `.slnx` when no `.sln`, else `Projects` over all discovered `.csproj`, else `SyntaxOnly`. Pure filesystem logic (no MSBuild), so no runtime/AOT tension here. Added `DotNetWorkspaceDiscovererTests` (6 tests covering each branch + ignore rules). New solution test total 654 (Fuse.Semantics.Tests 14 -> 20).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Semantics.Tests` 20 passed; `dotnet format Fuse.slnx --verify-no-changes` clean.
- Blockers/issues: None. The MSBuild-at-runtime / AOT tension flagged in the plan begins at P3.2 (RoslynWorkspaceLoader), not here.
- Lessons: Discovery deliberately does not parse the solution/project files; that is the loader's job (P3.2). `ProjectPaths` is populated even in `Solution` mode so a load failure can fall back to per-project or syntax indexing.
- Time: ~15 min

### P3.2 RoslynWorkspaceLoader + guarded MSBuildLocator + syntax fallback - 2026-06-26 11:40
- Status: done
- Result: Added `RoslynWorkspaceLoader` (+ `RoslynWorkspaceSnapshot`, `LoadedProject`) and `DiagnosticRecord`/`DiagnosticSeverity` to `Fuse.Semantics`. The loader registers `MSBuildLocator.RegisterDefaults()` once (lock-guarded static), opens the solution or each project via `MSBuildWorkspace`, collects `WorkspaceFailed` diagnostics as warnings, and returns compilations. Every failure mode degrades to `SemanticLoadSucceeded=false` with a diagnostic rather than throwing: SyntaxOnly discovery (info), no SDK/locator failure (`msbuild-unavailable`), load exception (`msbuild-load-failed`), no compilations (`no-projects-loaded`). Added `RoslynWorkspaceLoaderTests` (2). New solution test total 656 (Fuse.Semantics.Tests 20 -> 22).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test Fuse.slnx -c Release --no-build` 656 passed; `dotnet format --verify-no-changes` clean. MSBuild loads in this environment (under `dotnet test` the SDK is present); the SampleShop.Core fixture loads semantically and `OrderService` resolves as a type symbol - confirmed to pass even with the fixture's obj/bin deleted (CI-safe, no pre-restore needed).
- Blockers/issues: First load failed with "Cannot open project ... language 'C#' is not supported" - the C# workspace language service was missing at runtime. Fixed by adding `Microsoft.CodeAnalysis.CSharp.Workspaces` 4.14.0 to `Directory.Packages.props` and referencing it from `Fuse.Semantics`. The MSBuild-absent fallback path is implemented but its real exercise is the P11.1 self-contained publish smoke test.
- Lessons: `MSBuildWorkspace` needs BOTH `Microsoft.CodeAnalysis.Workspaces.MSBuild` AND the language-specific `Microsoft.CodeAnalysis.CSharp.Workspaces` assembly present, or it reports the language as unsupported. A `Compilation` resolves declared type symbols without restore (references only matter for external types), so semantic symbol extraction does not require a restored fixture. The new package flows transitively to `Fuse.Cli`, enlarging the publish; the MSBuild-unavailable fallback must be verified at publish time.
- Time: ~35 min

### P3.3 Semantic symbol extraction + SymbolIdBuilder - 2026-06-26 11:55
- Status: done
- Result: Added `SymbolIdBuilder` (stable id `symbol:{assembly}:{kind}:{hash}` from assembly + documentation-comment id, XxHash64-bounded) and `SemanticSymbolExtractor` to `Fuse.Semantics`. The extractor walks a `LoadedProject` compilation's namespaces/types/nested-types/members, emitting `SymbolRecord`s with resolved metadata (FQN, metadata name, namespace, assembly, accessibility, signature via a custom `SymbolDisplayFormat`, public-API visibility computed up the containing-type chain), keyed to files by normalized relative path. Skips implicitly-declared symbols and property/event accessor methods. Added `SemanticSymbolExtractorTests` (3: id stability across compilations, metadata extraction, accessor skipping). New solution test total 659 (Fuse.Semantics.Tests 22 -> 25).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Semantics.Tests` 25 passed; `dotnet format --verify-no-changes` clean.
- Blockers/issues: None.
- Lessons: `ISymbol.GetDocumentationCommentId()` is the ideal stable, position-independent, parameter-inclusive descriptor for symbol ids - no need to hand-assemble parameter type displays. The test compiles in-memory via `CSharpCompilation.Create` (no MSBuild) for speed; the loader path is covered separately in P3.2. Semantic ids replace P2.2's `symbol:fallback:...` ids when semantic load succeeds; P3.4 wires which extractor the indexer uses based on load status.
- Time: ~30 min

### P3.4 Project records + file linkage; Semantic/Partial status - 2026-06-26 12:12
- Status: done
- Result: Added the integrating `SemanticIndexer` (+ `SemanticIndexResult`) to `Fuse.Semantics`: discover -> load -> on semantic success upsert project records (path/name/assembly/hash), link files to projects (file->project map from each compilation's syntax-tree paths), upsert semantic symbols, and upsert chunks/routes from syntax (chunk symbol ids dropped in semantic mode to avoid dangling refs); on load failure fall back to full syntax indexing. Mode is `semantic` (clean), `partial` (any load diagnostic), or `syntax` (fallback), persisted to a new `index_meta` table and surfaced via `WorkspaceIndexState.Mode`. Added store `SetMetaAsync`/`GetMetaAsync` + `index_meta` schema table. Added `SemanticIndexerTests` (1, against the real SampleShop.Core fixture: mode semantic/partial, project records, files linked, no fallback symbol ids, OrderService symbol present). New solution test total 660 (Fuse.Semantics.Tests 25 -> 26).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test Fuse.slnx -c Release --no-build` 660 passed; `dotnet format --verify-no-changes` clean. The real-project test passes (loads SampleShop.Core via MSBuild, links files, emits assembly-qualified symbol ids).
- Blockers/issues: None. Without a fixture restore the mode is `partial` (MSBuild design-time warnings); with restore it is `semantic` - the test accepts either, matching the item's "Semantic or Partial".
- Lessons: File-to-project linkage is derived from `Compilation.SyntaxTrees[].FilePath` (relative to root), which matches the scanner's normalized paths, so `files.project_id` resolves through `ProjectRecord.Path`. Chunks/routes stay syntax-driven in both modes so FTS is always populated; only symbols differ by mode. Index mode lives in `index_meta` rather than the host lifecycle `Status`, keeping the two concepts separate.
- Time: ~30 min

### P4.1 Shared semantic fixture app - 2026-06-26 12:45
- Status: done
- Result: Added the `OrderingApp` fixture under `tests/fixtures/OrderingApp` exercising every analyzer: `IOrderService`/`OrderService` (DI + `IOptions<OrderOptions>` consumption), `OrdersController` (`[ApiController]`, `[Route("api/orders")]`, `[HttpPost("{id}")]`, ctor-injected `IOrderService`+`ISender`, sends `CreateOrderCommand`), `CreateOrderCommand`/`CreateOrderHandler` (MediatR), `OrderOptions` bound to the `Orders` config section in `Program.ConfigureServices`, `OrderServiceTests`, and `appsettings.json`. `Framework.cs` supplies minimal stubs for MediatR, ASP.NET Core MVC, and `Microsoft.Extensions.*` in their real namespaces so the fixture is hermetic (compiles in-memory, no restore). Added `OrderingAppFixture` test loader (TPA reference set minus the stubbed-namespace assemblies) and `OrderingAppFixtureTests` (9: clean compile, 7 expected types, interface/handler implementation). README documents the expected edge set. New solution test total 669 (Fuse.Semantics.Tests 26 -> 35).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test Fuse.slnx -c Release --no-build` 669 passed; `dotnet format --verify-no-changes` clean. The fixture compiles with zero error diagnostics in-memory.
- Blockers/issues: `Microsoft.CodeAnalysis.DiagnosticSeverity` collides with the new `Fuse.Semantics.DiagnosticSeverity`; fully qualified in the test. The in-memory reference set must exclude `Microsoft.Extensions.*`/`Microsoft.AspNetCore.*`/`MediatR`/`xunit` assemblies (present in the test host's TPA) or the stub namespaces would be ambiguously defined.
- Lessons: A hermetic source-stub fixture compiled in-memory is faster and more deterministic than a restored project for analyzer unit tests, and stubs in the real namespaces make name+namespace detection behave identically to the real packages. The real-project MSBuild path stays covered by SampleShop (P3.2/P3.4). This fixture is the spine of P4.2-P4.8 analyzer edge assertions.
- Time: ~45 min

### P4.2 InterfaceImplementationAnalyzer - 2026-06-26 13:02
- Status: done
- Result: Added the analyzer contract (`ISemanticAnalyzer`, `SemanticAnalyzerResult`, `SemanticAnalysisContext`) and node helpers (`SemanticNodes`: type/method/route/service/config node ids, `TypeNode`/`MethodNode` record builders, `IsInSource`, `EnumerateTypes`) under `Fuse.Semantics/Analyzers`. Added `InterfaceImplementationAnalyzer` emitting `type -> interface : implements` (0.90) and `type -> base : inherits` (0.75) edges between in-source types only (skips external framework types and System.Object); reverse traversal is served by the store's incoming-edge query. Added `InterfaceImplementationAnalyzerTests` (3) against OrderingApp. New solution test total 672 (Fuse.Semantics.Tests 35 -> 38).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Semantics.Tests` 38 passed (OrderService -> IOrderService implements edge present with weight 0.90 and both endpoint nodes); `dotnet format --verify-no-changes` clean.
- Blockers/issues: None.
- Lessons: Node ids are the shared contract between analyzers (`type:{fqn}`, `method:{type}.{name}`, `route:{METHOD}:{pattern}`, `service:{name}`, `config:{section}`); `SemanticNodes` centralizes them so edges from different analyzers join. Each analyzer emits the nodes for its edge endpoints so `UpsertNodes` (INSERT OR REPLACE) can precede `UpsertEdges` and satisfy the FK. "In source" means same assembly as the compilation plus a source location, which in the hermetic fixture includes the stub interfaces (acceptable; tests assert specific edges, not exclusivity).
- Time: ~30 min

### P4.3 DiRegistrationAnalyzer - 2026-06-26 13:20
- Status: done
- Result: Added `DiRegistrationAnalyzer` emitting `service -> implementation : di_resolves_to` (0.95) edges and `DiRegistrationRecord`s. Handles generic two-type (`AddScoped<TService,TImpl>`), generic self (`AddScoped<TService>`), and `typeof` pair forms across Scoped/Singleton/Transient and `TryAdd*`; factory and single-generic-with-factory record the service with a null implementation and no resolve edge. Type args resolved via `SemanticModel.GetTypeInfo` on the syntax (works without restore). Added test helper `InlineCompilation` (compiles inline snippets with the fixture's `Framework.cs` stubs) and `DiRegistrationAnalyzerTests` (5, incl. 3 registration-shape variants). New solution test total 677 (Fuse.Semantics.Tests 38 -> 43).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Semantics.Tests` 43 passed (IOrderService -> OrderService di_resolves_to present, lifetime Scoped, generic2 kind); `dotnet format --verify-no-changes` clean.
- Blockers/issues: None. di_registers (registration-site -> service) from 6.2 is intentionally not emitted as a separate edge: it is not in the P4.8 asserted set and the `DiRegistrationRecord` already carries the file linkage review needs. di_depends_on_impl (consumer -> impl) is the constructor-injection analyzer's job (P4.4).
- Lessons: Resolving type arguments off the syntax via `GetTypeInfo` is robust whether or not the `Add*` method itself resolves, so DI detection survives an unrestored project. Self-registration (`AddScoped<T>()`) yields a di_resolves_to self-edge (service==impl), which is harmless.
- Time: ~30 min

### P4.4 ConstructorInjectionAnalyzer - 2026-06-26 13:35
- Status: done
- Result: Added `ConstructorInjectionAnalyzer` emitting `consumer -> service : di_injects` (0.75) for each in-source constructor parameter of a source class, and `consumer -> implementation : di_depends_on_impl` (0.85) when the injected service is DI-registered. It composes `DiRegistrationAnalyzer` to build the service-node -> implementation-node map from its `di_resolves_to` edges. Added `ConstructorInjectionAnalyzerTests` (3: controller injects IOrderService, controller depends on OrderService impl, handler injects IOrderService). New solution test total 680 (Fuse.Semantics.Tests 43 -> 46).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Semantics.Tests` 46 passed (OrdersController -> IOrderService di_injects and OrdersController -> OrderService di_depends_on_impl present); `dotnet format --verify-no-changes` clean.
- Blockers/issues: None. di_depends_on_impl requires the DI map, so this analyzer depends on `DiRegistrationAnalyzer` (composition) rather than recomputing registrations; the Phase 4 aggregation (P4.8) can share one DI result if needed.
- Lessons: di_depends_on_impl is the direct edge the worked example asserts, but it is derivable as a 2-hop (di_injects then di_resolves_to); emitting it directly keeps single-hop retrieval cheap. Only in-source parameter types are injected-edged to avoid flooding the graph with framework dependencies (ILogger, IOptions infra); the options consumption edge is the options analyzer's job (P4.7).
- Time: ~25 min

### P4.5 MediatRAnalyzer - 2026-06-26 13:50
- Status: done
- Result: Added `MediatRAnalyzer` emitting `request -> handler : mediatr_handles` (0.95) for source types implementing `IRequestHandler<TReq,TResp>`/`IRequestHandler<TReq>`/`INotificationHandler<TNotif>` (request is the first type argument of the handler interface), and `caller -> request : sends_request` (0.70) for `Send`/`Publish` calls whose argument type implements `IRequest`/`INotification`. Interfaces matched by simple name so real MediatR and the local stub both work. Added `MediatRAnalyzerTests` (2). New solution test total 682 (Fuse.Semantics.Tests 46 -> 48).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Semantics.Tests` 48 passed (CreateOrderCommand -> CreateOrderHandler mediatr_handles; OrdersController -> CreateOrderCommand sends_request); `dotnet format --verify-no-changes` clean.
- Blockers/issues: `SemanticModel.GetDeclaredSymbol(TypeDeclarationSyntax)` binds to the `ISymbol` overload here, so it needs an `as INamedTypeSymbol` cast.
- Lessons: The request type is recovered from the handler's implemented generic interface's first type argument, which is robust regardless of how the handler is declared. `sends_request` uses the enclosing `TypeDeclarationSyntax` as the caller, which attributes the send to the controller/service rather than to a method node.
- Time: ~25 min

### P4.6 AspNetRouteAnalyzer - 2026-06-26 14:05
- Status: done
- Result: Added `AspNetRouteAnalyzer` walking MVC controller actions, building route nodes (`route:{METHOD}:{pattern}`, kind `route`) and method nodes for the resolved action symbol, and emitting `route -> handler method : route_handles` (1.00) plus `RouteRecord`s with the handler symbol id set. Controller `[Route]` prefix is combined with the action template; verb-only actions fall back to the action name. Added `AspNetRouteAnalyzerTests` (3). New solution test total 685 (Fuse.Semantics.Tests 48 -> 51).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Semantics.Tests` 51 passed (route:POST:/api/orders/{id} -> method:OrderingApp.Api.OrdersController.Create route_handles; route record carries handler symbol id; route + method nodes emitted); `dotnet format --verify-no-changes` clean.
- Blockers/issues: None. Minimal-API routes stay with the syntax route extractor (P2.3); this analyzer is the semantic MVC route-to-method edge. Pattern helpers are inlined here rather than refactoring the green `SyntaxRouteExtractor`.
- Lessons: The route handler edge keys the method node by `method:{type}.{name}` (the same id `SemanticNodes.MethodId` produces), so review/resolve can hop route -> method -> containing type -> injected services. The handler symbol id on the route record lets resolve return the action without a graph hop.
- Time: ~25 min

### P4.7 OptionsBindingAnalyzer - 2026-06-26 14:25
- Status: done
- Result: Added `OptionsBindingAnalyzer` emitting `config:Section -> TOptions : options_binds` (0.85) from `Configure<TOptions>(...GetSection("Section")...)` plus an `OptionsBindingRecord`, and `consumer -> TOptions : options_consumes` (0.75) from `IOptions<T>`/`IOptionsMonitor<T>`/`IOptionsSnapshot<T>` constructor parameters. Also indexes top-level `appsettings*.json` sections as `config:{section}` nodes (JsonDocument, bin/obj skipped, malformed/unreadable skipped). Added `OptionsBindingAnalyzerTests` (3). New solution test total 688 (Fuse.Semantics.Tests 51 -> 54).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Semantics.Tests` 54 passed (config:Orders -> OrderOptions options_binds; OrderService -> OrderOptions options_consumes; Orders config node present); `dotnet format --verify-no-changes` clean.
- Blockers/issues: None. `config_impacts` (config -> consumer) from 6.6 is not emitted yet (not in the P4.8 asserted set); it is derivable as config -binds-> options -consumes-by- consumer and can be added if retrieval needs the direct hop.
- Lessons: The config section name is found by searching the Configure call's arguments for a `GetSection("X")` invocation, which tolerates chained/extra arguments. `JsonDocument` reads arbitrary config json (not Fuse DTO serialization), so it does not conflict with the source-generated-JSON design invariant. Config nodes come from both the bind call and appsettings, deduped by node id via `TryAdd`.
- Time: ~30 min

### P4.8 Fixture edges + fuse resolve - 2026-06-26 15:05
- Status: done
- Result: Added `SemanticAnalysisRunner` (aggregates the 6 analyzers, `CreateDefault`), wired it into `SemanticIndexer` (semantic mode now upserts nodes/edges/routes/DI/options from the analyzers after symbols). Added store graph queries (`GetNodeAsync`, `FindNodesByDisplayNameAsync`, `GetOutgoingEdgesAsync`, `GetIncomingEdgesAsync`). Added `SemanticResolver` (+ `ResolveResult`/`ResolvedNode`/`ResolveTarget`) in `Fuse.Retrieval` resolving service/request/route/config/symbol via graph edges. Switched `IndexCommand` to `SemanticIndexer`; added `ResolveCommand` (`fuse resolve --service|--request|--route|--config|--symbol`); registered the semantic components in DI; added `Fuse.Retrieval` reference to `Fuse.Cli`. Added `OrderingApp.csproj` (no package refs) so the fixture loads via MSBuild. Tests: `FixtureEdgeSetTests` (9, all worked-example edges + endpoint-node invariant) and `SemanticResolverTests` (4, seeded store); removed the Retrieval placeholder. New solution test total 700.
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test Fuse.slnx -c Release --no-build` 700 passed; `dotnet format --verify-no-changes` clean. End-to-end CLI confirmed against the OrderingApp fixture: `fuse index` reports [semantic] 11 files/1 project/72 symbols; `fuse resolve --service IOrderService` -> OrderService (di_resolves_to), `--request CreateOrderCommand` -> CreateOrderHandler (mediatr_handles), `--route "POST /api/orders/{id}"` -> OrdersController.Create (route_handles), `--config Orders` -> OrderOptions (options_binds).
- Blockers/issues: DotMake marks nullable `string?` options Required by default; added `Required = false` to each resolve option. The fixture lives inside the Fuse git repo, so `FuseStorePaths` resolves the store to the repo root `.fuse/fuse.db` (gitignored) for both index and resolve, which keeps them consistent.
- Lessons: Resolution is a uniform graph lookup (find source node by name or constructed id, follow one typed edge), so all five resolve kinds share `FollowEdgesAsync`. The analyzer set is wired once in `SemanticAnalysisRunner.CreateDefault` and reused by both the indexer and tests. Switching `fuse index` to the semantic pipeline means a real workspace now gets the full graph, not just syntax symbols.
- Time: ~60 min

### P5.1 LocalizationRequest + candidate generators - 2026-06-26 15:30
- Status: done
- Result: Added `LocalizationRequest`, `CandidateNode`, `CandidateSource` (+ `CandidateSourceWeights`) and the candidate generators to `Fuse.Retrieval`: `ExactCandidateGenerator` (service/request/route/config/symbol via `SemanticResolver`), `FtsCandidateGenerator` (FtsSymbol when a hit name matches a query token, else FtsBody), `PathCandidateGenerator` (path LIKE on 3+ char tokens), `DiffCandidateGenerator` (SelectedPaths -> must-keep), and the composite `CandidateGenerator.CreateDefault`. Added store `FindFilesByPathAsync` + `FileListItem`. Added `CandidateGeneratorTests` (5). New solution test total 705 (Fuse.Retrieval.Tests 4 -> 9).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Retrieval.Tests` 9 passed (each source generates correctly; no-signal yields no candidates); `dotnet format --verify-no-changes` clean.
- Blockers/issues: Git base resolution for `ChangedSince` is deferred to P6.1 (review wires `GitChangeDetector`); the diff generator handles explicit `SelectedPaths` for now. `TokenEstimate` is 0 until packing (P5.4) resolves per-file token costs from chunks. Stack-trace parsing (`StackTraceCandidateGenerator`) is not yet implemented (the `StackTrace` field is reserved).
- Lessons: Candidates carry a `NodeId` (set for exact/graph candidates, empty for file-only FTS/path/diff candidates), which P5.3 graph expansion keys on; file-only candidates pass through to the plan directly. Base score starts at the source weight; P5.2 normalizes across sources. The "no signal yields nothing" case is the honest low-signal floor the localize benchmark (Suite C) checks for.
- Time: ~35 min

### P5.2 Score normalization - 2026-06-26 15:45
- Status: done
- Result: Added `CandidateScorer` (+ `ScoredCandidate`) to `Fuse.Retrieval`. Candidates are grouped by node id (or `file:`+path for file-only candidates), each group's sources combined with a noisy-or (`1 - product(1 - score)`) so corroboration raises the score toward but not past 1, with merged sources/reasons and max token estimate. Output is ranked by score desc then path. Added `CandidateScorerTests` (3). New solution test total 708 (Fuse.Retrieval.Tests 9 -> 12).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Retrieval.Tests` 12 passed (same-file merge to 0.865 from 0.55+0.70, distinct nodes kept separate, rank order); `dotnet format --verify-no-changes` clean.
- Blockers/issues: None.
- Lessons: Noisy-or is a clean normalization that needs no global max/min pass and is stable as sources are added; a single-source candidate keeps its own weight. Grouping by node id keeps symbol-level candidates distinct from file-level ones for graph expansion (P5.3), which still lets duplicate file candidates (FTS + path) merge.
- Time: ~20 min

### P5.3 Graph expansion with typed edge weights + pruning - 2026-06-26 16:00
- Status: done
- Result: Added `EdgeWeightProvider` (per-edge-type weights from 7.6, hop decay 0.65, low default for unknown types) and `GraphExpansionEngine` (+ `ExpandedNode`) to `Fuse.Retrieval`. A max-priority frontier expands seeds across outgoing and incoming edges; child score = parent * edgeWeight * decay * ambiguityPenalty (1/sqrt(fanout)); below-threshold branches are pruned and depth is bounded; first-pop-wins gives Dijkstra-like optimal scores. Seeds are must-keep at hop 0; file-only seeds pass through. Added `GraphExpansionEngineTests` (4). New solution test total 712 (Fuse.Retrieval.Tests 12 -> 16).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Retrieval.Tests` 16 passed (expands controller -> interface -> impl, seed must-keep at hop 0, depth 0 no expansion, high threshold prunes to seed only); `dotnet format --verify-no-changes` clean.
- Blockers/issues: None. `centralityBonus` from the 7.4 formula is left at 0 for now (no centrality metric computed yet); the ambiguity penalty and decay are in place. Edge traversal is bidirectional and untyped-by-mode here; the mode-specific traversal policy (7.5) is applied by review (P6) and context planning (P5.4).
- Lessons: Because child scores are strictly non-increasing (edge weight and decay are <= 1), a max-priority frontier with visit-on-pop reaches each node by its best path without revisiting, so no score-relaxation bookkeeping is needed. `PriorityQueue` is a min-heap, so negative score is used as the priority key.
- Time: ~30 min

### P5.4 Context plan output; localize + context commands - 2026-06-26 16:40
- Status: done
- Result: Added `ContextPlan`/`ContextPlanItem`/`ContextPlanEdge`/`RenderTier`, `ContextRequest`/`ContextSeed`, `LocalizationResult`/`LocalizedCandidate`, and `SemanticRetrievalEngine` (LocalizeAsync: candidates -> score -> rank with token costs and low-signal warning; PlanContextAsync: seeds -> resolve to nodes -> expand -> collapse to files -> role + render tier + greedy budget pack) to `Fuse.Retrieval`. Added store `GetFileTokenEstimateAsync`. Added `fuse localize` and `fuse context` CLI commands. Added `SemanticRetrievalEngineTests` (4). New solution test total 716 (Fuse.Retrieval.Tests 16 -> 20). The old `QueryScopingPipeline` is not used by this path.
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test Fuse.slnx -c Release --no-build` 716 passed; `dotnet format --verify-no-changes` clean. End-to-end CLI confirmed against OrderingApp: `fuse localize --task "order service"` returns ranked candidates; `fuse context --seed IOrderService` plans IOrderService (exact-seed/Reduced, must-keep), OrderService (di-implementation), OrdersController + CreateOrderHandler (consumer), OrderOptions (config), CreateOrderCommand (request-handler).
- Blockers/issues: None functional. FTS limitation surfaced: the unicode61 tokenizer does not split camelCase, so a query token "order" does not match the FTS token "orderservice"; path matching compensated in the demo. Worth addressing for localization recall in Phase 10 (split identifier tokens into the FTS `symbols` field, or a custom tokenizer).
- Lessons: Roles are derived from the last edge in a node's provenance chain (parsed from the provenance strings), which keeps `ExpandedNode` simple; `ExplanationEdges` is left empty for now and can be reconstructed in the renderer (P7). Greedy packing always keeps must-keep seeds and drops optional files past the budget with a warning. Context seeds of any named kind resolve through `FindNodesByDisplayName`, so `--seed IOrderService` finds the interface node and expansion supplies the rest.
- Time: ~55 min

### P6.1 Wire GitChangeDetector/GitDiffParser; changed files as must-keep seeds - 2026-06-26 17:05
- Status: done
- Result: Added `IChangeSource` (+ `ChangedFile`, `ChangeSourceException`) to `Fuse.Retrieval` to decouple the engine from the git plumbing. Updated `DiffCandidateGenerator` to resolve `ChangedSince` through an optional `IChangeSource` (combined with `SelectedPaths`, deduped, weight 1.00, source DiffChangedFile); a `ChangeSourceException` is swallowed so localization still produces other candidates. Threaded the change source through `CandidateGenerator.CreateDefault` and `SemanticRetrievalEngine`. Added `GitChangeSource` adapter in `Fuse.Cli` wrapping the existing `IChangeDetector` (reuses `GitChangeDetector`/`GitDiffParser`), registered `IChangeSource` in DI, and added `--changed-since` to `fuse localize`. Added `DiffChangeSourceTests` (4). New solution test total 720 (Fuse.Retrieval.Tests 20 -> 24).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Retrieval.Tests` 24 passed (changed-since -> diff candidates, combine+dedup with selected paths, failure swallowed, no source means none); `dotnet format --verify-no-changes` clean.
- Blockers/issues: `GitChangeDetector` already passes git args as discrete `ArgumentList` tokens with a fixed-length command (`diff --name-only <since>`), so the bounded-external-args invariant holds with no change. The adapter lives in `Fuse.Cli` (which already references Fuse.Fusion) so `Fuse.Retrieval` does not take a dependency on the retiring Fusion orchestrator.
- Lessons: Keeping the git dependency behind a Retrieval-owned `IChangeSource` interface, with the adapter in the host, preserves the layering the overhaul wants (Retrieval does not reference Fusion) while still reusing the existing detector and diff parser. Diff candidates are emitted as must-keep-weight (1.00) file candidates; their must-keep behavior is realized when used as seeds in review planning (P6.2/P6.3).
- Time: ~30 min

### P6.2 Per-changed-file semantic impact - 2026-06-26 17:25
- Status: done
- Result: Added store `GetNodesByFileAsync`. Refactored `SemanticRetrievalEngine` to extract a shared `BuildPlanAsync` (score -> expand -> collapse -> role/tier -> pack) and added `ReviewAsync` (+ `ReviewRequest`): resolves changed files via the change source, seeds the file plus every node declared in it (must-keep), expands the blast radius, and labels changed files with role `changed`. Tier mapping treats `changed` as Reduced. Added `ReviewImpactTests` (3). New solution test total 723 (Fuse.Retrieval.Tests 24 -> 27).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test tests/Fuse.Retrieval.Tests` 27 passed (changed file marked changed + must-keep; impact reaches interface and consumer; no-change-source warning); `dotnet format --verify-no-changes` clean.
- Blockers/issues: None. Co-change neighbors are not yet folded in (the co-change graph is a post-publish follow-up per Section 13); the structural blast radius (callers, DI consumers, route/request handlers, options consumers, tests) comes from graph expansion over the typed edges.
- Lessons: Review is the same planner as context with a different seeding strategy (changed-file nodes instead of explicit seeds) plus a `changed` role override, so extracting `BuildPlanAsync` avoided duplicating the collapse/role/pack logic. Seeding both the file and its nodes ensures files with no graph nodes (config json) are still kept.
- Time: ~25 min

### P6.3 Review preamble + fuse review command - 2026-06-26 18:00
- Status: done
- Result: Added `ReviewPreambleBuilder` (changed files, semantic impact with each non-changed file's edge-chain explanation, notes) to `Fuse.Retrieval`, and the `fuse review --changed-since` command (preamble + plan) to `Fuse.Cli`. Fixed a schema self-heal bug: a db created at the target version before a table was added never got the new table because migration is skipped when the version already equals the target; `InitializeAsync` now runs the idempotent `CreateTablesDdl` on every init. Added `ReviewPreambleTests` (2) and a schema self-heal regression test (1). New solution test total 726 (Fuse.Retrieval.Tests 27 -> 29, Fuse.Indexing.Tests +1).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test Fuse.slnx -c Release --no-build` 726 passed; `dotnet format --verify-no-changes` clean. End-to-end against a temp git repo: `fuse review --changed-since HEAD` detects the changed OrderService.cs and explains the blast radius (IOrderService via di_resolves_to, OrdersController/CreateOrderHandler via di_depends_on_impl, OrderOptions/Program via options, CreateOrderCommand via mediatr); every non-changed file carries its edge chain.
- Blockers/issues: Found and fixed the index_meta self-heal bug (surfaced as "no such table: index_meta" on a stale v10 db). Review requires workspace root == git root so git's repo-relative changed paths align with the index's normalized paths; documented as a constraint (the warm host will own per-workspace roots). Source body rendering is P7; review currently prints the plan.
- Lessons: Additive schema changes within one schema version need an idempotent ensure-tables on init, not just migration, or pre-existing same-version databases miss the new table. Git changed-file paths are repo-root-relative, so review path-matching depends on the indexed root being the repo root.
- Time: ~40 min

### P7.1 SemanticContextRenderer with tiered reduction - 2026-06-26 18:30
- Status: done
- Result: Added `SemanticContextRenderer` (+ `RenderedContext`, `RenderedFile`) to `Fuse.Context`. It maps each plan item's `RenderTier` to a `ReductionLevel` (FullSource->None, Reduced->Standard, Skeleton/Sketch->Skeleton, PublicApi->PublicApi, Omitted excluded) and reuses `ContentReductionPipeline` in a single pass via its per-file level selector, with redaction enabled. Added `SemanticContextRendererTests` (3) and the Roslyn plugin reference to `Fuse.Context.Tests`; removed the placeholder. New solution test total 728 (Fuse.Context.Tests 1 -> 3).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test Fuse.slnx -c Release --no-build` 728 passed (full source keeps the method body; skeleton tier keeps the signature but drops the body; omitted excluded); `dotnet format --verify-no-changes` clean.
- Blockers/issues: None. The renderer relies on `ReductionLevel` deriving `SkeletonMode`/`PublicApiMode`, so a per-file level fully captures the tier and one pipeline pass renders mixed tiers.
- Lessons: `ContentReductionPipeline.ReduceAsync` already supports per-file tiers through `perFileLevel`, so mixed-tier rendering needed no new reduction code, only a tier-to-level map. The pipeline drops trivial content and preserves input order, so rendered files come back in plan order. The manifest preamble and provenance comments (P7.2) and the output formats plus budget (P7.3) build on this `RenderedContext`.
- Time: ~35 min

### P7.2 Semantic manifest preamble + per-file provenance - 2026-06-26 18:50
- Status: done
- Result: Added `SemanticManifestBuilder` (manifest body: mode/root/changedSince/files/estimatedTokens, seeds list, semantic-impact list with each non-seed file's edge summary, notes from warnings) and `ProvenanceFormatter` (`Summarize` one-liner for the manifest, `Format` multi-line block for per-file comments) to `Fuse.Context`. Added `SemanticManifestBuilderTests` (4). New solution test total 732 (Fuse.Context.Tests 3 -> 7).
- Verification: `dotnet build Fuse.slnx -c Release` green; `dotnet test Fuse.slnx -c Release --no-build` 732 passed; `dotnet format --verify-no-changes` clean.
- Blockers/issues: None.
- Lessons: The manifest reuses the same provenance-edge parsing as the review preamble; both summarize a node's inclusion by its edge chain. The comment delimiters are deliberately left to the format-specific emitter (P7.3) so the same manifest body works for XML/Markdown/JSON.
- Time: ~20 min

### P7.3 XML/Markdown/JSON emission; budget respected - 2026-06-26 19:25
- Status: done
- Result: Added `SemanticContextEmitter` (+ `ContextOutputFormat`) and `ContextJsonDto`/`ContextFileDto` with a source-generated `FuseContextJsonContext` to `Fuse.Context`; added `Score` to `RenderedFile`. Wired rendering+emission into `fuse context` and `fuse review` (new `--format xml|markdown|json` and `--plan-only` options) via a shared `PlanFormatter` host helper; added the `Fuse.Context` reference to `Fuse.Cli`. Redaction is applied by the renderer; the budget is honored by plan packing so the emitter writes exactly the kept files. Fixed a latent P4.8 bug: `ResolveCommand` null-checked the awaited result instead of the nullable task, so the "no option specified" path would NRE; now null-checks the task. Added `SemanticContextEmitterTests` (3). New solution test total 735 (Fuse.Context.Tests 7 -> 10).
- Verification: `dotnet build Fuse.slnx -c Release` green (0 warnings after the nullable fix); `dotnet test Fuse.slnx -c Release --no-build` 735 passed; `dotnet format --verify-no-changes` clean. End-to-end: `fuse context --seed IOrderService --max-tokens 400` emits the manifest, per-file provenance, reduced bodies, and a budget note.
- Blockers/issues: JSON uses a source-generated `JsonSerializerContext` per the design invariant. "Structural maps" (route/DI/project graph) are served by `fuse map` (P2.4) plus the manifest's semantic-impact section rather than duplicated into every context payload; noted for the docs phase.
- Lessons: With the plan already packed under budget and the renderer applying redaction, the emitter is a pure formatter over `(plan, rendered)`. The XML envelope embeds raw bodies (consumer is an LLM, not a strict XML parser), matching Fuse's existing output style; only attribute values are escaped.
- Time: ~45 min


