// Pure, vscode-free shaping of the host session DTOs into the observability panel's tree nodes (G3). Kept as a
// dependency-free CommonJS module so it is imported by the CommonJS extension bundle (via require) AND run
// headlessly under `node --test` (via ESM interop) against the shared fixtures, the same way the RPC contract
// test does. The TreeDataProvider in sessions.ts wraps these nodes into vscode.TreeItems.

"use strict";

/**
 * Builds the top-level session rows from a fuse/sessions result.
 * @param {{sessions: Array<{sessionId: string, updatedUtc: string, hasBaseline: boolean, hasClaims: boolean}>}} list
 */
function buildSessionRows(list) {
  return (list.sessions || []).map((s) => {
    const carries = [s.hasBaseline ? "diagnostics" : null, s.hasClaims ? "claims" : null].filter(Boolean);
    return {
      kind: "session",
      sessionId: s.sessionId,
      label: s.sessionId,
      description: carries.length > 0 ? carries.join(" + ") : "no data",
    };
  });
}

/**
 * Builds the child rows under one session from a fuse/session-view result: an introduced-diagnostics group, a
 * resolved-diagnostics count, and a claims block. Read-only - no promote/discard actions (those need F1).
 * @param {{sessionId: string, resident: boolean, introduced: Array<{id: string, severity: string, message: string, path?: string, line: number}>, resolved: Array<object>, claims: string}} view
 */
function buildSessionChildren(view) {
  const rows = [];

  if (!view.resident) {
    rows.push({ kind: "info", label: "no resident workspace (diagnostics unavailable; start with FUSE_RESIDENT=1)" });
  } else if (view.introduced.length === 0 && view.resolved.length === 0) {
    rows.push({ kind: "info", label: "no diagnostics introduced or resolved since the baseline" });
  }

  for (const d of view.introduced) {
    rows.push({
      kind: "diagnostic",
      label: `${d.severity} ${d.id}`,
      description: `${d.path || ""}:${d.line} ${d.message}`,
      path: d.path,
      line: d.line,
    });
  }

  if (view.resident && view.resolved.length > 0) {
    rows.push({ kind: "info", label: `${view.resolved.length} diagnostic(s) resolved since the baseline` });
  }

  // A git-free "files touched" summary (G3b, a lightweight stand-in for the staged-diff view): the distinct files
  // the session's introduced and resolved diagnostics land in, computed from the panel's existing data - no git
  // spawn, no extra RPC. It answers "which files did this session's edits affect" without a working-tree diff.
  const touched = distinctFiles([...view.introduced, ...view.resolved]);
  if (touched.length > 0) {
    rows.push({ kind: "info", label: `files touched (${touched.length}): ${touched.join(", ")}` });
  }

  const claims = (view.claims || "").trim();
  if (claims.length > 0) {
    rows.push({ kind: "claims", label: "claim ledger", tooltip: claims });
  }

  return rows;
}

/**
 * The distinct, sorted file paths a set of diagnostics land in (skipping any without a path).
 * @param {Array<{path?: string}>} diagnostics
 * @returns {string[]}
 */
function distinctFiles(diagnostics) {
  const seen = new Set();
  for (const d of diagnostics) {
    if (d.path) {
      seen.add(d.path);
    }
  }
  return [...seen].sort();
}

// The static root node for the workspace working-tree diff (G3b): a sibling of the session rows, since the working
// tree is one tree, not per session. Its children are fetched lazily (buildWorktreeChildren) when it expands.
const WORKTREE_ROW = { kind: "worktree", label: "Working tree (vs HEAD)" };

/**
 * Builds the child rows under the working-tree node from a fuse/session-diff result (G3b): one row per changed
 * file with its added/removed counts, and a handoff-preview row. Read-only.
 * @param {{available: boolean, base: string, files: Array<{path: string, added: number, removed: number}>, handoffPreview: string}} diff
 */
function buildWorktreeChildren(diff) {
  const rows = [];

  if (!diff.available) {
    rows.push({ kind: "info", label: "working-tree diff unavailable (no git, or no HEAD in this repository)" });
  } else if (diff.files.length === 0) {
    rows.push({ kind: "info", label: "no uncommitted changes (the working tree matches HEAD)" });
  }

  for (const f of diff.files) {
    rows.push({ kind: "difffile", label: f.path, description: `+${f.added} -${f.removed}`, path: f.path });
  }

  const preview = (diff.handoffPreview || "").trim();
  if (preview.length > 0) {
    rows.push({ kind: "claims", label: "handoff preview", tooltip: preview });
  }

  return rows;
}

module.exports = { buildSessionRows, buildSessionChildren, buildWorktreeChildren, WORKTREE_ROW };
