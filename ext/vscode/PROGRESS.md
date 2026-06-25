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
- [ ] Concurrency test (simultaneous fuse/graph and fuse/scope against one root).
- [ ] Host-publish CI matrix per RID.

Build note: adding StreamJsonRpc pulled in the VS Threading analyzers (VSTHRD103, suppressed via NoWarn since it
flagged pre-existing synchronous console writes) and a transitive MessagePack with a known advisory
(GHSA-hv8m-jj95-wg3x, suppressed via NuGetAuditSuppress because the MessagePack formatter is never used; the host
uses the System.Text.Json formatter). Both recorded in DECISIONS.md rationale and the csproj comments.

## Phase 2: thin read-only extension

- [ ] Not started.

## Phase 3: context diagnostics

- [ ] Not started.

## Phase 4: graph webview with level-of-detail

- [ ] Not started.

## Phase 5: scoping commands, hover, lenses, panels

- [ ] Not started.

## Phase 6: packaging and distribution

- [ ] Not started.

## Notes and deviations

- The engine gate (`dotnet build` 0 warnings, `dotnet test`, `dotnet format --verify-no-changes`) is run after
  every host commit; the extension gate (`npm run build && npm run lint && npm test`) is added once the TS
  extension scaffolding lands in Phase 2.
