# Fuse VS Code extension: decision log

Autonomy decision log for the extension build (see the implementation playbook). Each entry records a choice
made without pausing for input, with the reasoning, so a later session can audit or revisit it.

## Initial decisions (from the playbook autonomy defaults)

- **Transport.** JSON-RPC (StreamJsonRpc on .NET, vscode-jsonrpc on TS) over a named pipe on Windows and a
  Unix domain socket elsewhere. The pipe name is derived from a hash of the repository root, so multiple
  windows and multiple repositories each get a distinct endpoint. Reasoning: stdio is taken by the MCP
  transport and StdioGuard disables watch mode when stdio is redirected; a pipe also allows multiple UI
  clients. The host exposes this UI transport over the same AddFuse DI graph the MCP server uses.
- **Host process model.** One `fuse host` process per repository root, sharing the one `FusionOrchestrator`,
  warm analysis index, and pooled SQLite store. The extension spawns and supervises it.
- **JSON.** All host JSON goes through a source-generated `FuseHostJsonContext` (hard invariant: no reflection
  serialization). `protocol.ts` mirrors the DTOs and is pinned by a contract test.
- **Graph renderer.** Cytoscape.js bundled into the VSIX, no CDN (offline invariant).
- **Host distribution.** Resolved at first packaging: the VSIX does NOT bundle the host binary. It is
  `fuse-vscode-3.0.0.vsix` at 229.92 KB (dist/extension.js 138 KB plus the offline Cytoscape webview bundle
  dist/webview.js 998 KB compress to that). The supervisor resolves the host from `fuse.host.path` or the
  `fuse` global tool on PATH, so the extension stays tiny and offline. Bundling a self-contained host per RID
  via VS Code platform-specific extensions would add tens of MB per platform; that is deferred to a dedicated
  packaging pass (or a download-on-first-run model) rather than shipped in the base VSIX. Recorded per the
  playbook's instruction to capture the resolved size here.
- **Platform VSIX size (resolved).** `scripts/package-platform.mjs` plus the `ext-release.yml` workflow now
  produce a per-platform VSIX with the self-contained host bundled. Measured for win32-x64: the host publishes
  to about 141 MB and the resulting VSIX is 62.65 MB (the native libraries compress well). That is a large but
  acceptable per-user download for a zero-install, fully-offline tool, and only one platform's VSIX is fetched
  per user. If that size proves too high, the download-on-first-run fallback is the alternative (the extension
  already resolves a bundled host first, then PATH, so switching is an extension-side change, not a protocol
  one). The base no-host VSIX (about 230 KB) remains available for the PATH-based install.
- **NativeAOT.** Attempt for the host; fall back to self-contained trimmed if Roslyn, Microsoft.Data.Sqlite,
  or ONNX are not AOT-compatible, and record why here.
- **Deep index (compiled reference graph, ONNX reranker).** Never default warm state; explicit user-triggered
  actions with visible memory and download cost, off by default.

## Build-time decisions

- **StreamJsonRpc version.** Pinned in `Directory.Packages.props` under central package management, consistent
  with every other dependency in the repository.
- **DTO placement.** Host RPC DTOs live in `src/Host/Fuse.Cli/Host/Rpc` as public records distinct from the
  internal engine types (`ContextPlan`, `DependencyGraph`), so the wire contract is decoupled from engine
  internals and can be source-gen serialized and contract-tested independently.
