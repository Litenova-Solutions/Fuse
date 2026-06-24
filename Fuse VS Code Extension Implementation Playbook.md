# Fuse VS Code Extension Implementation Playbook

A companion to the engine playbook, written so a single autonomous session can build the extension end to end without interruption. Same working principles apply, with an added autonomy contract for unattended overnight execution.

---

## Working principles for this effort (read first)

1. **Autonomous execution, no interruption.** Run start to finish without waiting for human input. Never pause to ask a question; when a decision is ambiguous, pick the option this plan recommends (or the simpler, more reversible one), record the choice and the reasoning in `ext/vscode/DECISIONS.md`, and continue. Do not stop on the first failure: diagnose, fix, re-run, and only escalate in writing (a `BLOCKED.md` entry with the exact error, what was tried, and the smallest reproduction) if a hard external blocker (missing runtime, unavailable platform SDK, network-only dependency) genuinely prevents progress, then move to the next independent item rather than halting.
2. **Green-per-commit, commit-per-feature.** Each feature lands as its own commit only when its build, lint, and tests pass. Never commit red. After every commit, run the full gate (section 9) and fix any regression before starting the next item. Keep a running `ext/vscode/PROGRESS.md` updated after each commit: item, status, what was tested, any deviation from the plan.
3. **Tests and docs are part of done, not after.** No feature is complete without unit or integration tests for the new logic and updated docs. A feature with code but no test is not done and must not be committed as done.
4. **Breaking changes are fine.** No backward compatibility, no migration shims. The host command surface, RPC DTO shapes, the named-pipe protocol, and the extension manifest may all change freely. Update both sides and the contract test in the same commit.
5. **Hard invariants (never violate, even under time pressure).** Plain ASCII in all prose and docs (no em dash, no arrow glyphs, no emoji outside fenced code blocks). All .NET JSON via source-generated `JsonSerializerContext`. The extension and host must work fully offline (no CDN, no network telemetry, bundled webview assets). Redaction stays load-bearing: any surface that shows a secret as redacted must match what the engine emits. No network telemetry of any kind.
6. **Do not regress the engine.** The extension adds a new host command and a new top-level `ext/vscode` directory. It must not change scoping or reduction behavior or any benchmark number. If a host change touches shared engine code, run the engine gate (`dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`) and confirm the benchmark harness is unaffected.

### Autonomy decision defaults (apply without asking)

- Transport: named pipe on Windows, Unix domain socket elsewhere, pipe name derived from a hash of the repository root.
- Graph renderer: Cytoscape.js, bundled into the VSIX, no CDN.
- Distribution shape for the host: self-contained per-RID, bundled via VS Code platform-specific extensions. Defer the download-on-first-run model unless VSIX size forces it; record the size in `DECISIONS.md`.
- NativeAOT: attempt for the host; if Roslyn, `Microsoft.Data.Sqlite`, or ONNX are not AOT-compatible, fall back to self-contained trimmed and record why.
- When a test is flaky, quarantine it with a clear skip reason in `PROGRESS.md` and keep going; do not delete it.

---

## 0. Orientation

### What the extension is

A long-lived, ReSharper-style background service for VS Code that hosts the Fuse engine warm, indexes the workspace on open, and projects already-computed engine data (dependency graph, per-file token cost, centrality, redaction findings, git churn, detected patterns, cache health) into native VS Code surfaces plus one custom graph webview. It is the human-facing twin of the MCP server: the agent reads the warm engine over MCP, the developer reads the same warm engine over a UI transport, and both share one index and one SQLite store.

### What it is not

- Not a linter or C# language service. Diagnostics are framed as "Fuse: context" (token cost, secrets, graph gaps), never code quality, and never compete with the compiler's Problems entries.
- Not a second analysis engine. Every number shown comes from an engine call or notification. No structural analysis is reimplemented in TypeScript.

### The central value

