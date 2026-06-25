# Fuse VS Code extension: session summary

## Completed so far (committed, gate green at each commit)

Phase 1, the `fuse host` JSON-RPC endpoint, transport plus five working methods:

- `fuse host` command serving the warm engine over a named pipe (Windows) or Unix domain socket (elsewhere),
  sharing the same `AddFuse` DI graph as `fuse mcp serve`. Address derived from the repository root; accept loop
  serves multiple connections until `fuse/shutdown`.
- Wire contract: source-generated `FuseHostJsonContext` over the RPC DTOs, mirrored by `src/host/protocol.ts`,
  pinned by `FuseHostContractTests`. `FuseHostConnection` centralizes the StreamJsonRpc setup.
- Methods, all tested: `fuse/handshake` (version match), `fuse/stats` (process health, host RSS),
  `fuse/index` (warms the shared engine, returns state and file count), `fuse/scope` (runs a focus/search/
  changes fusion through the shared orchestrator, returns the emitted file plan with token costs, and writes
  the payload to a temp file the extension opens read-only), `fuse/shutdown`. Verified by
  `FuseHostServiceRpcTests` over an in-memory duplex pipe and the real `AddFuseForTests` provider (10 host tests).

Gate at each commit: `dotnet build Fuse.slnx -c Release` 0 warnings, all tests pass, `dotnet format
--verify-no-changes` clean. Engine behavior and every benchmark number unchanged.

## Phase 1 RPC surface: 7 of 8 methods done

Working and tested end to end (14 host tests): `fuse/handshake`, `fuse/stats`, `fuse/index`, `fuse/scope`,
`fuse/graph` (file and directory level of detail), `fuse/diagnostics` (secret findings with precise editor
ranges, via the new read-only `ISecretRedactor.FindSecretSpans` that leaves redaction output byte-identical),
and `fuse/shutdown`.

## Remaining (recommended next-session order)

1. Finish Phase 1:
   - `fuse/explain`: surface a read-only projection of the `ContextPlan` (ranked seeds, neighbours, scores,
     provenance, planned tier, omitted-and-why). The plan is built in the orchestrator (`ContextPlanBuilder
     .Build`) but is `internal` and constructed well before, and separately from, the `FusionResult`. The clean
     change is a public `PlannedFileInfo` projection added as an additive, defaulted field on `FusionResult`,
     populated at the main success-path construction; verify it is in scope there (the result is built in a
     helper) and add a host test asserting roles and tiers. A core-flow change, so its own careful commit.
   - Then add hotspots (`TopTokenFiles`) and graph gaps (unconnected files) to `fuse/diagnostics` (no engine
     change needed), the warm-index lifecycle (pooled repo-root store, resident index, watcher invalidation
     pushing `fuse/invalidated`), the concurrency test (simultaneous `fuse/graph` and `fuse/scope`), and the
     per-RID host-publish CI matrix.
2. Phase 2: the thin read-only extension (`package.json` manifest, supervisor with spawn/health/restart and
   version handshake, status bar, index-status tree, token-hotspot tree), plus the extension test harness
   (`@vscode/test-electron`) and the TS-side contract test that parses the same fixtures as the .NET one.
3. Phases 3-6 as in the playbook: context diagnostics, the Cytoscape graph webview with directory-level
   detail, scoping commands and the scope/explain panels, then packaging (NativeAOT vs self-contained per RID,
   offline install, VSIX size recorded in DECISIONS.md).

## Blockers

None hit this session. The TS toolchain (npm, esbuild, `@vscode/test-electron`) is a Node dependency that
Phase 2 will introduce under `ext/vscode`; no `BLOCKED.md` was needed.

## Notes

- The whole effort is the size the playbook anticipates ("overnight run", six phases). This session delivered
  the contract-gating foundation (the playbook's recommended first artifact) end to end with tests; every later
  UI surface is a typed projection over the RPC methods extended from here.
