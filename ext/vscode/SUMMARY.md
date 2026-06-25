# Fuse VS Code extension: session summary

All six phases of the playbook are touched and the extension packages into an installable VSIX. Every commit
held the gate: `dotnet build Fuse.slnx -c Release` 0 warnings, full .NET suite green, `dotnet format
--verify-no-changes` clean, the B9 benchmark gate passing, and engine/benchmark numbers unchanged; on the
extension, `npm run build`, `tsc --noEmit` (both tsconfigs), and `npm run lint` green.

## Completed

- **Phase 1 (host): complete.** All eight RPC methods (handshake, stats, index, scope, graph, diagnostics,
  explain, shutdown) over a named pipe (Windows) or Unix socket (elsewhere), sharing the MCP `AddFuse` DI graph.
  17 host tests including a concurrency test. The wire contract is source-generated (`FuseHostJsonContext`),
  mirrored in `src/host/protocol.ts`, and pinned by contract tests. `fuse/explain` surfaces a read-only,
  additive `FusionResult.Plan` projection; `fuse/diagnostics` adds `ISecretRedactor.FindSecretSpans`, which
  leaves redaction output byte-identical.
- **Phase 2 (thin extension): complete.** Manifest, supervisor (spawn, backoff connect, version handshake,
  restart with capped backoff), typed `vscode-jsonrpc` client, status bar, index-status tree, token-hotspot tree.
- **Phase 3 (diagnostics): secrets done.** Secret findings as precise editor diagnostics in a dedicated
  "Fuse: context" collection, validating the C1 fix.
- **Phase 4 (graph webview): done.** Offline Cytoscape webview (esbuild inlines it, strict CSP, no CDN); nodes
  sized by centrality, colored by token cost, click to open; directory level of detail.
- **Phase 5 (scoping): commands done.** Search, Focus Here (editor and explorer menus), and Changes Since
  Branch run a scoped fusion, open the payload read-only, and fill the Scope Result panel.
- **Phase 6 (packaging): VSIX done.** `npm run package` produces `fuse-vscode-3.0.0.vsix` (229.92 KB), offline,
  host resolved from `fuse.host.path` or the `fuse` global tool on PATH (size recorded in DECISIONS.md).

Since: Phase 3 is now complete (secrets, hotspots, graph gaps, generated code, and live refresh via the
`fuse/invalidated` watcher push); Phase 4 added the scoped role overlay; Phase 5 added the token lens, hover
card, and explainer panel. A real concurrency bug the host test caught (concurrent same-mode scopes colliding
on one payload file) is fixed. Host tests: 19.

All six phases are now functionally complete. Since the lines above: Phase 4 gained directory expand-on-click;
Phase 2 gained the TS-side contract test (headless, in CI); Phase 6 gained per-RID publish profiles and CI for
all six RIDs plus extension-side bundled-host resolution (prefer `fuse.host.path`, then a bundled
`host/<rid>/fuse`, then PATH).

## Remaining (CI/release production or runtime-blocked only)

1. The release job that copies each per-RID published host into `host/<rid>/` and runs
   `vsce package --target <platform>` to emit the six platform VSIXes. The extension, profiles, and CI matrix
   are ready for it; producing and uploading the binaries is a release/CI task. The base no-host VSIX and the
   PATH fallback work today.
2. The `@vscode/test-electron` integration test: needs an Electron download and a display, so it belongs on a
   headful or specially-configured runner (quarantine if the runner cannot launch VS Code). The host RPC
   surface and the wire contract are already covered by 19 .NET host tests and the headless TS contract test.
3. Optional: a git-churn code lens (a further host method over `IGitStatsProvider`).

## Blockers

None hit. The `@vscode/test-electron` harness needs an Electron download and a display, so it is the one item
deferred to a headful runner rather than committed red here; the enforceable extension gate (build, dual
typecheck, lint, contract test) is green, and a VSIX packages.