Remove cold start. On workspace open the host collects, builds the dependency graph and analysis index once, holds it warm in the `NamespacedKvCache` in-memory layer backed by `.fuse/fuse.db`, and the `DebouncedFileWatcher` invalidates only changed files thereafter. After the first index, every focus, search, or graph request pays only ranking and emission. This is the ReSharper effect and the production-grade form of engine items 24 (persistent relevance stats) and C4 (pooled store).

### Layout

```
ext/vscode/
  package.json            manifest: contributes, activation, commands, menus
  DECISIONS.md            autonomy decision log (created at start)
  PROGRESS.md             per-commit status log (created at start)
  BLOCKED.md              hard-blocker log (created only if needed)
  src/
    extension.ts          activation, host lifecycle, command registration
    host/
      client.ts           JSON-RPC client over the named pipe / socket
      supervisor.ts       spawn, health, restart with backoff, version handshake
      protocol.ts         TS types mirroring the .NET DTOs (contract-tested)
    views/
      indexStatus.ts      TreeDataProvider: index state, cache, host RSS
      hotspots.ts         TreeDataProvider: token hotspots
      patterns.ts         TreeDataProvider: detected patterns
      scopeResult.ts      TreeDataProvider: ContextPlan from last scope
      graph/
        webview.ts        graph host, message bridge
        media/            bundled Cytoscape renderer (no CDN)
    diagnostics/
      secrets.ts          redaction findings as Diagnostics with precise spans
      tokens.ts           informational hotspot diagnostics
      graphGaps.ts        unconnected-file hints
    lenses/
      tokenLens.ts        CodeLens: token cost, centrality, dependents
      churnLens.ts        CodeLens: git churn (optional)
    hover/
      fuseHover.ts        hover card with focus action
    commands/
      scope.ts            Focus here, Search, Changes since branch, Copy context
      index.ts            Index workspace, Show graph, Explain scope
    statusBar.ts          persistent status bar item
  test/
    suite/                @vscode/test-electron integration tests
    fixtures/             sample workspaces (incl. a secret fixture)
src/Host/Fuse.Cli/
  Commands/HostCommand.cs JSON-RPC endpoint over a named pipe / UDS (new `fuse host`)
  Mcp or Host/Rpc/        StreamJsonRpc service surface + FuseHostJsonContext
```

### Tooling

```
# host (shares AddFuse DI with CLI/MCP)
dotnet build src/Host/Fuse.Cli/Fuse.Cli.csproj -c Release
# host publish matrix (per RID, for bundling)
dotnet publish src/Host/Fuse.Cli/Fuse.Cli.csproj -c Release -r win-x64 --self-contained
# (repeat for win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64)
# extension
cd ext/vscode
npm ci
npm run build            # esbuild bundle, no CDN
npm run lint
npm test                 # @vscode/test-electron, headless
npm run package          # vsce package -> .vsix
```

---

## 1. Transport decision

A VS Code extension runs in Node and cannot run .NET in-process; the engine lives in a separate long-lived process the extension spawns and supervises (the OmniSharp / Roslyn-LSP model).

| Option | Mechanism | Warm fit | Server push | Effort | Verdict |
|---|---|---|---|---|---|
| Fuse host over JSON-RPC | `StreamJsonRpc` (.NET) and `vscode-jsonrpc` (TS) over named pipe / UDS | Excellent | Native progress, invalidation, diagnostics notifications | M | Use this |
| Reuse MCP stdio | `McpServeCommand` | Good | Poor (request/response only) | S to M | Keep for the agent, not the UI |
| Full LSP | CLSP | Good | Native but wrong semantics | L | Overkill |

Build one host process exposing two transports over one `AddFuse` DI graph: the existing MCP stdio transport for the agent, and a new JSON-RPC endpoint over a named pipe for the UI, sharing one `FusionOrchestrator`, one warm `IAnalysisIndex`, one pooled SQLite store. Use a pipe (not stdio) for the UI because stdio is taken by MCP and `StdioGuard` already disables watch mode when stdio is redirected; a pipe also allows multiple UI clients across windows. JSON on the host stays on a source-generated context (`FuseHostJsonContext`); `protocol.ts` mirrors it and is pinned by a contract test.

