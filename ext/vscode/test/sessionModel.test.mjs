// G3 fixture-driven panel data test: the pure session-model shaping (src/views/sessionModel.mjs) turns the host
// DTOs into the observability panel's tree nodes. Runs headless under `node --test` against the shared fixtures,
// the same wire shapes the contract test pins, so the panel's data mapping is verified without Electron.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildSessionRows, buildSessionChildren, buildWorktreeChildren, WORKTREE_ROW } from "../src/views/sessionModel.js";

const here = dirname(fileURLToPath(import.meta.url));
const fixtures = JSON.parse(readFileSync(join(here, "fixtures.json"), "utf8"));

test("session rows summarize what each session carries", () => {
  const rows = buildSessionRows(fixtures.sessions);
  assert.equal(rows.length, 1);
  assert.equal(rows[0].kind, "session");
  assert.equal(rows[0].sessionId, "hook");
  assert.equal(rows[0].label, "hook");
  // The fixture session has a baseline but no claims.
  assert.equal(rows[0].description, "diagnostics");
});

test("an empty session list yields no rows", () => {
  assert.deepEqual(buildSessionRows({ sessions: [] }), []);
});

test("session children map introduced diagnostics and the claim ledger", () => {
  const children = buildSessionChildren(fixtures.sessionView);
  // The fixture view is resident with one introduced diagnostic, no resolved, and a non-empty claim ledger.
  const diagnostics = children.filter((c) => c.kind === "diagnostic");
  assert.equal(diagnostics.length, 1);
  assert.equal(diagnostics[0].label, "Error CS1061");
  assert.ok(diagnostics[0].description.includes("a/Order.cs"));

  const claims = children.filter((c) => c.kind === "claims");
  assert.equal(claims.length, 1);
  assert.ok(claims[0].tooltip.includes("[verified]"));

  // No "no diagnostics" info row, since there is an introduced diagnostic.
  assert.ok(!children.some((c) => c.kind === "info" && c.label.startsWith("no diagnostics")));

  // A git-free "files touched" summary lists the distinct files the diagnostics land in (G3b).
  const touched = children.find((c) => c.kind === "info" && c.label.startsWith("files touched"));
  assert.ok(touched, "expected a files-touched summary row");
  assert.ok(touched.label.includes("a/Order.cs"));
});

test("files-touched dedupes and sorts across introduced and resolved, skipping path-less diagnostics", () => {
  const children = buildSessionChildren({
    sessionId: "s",
    resident: true,
    introduced: [
      { id: "CS1", severity: "Error", message: "m", path: "b/Two.cs", line: 1 },
      { id: "CS2", severity: "Error", message: "m", path: "a/One.cs", line: 2 },
      { id: "CS3", severity: "Error", message: "m", line: 3 },
    ],
    resolved: [{ id: "CS4", severity: "Error", message: "m", path: "a/One.cs", line: 4 }],
    claims: "",
  });
  const touched = children.find((c) => c.kind === "info" && c.label.startsWith("files touched"));
  assert.ok(touched);
  // Distinct (One.cs once) and sorted (a/ before b/); the path-less CS3 is skipped.
  assert.equal(touched.label, "files touched (2): a/One.cs, b/Two.cs");
});

test("a non-resident view reports the missing resident workspace and still shows claims", () => {
  const children = buildSessionChildren({
    sessionId: "s",
    resident: false,
    introduced: [],
    resolved: [],
    claims: "claims (1, each graded and evidence-referenced):\n  [partially verified] 3 callers  (evidence: graph)",
  });
  assert.ok(children.some((c) => c.kind === "info" && c.label.includes("no resident workspace")));
  assert.ok(children.some((c) => c.kind === "claims"));
});

test("the working-tree root node maps a diff to file rows and a handoff preview (G3b)", () => {
  assert.equal(WORKTREE_ROW.kind, "worktree");

  const children = buildWorktreeChildren(fixtures.sessionDiff);
  const files = children.filter((c) => c.kind === "difffile");
  assert.equal(files.length, 1);
  assert.equal(files[0].label, "a/Order.cs");
  assert.equal(files[0].description, "+12 -3");
  assert.equal(files[0].path, "a/Order.cs");

  const preview = children.find((c) => c.kind === "claims" && c.label === "handoff preview");
  assert.ok(preview);
  assert.ok(preview.tooltip.includes("handoff:"));
});

test("an unavailable working-tree diff reports the missing git base", () => {
  const children = buildWorktreeChildren({ available: false, base: "HEAD", files: [], handoffPreview: "" });
  assert.ok(children.some((c) => c.kind === "info" && c.label.includes("unavailable")));
});

test("a clean working tree reports no uncommitted changes", () => {
  const children = buildWorktreeChildren({ available: true, base: "HEAD", files: [], handoffPreview: "" });
  assert.ok(children.some((c) => c.kind === "info" && c.label.includes("no uncommitted changes")));
});

test("a resident view with no delta and no claims reports the clean baseline", () => {
  const children = buildSessionChildren({
    sessionId: "s",
    resident: true,
    introduced: [],
    resolved: [],
    claims: "",
  });
  assert.equal(children.length, 1);
  assert.equal(children[0].kind, "info");
  assert.ok(children[0].label.includes("no diagnostics"));
});
