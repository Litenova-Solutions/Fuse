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
- **Host distribution.** Self-contained per-RID, bundled via VS Code platform-specific extensions. Defer the
  download-on-first-run model unless VSIX size forces it; the resolved size will be recorded here at packaging.
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
