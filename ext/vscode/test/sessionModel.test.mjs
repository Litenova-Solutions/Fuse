// G3 fixture-driven panel data test: the pure session-model shaping (src/views/sessionModel.mjs) turns the host
// DTOs into the observability panel's tree nodes. Runs headless under `node --test` against the shared fixtures,
// the same wire shapes the contract test pins, so the panel's data mapping is verified without Electron.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildSessionRows, buildSessionChildren } from "../src/views/sessionModel.js";

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
