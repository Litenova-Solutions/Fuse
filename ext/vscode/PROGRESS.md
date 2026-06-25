# Fuse VS Code extension: progress log

Per-commit status for the extension build. Updated after each commit: item, status, what was tested, any
deviation from the playbook.

## Phase 1: host extraction (fuse host JSON-RPC)

- [x] `fuse host` command: JSON-RPC over a named pipe (Windows) / Unix domain socket (else) sharing AddFuse DI,
      with an accept loop that serves multiple connections (multiple editor windows) until shutdown. Tested.
- [x] Host RPC DTOs and source-generated `FuseHostJsonContext` (camelCase, reflection-free). Mirrored in
      `src/host/protocol.ts`, pinned by `FuseHostContractTests`.
- [x] Lifecycle methods: `fuse/handshake` (version match), `fuse/stats` (process health), `fuse/shutdown`.
      Verified end to end by `FuseHostServiceRpcTests` over an in-memory duplex pipe.
- [x] `fuse/index`: warms the engine (collect plus analysis index plus graph through the shared orchestrator)
      and returns the index state and file count. Integration-tested against a fixture (warm and missing-dir).
- [x] `fuse/scope`: runs a focus/search/changes fusion through the shared orchestrator, returns the emitted
      file plan with token costs, and writes the payload to a temp file the extension opens read-only.
      Integration-tested (search surfaces the matched file and writes a readable payload).
- [x] `fuse/graph`: projects the dependency graph (nodes with declared types, PageRank centrality, and an
      estimated token cost; reference edges) at `Files` or `Directories` level of detail (directory supernodes
      fold files and aggregate cross-directory edges). Integration-tested (a reference edge appears at file
      level; directories fold files into supernodes).
- [x] `fuse/diagnostics`: returns secret findings with precise zero-based editor ranges, computed read-only
      with the same redactor the reduction path uses (new additive `ISecretRedactor.FindSecretSpans`, which
      never changes redaction output). Integration-tested (a secret lands at the exact line and column).
      Hotspots and graph gaps layer onto this method next.
- [x] `fuse/explain`: returns the scoped result's context plan (each planned file's role, reduction tier, and
      score) without writing a payload, via a new additive read-only `FusionResult.Plan` projection from the
      orchestrator. Integration-tested (the matched file appears with a role and tier). All eight Phase 1 RPC
      methods now work end to end (16 host tests).
- [ ] Warm-index lifecycle (pooled repo-root store, resident analysis index, incremental invalidation).
- [x] Concurrency test: simultaneous fuse/graph and fuse/scope against one root both succeed (exercises the
      shared orchestrator and pooled store under concurrent calls, C3 DI concurrency under the new transport).
- [ ] Warm-index lifecycle (pooled repo-root store, resident analysis index, watcher invalidation pushing
      fuse/invalidated). Note: fuse/index already warms the shared index via the persistent-index path.
- [ ] Host-publish CI matrix per RID.

Build note: adding StreamJsonRpc pulled in the VS Threading analyzers (VSTHRD103, suppressed via NoWarn since it
flagged pre-existing synchronous console writes) and a transitive MessagePack with a known advisory
(GHSA-hv8m-jj95-wg3x, suppressed via NuGetAuditSuppress because the MessagePack formatter is never used; the host
uses the System.Text.Json formatter). Both recorded in DECISIONS.md rationale and the csproj comments.

## Phase 2: thin read-only extension

- [x] Build tooling: `package.json` (manifest with the Fuse activity-bar container, index and hotspot views,
      index/restart commands, and settings), `tsconfig`, esbuild bundle (no CDN, vscode-jsonrpc bundled),
      eslint. `npm run build`, `tsc --noEmit`, and `npm run lint` all green.
- [x] Supervisor: spawns `fuse host --directory <root>`, connects over the pipe/socket with linear backoff,
      verifies the protocol version at handshake, and restarts with capped backoff on host exit.
- [x] Typed client: `vscode-jsonrpc` over the transport, one method per host RPC; endpoint address mirrors the
      .NET `HostEndpoint` hash so the extension finds the host.
