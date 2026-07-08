// Type declarations for the pure session-model shaping (sessionModel.js), so the TypeScript TreeDataProvider can
// import it with types while the module itself stays plain CommonJS JS runnable under node --test.
import { SessionDiffDto, SessionListDto, SessionViewDto } from "../host/protocol";

export interface SessionRow {
  kind: "session";
  sessionId: string;
  label: string;
  description: string;
}

export interface DiagnosticRow {
  kind: "diagnostic";
  label: string;
  description: string;
  path?: string;
  line: number;
}

export interface InfoRow {
  kind: "info";
  label: string;
}

export interface ClaimsRow {
  kind: "claims";
  label: string;
  tooltip: string;
}

export interface WorktreeRow {
  kind: "worktree";
  label: string;
}

export interface DiffFileRow {
  kind: "difffile";
  label: string;
  description: string;
  path: string;
}

export type SessionChildRow = DiagnosticRow | InfoRow | ClaimsRow;
export type WorktreeChildRow = DiffFileRow | InfoRow | ClaimsRow;

export const WORKTREE_ROW: WorktreeRow;

export function buildSessionRows(list: SessionListDto): SessionRow[];
export function buildSessionChildren(view: SessionViewDto): SessionChildRow[];
export function buildWorktreeChildren(diff: SessionDiffDto): WorktreeChildRow[];
