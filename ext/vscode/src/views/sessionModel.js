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

module.exports = { buildSessionRows, buildSessionChildren };
