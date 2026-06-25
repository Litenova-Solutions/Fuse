// TypeScript mirror of the .NET host RPC DTOs (src/Host/Fuse.Cli/Host/Rpc/FuseHostDtos.cs), serialized through
// the source-generated FuseHostJsonContext with camelCase names. These shapes are the wire contract: the .NET
// FuseHostContractTests pin the JSON, and the extension's own contract test (added with the Phase 2 scaffolding)
// parses the same fixtures, so any drift on either side fails a test. Keep this file in lockstep with the DTOs.

/** Wire protocol version; must equal FuseHostService.ProtocolVersion on the host. */
export const PROTOCOL_VERSION = 1;

/** Result of `fuse/handshake`: host package version and the wire protocol version to match. */
export interface FuseHostHandshake {
  hostVersion: string;
  protocolVersion: number;
}

/** Result of `fuse/stats`: cheap process-level health for the status bar and index panel. */
export interface FuseHostStats {
  hostVersion: string;
  processId: number;
  uptimeMs: number;
  workingSetBytes: number;
}

/** A dependency-graph node projected for the webview. `role` is present only when a scope is active. */
export interface GraphNodeDto {
  path: string;
  declaredTypes: string[];
  centrality: number;
  tokenCost: number;
  role?: string;
}

/** A directed dependency-graph edge. */
export interface GraphEdgeDto {
  from: string;
  to: string;
  weight: number;
  kind: string;
}

/** Result of `fuse/graph`: the dependency graph at the requested level of detail. */
export interface GraphDto {
  nodes: GraphNodeDto[];
  edges: GraphEdgeDto[];
  detail: "Files" | "Directories";
}

/** Result of `fuse/index`: the warm-index state after collecting and building the graph. */
export interface IndexResultDto {
  indexState: "Warm" | "Indexing" | "NotIndexed";
  fileCount: number;
  elapsedMs: number;
}

/** One emitted file in a scope result: the included path and its token cost. */
export interface ScopeFileDto {
  path: string;
  tokenCost: number;
}

/** Result of `fuse/scope`: the files a scoped fusion included, the total tokens, and the payload file path. */
export interface ScopeResultDto {
  mode: string;
  files: ScopeFileDto[];
  totalTokens: number;
  payloadPath?: string;
}

/** One detected secret with a zero-based editor range, for an in-place diagnostic. */
export interface SecretDiagnosticDto {
  path: string;
  kind: string;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
}

/** Result of `fuse/diagnostics`: context diagnostics for the repository (secrets, with hotspots/gaps to come). */
export interface DiagnosticsDto {
  secrets: SecretDiagnosticDto[];
}

/** One planned file in an explain result: why it was included (role), at what fidelity (tier), and its score. */
export interface ExplainFileDto {
  path: string;
  role: string;
  tier: string;
  score: number;
}

/** Result of `fuse/explain`: the scoped result's context plan, computed without writing a payload. */
export interface ExplainResultDto {
  mode: string;
  files: ExplainFileDto[];
}

/** RPC method names exposed by the host (the `fuse/` namespace). */
export const Methods = {
  handshake: "fuse/handshake",
  stats: "fuse/stats",
  index: "fuse/index",
  graph: "fuse/graph",
  scope: "fuse/scope",
  diagnostics: "fuse/diagnostics",
  explain: "fuse/explain",
  shutdown: "fuse/shutdown",
} as const;
