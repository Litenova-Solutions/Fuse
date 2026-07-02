// TypeScript-side contract test for the host RPC wire shapes. Runs headless with `node --test` (no Electron),
// so it can gate in CI. It loads the shared fixtures.json (whose keys the .NET FuseHostContractTests pin on the
// serializer side) and asserts each DTO carries exactly the keys protocol.ts declares. Drift on either side
// fails: the .NET test if the serializer stops emitting a key, this test if the fixture loses or gains one.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const fixtures = JSON.parse(readFileSync(join(here, "fixtures.json"), "utf8"));

function assertKeys(obj, keys, label) {
  assert.deepEqual(Object.keys(obj).sort(), [...keys].sort(), `${label} keys`);
}

test("handshake shape", () => {
  assertKeys(fixtures.handshake, ["hostVersion", "protocolVersion", "sessionToken"], "handshake");
  assert.equal(typeof fixtures.handshake.protocolVersion, "number");
  assert.equal(fixtures.handshake.protocolVersion, 3); // must match PROTOCOL_VERSION in protocol.ts
  assert.equal(typeof fixtures.handshake.sessionToken, "string");
  assert.ok(fixtures.handshake.sessionToken.length > 0);
});

test("stats shape", () => {
  assertKeys(fixtures.stats, ["hostVersion", "processId", "uptimeMs", "workingSetBytes"], "stats");
});

test("graph node and edge shapes", () => {
  assertKeys(fixtures.graph, ["nodes", "edges", "detail"], "graph");
  assertKeys(fixtures.graph.nodes[0], ["path", "declaredTypes", "centrality", "tokenCost", "role"], "graph node");
  assertKeys(fixtures.graph.edges[0], ["from", "to", "weight", "kind"], "graph edge");
  assert.ok(["Files", "Directories"].includes(fixtures.graph.detail));
});

test("index shape", () => {
  assertKeys(
    fixtures.index,
    ["indexState", "fileCount", "elapsedMs", "mode", "symbolCount", "routeCount", "schemaVersion", "fullTextSearch", "fuseVersion", "languages"],
    "index");
  assertKeys(fixtures.index.languages[0], ["language", "count"], "index language");
});

test("scope shape", () => {
  assertKeys(fixtures.scope, ["mode", "files", "totalTokens", "payloadPath"], "scope");
  assertKeys(fixtures.scope.files[0], ["path", "tokenCost"], "scope file");
});

test("diagnostics shape", () => {
  assertKeys(fixtures.diagnostics, ["secrets", "hotspots", "graphGaps", "generated"], "diagnostics");
  assertKeys(
    fixtures.diagnostics.secrets[0],
    ["path", "kind", "startLine", "startColumn", "endLine", "endColumn"],
    "secret",
  );
  assertKeys(fixtures.diagnostics.hotspots[0], ["path", "tokenCost"], "hotspot");
});

test("explain shape", () => {
  assertKeys(fixtures.explain, ["mode", "files"], "explain");
  assertKeys(fixtures.explain.files[0], ["path", "role", "tier", "score"], "explain file");
});

test("authenticated RPC methods pass sessionToken first", () => {
  const authenticated = [
    "fuse/stats",
    "fuse/index",
    "fuse/graph",
    "fuse/scope",
    "fuse/diagnostics",
    "fuse/explain",
    "fuse/shutdown",
  ];
  for (const method of authenticated) {
    assert.equal(fixtures.rpcParams[method][0], "sessionToken", `${method} first param`);
  }
  assert.deepEqual(fixtures.rpcParams["fuse/handshake"], []);
});