---

## 2. RPC contract

Design around `ContextPlan` and the warm index. Methods (client calls host):

- `fuse/index { root, force }` returns `{ indexState, fileCount, elapsedMs }`. Streams `$/progress`.
- `fuse/graph { root, scope?, detail }` returns `GraphDto { nodes:[{ path, declaredTypes, centrality, tokenCost, role? }], edges:[{ from, to, weight, kind }] }`. `detail = Files | Directories` (level of detail, section 4).
- `fuse/stats { root }` returns `StatsDto { topTokenFiles, totalTokens, cacheHits, cacheMisses, indexHits, indexMisses, patternSummary, gitStats? }`.
- `fuse/diagnostics { root, kinds }` returns `DiagnosticsDto { secrets:[{ path, range, kind }], hotspots:[{ path, tokenCost }], graphGaps:[{ path, reason }], generated:[path] }`. `range` from `DefaultSecretRedactor` literal spans.
- `fuse/scope { root, mode, seed?, query?, since?, maxTokens }` returns `ScopeResultDto { plan, payloadUri }`. Writes the payload to a temp file the extension opens read-only.
- `fuse/explain { root, mode, ... }` returns the `ContextPlan` without emitting: ranked seeds, neighbours, scores, provenance, planned tier, estimated cost, omitted-and-why.
- `fuse/shutdown {}`: flush `_pending`, then exit.

Notifications (host pushes client): `$/progress`, `fuse/indexed`, `fuse/invalidated { paths }`, `fuse/log { level, message }`.

Contract test: a .NET test serializing each DTO through `FuseHostJsonContext` and a TS test parsing the same fixtures, failing on drift.

---

## 3. Warm-host lifecycle (the headline feature)

Host side: pooled repo-root-keyed store opened once (engine C4); warm index on `fuse/index` (collection plus `DependencyGraphBuilder.BuildAsync` plus analysis once, kept resident); incremental invalidation via the existing `DebouncedFileWatcher` pushing `fuse/invalidated`; a repo-scoped BM25 stats cache keyed by source dir plus options hash plus file hashes, invalidated by the watcher.

Extension side: activation on `workspaceContains:**/*.csproj` or `**/*.sln` plus `onStartupFinished`. Auto-warm under a configurable file-count threshold; above it, show "Fuse: not indexed" and warm on first use or explicit command, with progress. Scale behavior to repo size, mirroring the engine TOC degradation.

Memory guard: keep default warm state to the syntax-level analysis index and SQLite caches only. The compiled reference graph (engine item 8) and the ONNX reranker (engine item 9) are explicit user-triggered "deep index" actions with visible memory and download cost, never default warm state. Surface host RSS in the status bar tooltip.

---

## 4. The complete UI surface

VS Code does not allow arbitrary chrome. Compose the fixed contribution points. Every panel binds to data the engine already returns; the only custom-drawn surface is the graph webview.

### Activity bar container

One Fuse icon in the activity bar opens the Fuse view container (like Source Control or Test Explorer), revealing the stacked sidebar tree views below. Single obvious home base.

### Sidebar tree views (`TreeView` API)

- **Index status panel (top).** Workspace name, index state (Not indexed, Indexing N percent, Warm), file count, last index time, cache hit rate, host RSS. Inline refresh/index icon.
- **Token hotspots.** Sorted most-expensive files, each row "name (~4.8k)" with planned tier badge, icon color encoding cheap/medium/expensive. Click opens file. Bound to `EmittedFileTokens` and `TopTokenFiles`.
- **Patterns.** Detected patterns (DI, CQRS, async, repository, logging, exception), expandable to example files. Bound to `PatternSummary`.
- **Scope result.** After a focus/search/changes command, the `ContextPlan`: seeds, dependencies, dependents, skeletonized neighbours (labeled "skeleton, budget"), and dropped-but-relevant files, each row showing role, tier, and token cost. The human form of `fuse_explain`; the most useful panel because it makes scoping legible.

### Graph webview (`WebviewPanel`, the one freely drawn surface)

