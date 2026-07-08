// TypeScript mirror of the .NET host RPC DTOs (src/Host/Fuse.Cli/Host/Rpc/FuseHostDtos.cs), serialized through
// the source-generated FuseHostJsonContext with camelCase names. These shapes are the wire contract: the .NET
// FuseHostContractTests pin the JSON, and the extension's own contract test (added with the Phase 2 scaffolding)
// parses the same fixtures, so any drift on either side fails a test. Keep this file in lockstep with the DTOs.

/** Wire protocol version; must equal FuseHostService.ProtocolVersion on the host. */
export const PROTOCOL_VERSION = 5;

/** Result of `fuse/handshake`: host package version, the wire protocol version to match, and the session token for later RPC calls. */
export interface FuseHostHandshake {
  hostVersion: string;
  protocolVersion: number;
  sessionToken: string;
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

/** The indexed file count for one language, for the index panel. */
export interface LanguageCountDto {
  language: string;
  count: number;
}

/** Result of `fuse/index`: the semantic index summary the panel shows after building or refreshing the index. */
export interface IndexResultDto {
  indexState: "Warm" | "Indexing" | "NotIndexed";
  fileCount: number;
  elapsedMs: number;
  /** The index tier: `semantic` (full typed graph), `partial`, or `syntax`. */
  mode: string;
  symbolCount: number;
  routeCount: number;
  schemaVersion: number;
  fullTextSearch: boolean;
  fuseVersion: string;
  languages: LanguageCountDto[];
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

/** One token hotspot: a budget-heavy file and its estimated token cost. */
export interface HotspotDiagnosticDto {
  path: string;
  tokenCost: number;
}

/** Result of `fuse/diagnostics`: secrets (with ranges), token hotspots, graph gaps, and generated files. */
export interface DiagnosticsDto {
  secrets: SecretDiagnosticDto[];
  hotspots: HotspotDiagnosticDto[];
  graphGaps: string[];
  generated: string[];
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

/** One compiler diagnostic in a `fuse/check` delta (S3 ambient verification). */
export interface CheckDiagnosticDto {
  id: string;
  severity: string;
  message: string;
  path?: string;
  line: number;
}

/** Result of `fuse/check`: the diagnostics a session's edits introduced or resolved since its baseline. When no resident workspace serves the root, `resident` is false and both lists are empty. */
export interface CheckDeltaDto {
  resident: boolean;
  introduced: CheckDiagnosticDto[];
  resolved: CheckDiagnosticDto[];
}

/** One session the store knows for a root (G3): its id, when it was last written, and what data it carries. */
export interface SessionSummaryDto {
  sessionId: string;
  updatedUtc: string;
  hasBaseline: boolean;
  hasClaims: boolean;
}

/** Result of `fuse/sessions`: the sessions the store knows for a root, most recently written first. */
export interface SessionListDto {
  sessions: SessionSummaryDto[];
}

/** Result of `fuse/session-view` (G3): the read-only observability view of one session - the diagnostics its edits introduced or resolved since its baseline (empty when no resident workspace serves the root) and its rendered graded claim ledger. */
export interface SessionViewDto {
  sessionId: string;
  resident: boolean;
  introduced: CheckDiagnosticDto[];
  resolved: CheckDiagnosticDto[];
  claims: string;
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
  check: "fuse/check",
  sessions: "fuse/sessions",
  sessionView: "fuse/session-view",
  shutdown: "fuse/shutdown",
} as const;

/** Server-to-client notification: the workspace changed and the extension should refresh. */
export const Notifications = {
  invalidated: "fuse/invalidated",
} as const;
