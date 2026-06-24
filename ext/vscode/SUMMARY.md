# Fuse VS Code extension: session summary

## Completed this session (committed, gate green)

Phase 1 foundation, the `fuse host` JSON-RPC endpoint:

- `fuse host` command serving the warm engine over a named pipe (Windows) or Unix domain socket (elsewhere),
  sharing the same `AddFuse` DI graph as `fuse mcp serve`. Address derived from the repository root; accept loop
  serves multiple connections until `fuse/shutdown`.
- Wire contract: source-generated `FuseHostJsonContext` over the RPC DTOs (handshake, stats, graph, index),
  mirrored by `src/host/protocol.ts`, pinned by `FuseHostContractTests`.
- Lifecycle methods `fuse/handshake`, `fuse/stats`, `fuse/shutdown`, verified end to end by
  `FuseHostServiceRpcTests` over an in-memory duplex pipe.
- `FuseHostConnection` centralizes the StreamJsonRpc setup so the command and tests share one wire config.

Gate at the commit: `dotnet build Fuse.slnx -c Release` 0 warnings, all tests pass (Fuse.Cli.Tests +5 host
tests), `dotnet format --verify-no-changes` clean. Engine behavior and every benchmark number unchanged.

## Remaining (recommended next-session order)

1. Finish Phase 1: wire the engine-data methods (`fuse/index`, `fuse/graph`, `fuse/scope`, `fuse/explain`,
   `fuse/diagnostics`) to the `FusionOrchestrator` and `DependencyGraphBuilder` (DTOs and protocol entries are
   already in place); add the warm-index lifecycle (pooled repo-root store, resident index, watcher
   invalidation pushing `fuse/invalidated`); add the concurrency test (simultaneous `fuse/graph` and
   `fuse/scope`); stand up the per-RID host-publish CI matrix.
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