Opened in the editor area. Cytoscape renderer, bundled, offline. Nodes sized by centrality (PageRank, engine Q7), colored by token cost, edges weighted by reference strength and styled by kind (base type, interface, constructor param, incidental, proximity). Directory-level clustering for large repos that expands on click. When a scope is active, recolor nodes by role and tier so the user sees exactly what a fusion would include and drop. In-webview toolbar: file vs directory detail toggle, scope-mode switch, fit button. Click opens a file; right-click offers Focus here and Expand neighbours; hover shows centrality, token cost, declared types, edge counts.

Level of detail is the one genuinely new algorithm. Reuse `TableOfContentsBuilder.AggregateByDirectory`: expose `detail = Directories | Files` mirroring `TableOfContentsDetail`, render directory supernodes that lazy-load their file subgraph on click, so a 5,000-file graph never ships whole.

### Status bar

One persistent item: Fuse icon plus state, for example `Fuse: warm (266)` or `Fuse: indexing...` with a spinner. Click focuses the index panel. Tooltip shows cache hit/miss and host RSS.

### Editor decorations, lenses, gutter

- **Secret squiggles.** Redaction findings underline the exact literal range (from `DefaultSecretRedactor` spans), surfaced through a dedicated "Fuse: context" `DiagnosticCollection` so they appear in the editor and Problems panel. Hover shows the kind. Differentiated, unique, and it makes the C1 redaction fix visibly correct.
- **Token cost code lens.** Optional, toggleable lens above each type or the file header: "Fuse: ~1.2k tokens, centrality 0.71, 4 dependents."
- **Git churn lens.** Optional: "12 commits in 90d, last 3d ago" from `GitStatsProvider`.

### Hover provider

Hovering a type name shows a Fuse card: token cost to include this file, centrality, direct dependencies and dependents, and a "Focus here" action link.

### Context menus

Editor: "Fuse: Focus here", "Fuse: Copy context for agent". Explorer file/folder: "Fuse: Focus this file", "Fuse: Search within". Graph node: Open, Focus here, Expand neighbours.

### Command palette

"Fuse: Index workspace", "Fuse: Search", "Fuse: Changes since branch", "Fuse: Show dependency graph", "Fuse: Explain scope", "Fuse: Copy context for agent", "Fuse: Download rerank model".

### Inputs and pickers

Search opens a native input box; "Changes since branch" opens a branch quick-pick; results open as a read-only virtual document (the fused payload) with the scope-result panel populated alongside.

### Settings (`configuration` contribution)

`fuse.autoIndexThreshold` (file count), `fuse.showTokenLens`, `fuse.showChurnLens`, `fuse.graph.defaultDetail`, `fuse.host.path` (manual host override for offline install), `fuse.deepIndex.enabled` (compiled graph and reranker, default off).

### The `contributes` manifest (write this first)

```jsonc
{
  "contributes": {
    "viewsContainers": {
      "activitybar": [
        { "id": "fuse", "title": "Fuse", "icon": "media/fuse.svg" }
      ]
    },
    "views": {
      "fuse": [
        { "id": "fuse.indexStatus", "name": "Index" },
        { "id": "fuse.hotspots", "name": "Token Hotspots" },
        { "id": "fuse.patterns", "name": "Patterns" },
        { "id": "fuse.scopeResult", "name": "Scope Result" }
      ]
    },
    "commands": [
      { "command": "fuse.index", "title": "Fuse: Index Workspace" },
      { "command": "fuse.search", "title": "Fuse: Search" },
      { "command": "fuse.focusHere", "title": "Fuse: Focus Here" },
      { "command": "fuse.changesSince", "title": "Fuse: Changes Since Branch" },
      { "command": "fuse.showGraph", "title": "Fuse: Show Dependency Graph" },
      { "command": "fuse.explainScope", "title": "Fuse: Explain Scope" },
      { "command": "fuse.copyContext", "title": "Fuse: Copy Context For Agent" }
    ],
    "menus": {
      "editor/context": [
        { "command": "fuse.focusHere", "group": "fuse" },
        { "command": "fuse.copyContext", "group": "fuse" }
      ],
      "explorer/context": [
        { "command": "fuse.focusHere", "group": "fuse" }
      ]
    },
    "configuration": {
      "title": "Fuse",
      "properties": {
        "fuse.autoIndexThreshold": { "type": "number", "default": 2000 },
        "fuse.showTokenLens": { "type": "boolean", "default": false },
        "fuse.graph.defaultDetail": { "type": "string", "enum": ["Files", "Directories"], "default": "Directories" },
        "fuse.host.path": { "type": "string", "default": "" },
        "fuse.deepIndex.enabled": { "type": "boolean", "default": false }
      }
    }
  },
  "activationEvents": [
    "workspaceContains:**/*.csproj",
    "workspaceContains:**/*.sln",
    "onStartupFinished"
  ]
}
```

