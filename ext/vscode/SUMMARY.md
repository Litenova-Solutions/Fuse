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

## Methods landed since: fuse/index, fuse/scope, fuse/graph

`fuse/graph` ships at both levels of detail (file nodes with PageRank centrality and an estimated token cost;
directory supernodes that fold files and aggregate edges). Six RPC methods now work end to end with 12 tests.

## Remaining (recommended next-session order)

1. Finish Phase 1. The two remaining engine methods each need a small, deliberate engine-surface addition to a
   load-bearing or internal component, so they are their own commits rather than wiring:
   - `fuse/explain`: the `ContextPlan` (ranked seeds, neighbours, scores, provenance, planned tier, token
     estimate, omitted-and-why) is `internal` and not surfaced on `FusionResult`. Expose a read-only plan
     projection from the orchestrator (a public DTO, not the internal type), then project it. No behavior change.
   - `fuse/diagnostics`: secret findings need precise spans, but `SecretRedactionResult` currently carries only
     the redacted content and per-kind counts, not per-finding ranges. Add a finding list (kind, start, end) to
     the redactor result and thread it through, with tests that the spans match the redacted output (the
     redaction component is load-bearing and a hard invariant, so this is a careful, dedicated change, not a
     tail-of-session edit). Hotspots (`TopTokenFiles`) and graph gaps (unconnected files in the graph) need no
     engine change and can land in the same method once the secret spans are in.
   Then add the warm-index lifecycle (pooled repo-root store, resident index, watcher invalidation pushing
   `fuse/invalidated`), the concurrency test (simultaneous `fuse/graph` and `fuse/scope`), and the per-RID
   host-publish CI matrix.
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