- [x] Status bar (state plus host RSS tooltip), index-status tree (state, file count, index time, RSS), and
      token-hotspot tree (top files by the graph's token-cost estimate, click to open).
- [ ] `@vscode/test-electron` integration test (needs an Electron download and a display; to be added with a
      headless-capable harness, quarantined if the runner cannot launch VS Code).
- [ ] TS-side contract test parsing the same fixtures as the .NET `FuseHostContractTests`.

## Phase 3: context diagnostics

- [x] Secret diagnostics: a dedicated "Fuse: context" `DiagnosticCollection` (never mixed with compiler
      problems) populated from `fuse/diagnostics`, underlining each secret literal at the host-reported range
      with the kind in the message. Refreshed on index/warm. Build, typecheck, and lint green.
- [x] Token-hotspot (Information) and graph-gap (Hint) diagnostics: `fuse/diagnostics` now also returns the
      most token-expensive files and the files with no dependency edge; the extension surfaces them in the same
      "Fuse: context" collection. The concurrency test caught and fixed a real bug (concurrent same-mode scopes
      collided on one payload temp file; payload names are now unique per call). Host and extension gates green.
- [ ] Generated-code diagnostics and refresh on `fuse/invalidated` per changed file (needs the watcher
      lifecycle, a host server-push feature).

## Phase 4: graph webview with level-of-detail

- [x] Cytoscape graph webview, bundled offline (esbuild inlines Cytoscape into `dist/webview.js`, no CDN),
      under a strict CSP (nonce script, no external sources). The host fetches `fuse/graph` at the Directories
      level of detail and posts it; nodes are sized by centrality and colored by token cost, edges show
      references, and clicking a node opens the file. "Fuse: Show Dependency Graph" command. The webview has its
      own DOM-aware `tsconfig.webview.json`; both tsconfigs typecheck clean alongside the build and lint.
- [x] Scoped role overlay: `fuse/graph` takes an optional scope (mode/seed/query/since) and tags each node with
      the role the context plan assigned (Seed, Changed, Dependency); the webview recolors by role when a scope
      is active. "Show Dependency Graph" overlays the most recent scope automatically. Host test covers it.
- [ ] Directory-supernode expand-on-click (the host already serves directory-level detail; expand-to-files is
      the remaining interaction).

## Phase 5: scoping commands, hover, lenses, panels

- [x] Scoping commands close the interactive loop: "Fuse: Search" (input box), "Fuse: Focus Here" (editor and
      explorer context menu, workspace-relative seed), and "Fuse: Changes Since Branch" (git base input). Each
      runs a scoped fusion on the host, opens the payload read-only, and populates the Scope Result tree (mode,
      included files, token costs). Build, typecheck, and lint green.
- [x] Token CodeLens: a lens at the top of each `.cs` file showing its Fuse token cost and graph centrality,
      fed from the graph the extension already fetched and toggled by the `fuse.showTokenLens` setting (default
      off). Build, typecheck, lint green.
- [x] Hover provider: hovering a `.cs` file shows a Fuse card (token cost, centrality) with a "Focus here"
      action link, reading the same graph metrics as the lens. Build, typecheck, lint green.
- [x] Explainer panel: "Fuse: Explain Scope" calls fuse/explain and shows each planned file with its role,
      tier, and score in an Explain tree, so an agent or developer sees why a file is in and at what fidelity
      before fetching. Build, typecheck, lint green; VSIX repackages (230.74 KB).

Phase 5 is complete (scoping commands, scope-result panel, token lens, hover, explainer panel). The optional
git-churn lens is the only remaining nice-to-have.

## Phase 6: packaging and distribution

- [x] VSIX packaging: `npm run package` (vsce, `--no-dependencies` since esbuild bundles the runtime deps)
      produces `fuse-vscode-3.0.0.vsix` at 229.92 KB. A `.vscodeignore` ships only `dist/`, `media/`, the
      manifest, and the README; a README documents the host model and the offline story. Size recorded in
      DECISIONS.md.
- [ ] Bundle a self-contained host per RID via platform-specific extensions (or download-on-first-run), and the
      per-RID host-publish CI matrix. The base VSIX relies on the `fuse` global tool / `fuse.host.path` for now.

## Notes and deviations

- The engine gate (`dotnet build` 0 warnings, `dotnet test`, `dotnet format --verify-no-changes`) is run after
  every host commit; the extension gate (`npm run build && npm run lint && npm test`) is added once the TS
  extension scaffolding lands in Phase 2.