The constraint is honest: VS Code gives the activity-bar container, tree views, webviews, the status bar, diagnostics, lenses, hovers, menus, and settings. That set is enough because the engine produces the data; the extension is projection. The only freely drawn surface is the graph webview.

---

## 5. Context diagnostics detail

A dedicated "Fuse: context" collection, never mixed with compiler problems. Secrets first (precise spans from `FindStringLiteralSpans`, severity Warning, kind in the message); token hotspots (Information, suppressible); graph gaps (Hint, unconnected files); generated code (Hint, from `GeneratedCodeCollapser.IsGenerated`). All from engine data; refresh on `fuse/invalidated` per changed file. The C1 fix (redaction after post-reduction rewrites) is a prerequisite for the secrets diagnostic, because a visible contradiction between "flagged as secret" and "emitted unredacted" is worse than either alone.

---

## 6. Scoping commands

"Fuse: Focus here" (type at cursor or file), "Fuse: Search workspace", "Fuse: Changes since branch" (routes the developer to the engine's strongest mode, 87 to 88 percent recall), "Fuse: Copy context for agent" (runs the routed `ask` selector, engine item 31, copies the payload). Each goes through the same `FusionOrchestrator` as MCP, so UI and agent behavior are identical, and C2 strict token accounting guarantees the opened payload fits the requested budget.

---

## 7. Packaging and distribution

Publish the host self-contained per RID (win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64). Attempt NativeAOT; fall back to self-contained trimmed if Roslyn, `Microsoft.Data.Sqlite`, or ONNX are not AOT-compatible. Default: bundle the matching host in the VSIX via platform-specific extensions. The ONNX reranker model is never bundled; reuse the engine `fuse models download` flow and `FUSE_USER_DATA` cache, prompting before any download, so offline holds. Version handshake on connect; a mismatch triggers a clear notification, not silent protocol errors. Stand up a host-publish CI job per RID early so the extension build always has artifacts.

---

## 8. Phasing (execute in this order)

1. Host extraction: `fuse host` (JSON-RPC over a named pipe sharing `AddFuse`), pooled repo-root store (C4), warm-index lifecycle, host-publish CI matrix. Validate with a scripted RPC client. No UI yet. This also hardens the MCP path.
2. Thin read-only extension: manifest, supervisor, status bar, index-status panel, token-hotspot tree. Proves warm start, supervision, and the latency gate.
3. Context diagnostics: secrets with precise spans first, then token-hotspot and generated-code diagnostics, refreshed on invalidation.
4. Graph webview with level of detail: `fuse/graph` plus the bundled Cytoscape renderer with directory supernodes, then the scoped role/tier overlay.
5. Scoping commands, hover, lenses, and the scope-result and explainer panels, closing the interactive loop.
6. Packaging hardening: bundle vs download decision, NativeAOT vs self-contained, offline install, version handshake, marketplace preview.

The first two phases deliver the headline (cold start removed, warm index visible) and most strengthen the engine itself. Everything after is projection of existing data, so risk drops once the host and supervisor are solid.

---

## 9. Definition of done, gate, and testing

Run this gate after every commit; fix any regression before the next item.

```
# engine unchanged
dotnet build Fuse.slnx -c Release        # 0 warnings
dotnet test Fuse.slnx -c Release --no-build
dotnet format Fuse.slnx --verify-no-changes
# extension
cd ext/vscode && npm run build && npm run lint && npm test
```

Test coverage required per area:

- Host RPC: .NET unit tests per method over `FuseHostJsonContext`; a contract test pinning wire shapes; a concurrency test driving simultaneous `fuse/graph` and `fuse/scope` against one root (validates the pooled store and C3 DI concurrency under the new transport).
- Supervisor: spawn, crash detection, restart with backoff, version-mismatch handling, and graceful shutdown asserting `_pending` is flushed (a dropped flush silently loses cache writes).
- Extension integration (`@vscode/test-electron`, headless): activate against a fixture workspace, assert the status bar reaches "warm", assert hotspot and pattern trees populate, assert a secret fixture produces a diagnostic at the exact range, assert "Focus here" opens a payload and fills the scope-result panel.
- Graph LOD: a synthetic 2,000-file workspace returns directory-aggregate nodes under threshold and expands one directory on demand without shipping the full file graph.
- Latency gate (mirrors engine B13): record host p50/p95 for `fuse/graph`, `fuse/scope`, and warm vs cold `fuse/index`; fail if warm scope latency regresses beyond tolerance. The warm-host promise is the product; a regression there is a release blocker.

Telemetry stays local: an output channel logging index timings, cache hit rates, and host RSS. No network telemetry. Docs: MDX under `site/content/docs` (install, the host model, the offline story, the diagnostics framing), `CHANGELOG.md` `[Unreleased]`, and a statement that the extension shows the same warm engine the MCP server exposes.

---

## 10. Risks and constraints

- Runtime distribution is the dominant cost and least like the rest of Fuse; treat it as its own workstream with CI from day one.
- The compiled reference graph and ONNX reranker are the memory and download risks; keep them user-triggered deep actions with visible cost, never default warm state, or the extension inherits ReSharper's RAM reputation.
- The redaction invariant is more exposed in a UI than in a payload; the C1 fix is a prerequisite for the secrets diagnostic.
- Graph scale is the one genuine new algorithm; lean on the existing TOC aggregation rather than inventing a clustering scheme.
- Supervision is unglamorous and mandatory: crash/restart, multi-root (one host per root, or one host with multiple warm indexes keyed by root), version handshake, flushed shutdown. The existing `StdioGuard` and watch-disable logic show the engine already reasons about process and stream edge cases; the daemon adds the full supervision surface.

The two genuinely new engineering efforts are runtime packaging and graph level-of-detail. Everything else is a typed RPC projection of data `FusionResult`, `ContextPlan`, `DependencyGraph`, `DefaultSecretRedactor`, `GitStatsProvider`, and the statistics objects already compute. The warm-host work built for the UI is the same work that makes the MCP agent path faster, so both surfaces compound on one investment.

---

## 11. Overnight run protocol (operational)

1. At start, create `DECISIONS.md`, `PROGRESS.md`, and (if needed) `BLOCKED.md`. Record the autonomy defaults from the top of this plan as the initial decisions.
2. Work phases 1 through 6 in order. Within a phase, complete each item to its definition of done (code, tests, docs, gate green), commit, update `PROGRESS.md`, then continue.
3. On any failure: diagnose, fix, re-run the gate, and proceed. Do not stop. If a single item is genuinely blocked by an external dependency, write a `BLOCKED.md` entry and move to the next independent item; return to blocked items at the end.
4. Never leave the tree red. If a commit's gate fails and cannot be fixed quickly, revert that commit, log it in `PROGRESS.md`, and move on.
5. At the end, write a `SUMMARY.md`: items completed, items blocked with reasons, test and gate status, the resolved host VSIX size, latency numbers from the gate, and a recommended next-session order for anything unfinished.

If you want, the first artifact to generate at run start is the concrete `StreamJsonRpc` service interface plus the matching `FuseHostJsonContext` and the `protocol.ts` it pins, since that contract gates every UI surface that follows.