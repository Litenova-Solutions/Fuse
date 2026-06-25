# Fuse VS Code extension: hard blockers

Per the playbook's overnight protocol, this records the items that a hard external dependency genuinely
prevents from completing in this environment, with what they need.

## `@vscode/test-electron` integration test

- **What it would do:** activate the extension against a fixture workspace under a real VS Code instance and
  assert the end-to-end UI behavior (status bar reaches "warm", the hotspot and pattern trees populate, a
  secret fixture produces a diagnostic at the exact range, "Focus here" opens a payload and fills the
  scope-result panel), plus the latency gate.
- **Why it is blocked here:** `@vscode/test-electron` downloads a full VS Code build (about 150 MB) and
  launches an Electron window, which needs a display session the headless runner does not provide; the playbook
  itself says to quarantine this when the runner cannot launch VS Code. It also needs the `fuse` host on PATH or
  `fuse.host.path` set, which a headful runner would arrange.
- **What is covered instead:** the host RPC surface and wire contract, which the integration test would exercise
  indirectly, are already covered by 19 .NET host tests (handshake, stats, index, scope, graph, diagnostics,
  explain, shutdown, the watcher broadcast, and a concurrency test) and the headless TypeScript contract test.
- **How to finish it:** on a headful or xvfb-backed runner, scaffold `test/suite/` (mocha) and
  `test/runTest.js` (`@vscode/test-electron`), set `fuse.host.path` to a built host, and run `npm test`.

## Benchmark-data items deferred to deliberate curation (not blocked by environment, governed by an invariant)

These are not environment blockers; they are recorded here so the accounting is in one place. Producing them
hastily would weaken the benchmark, which the hard invariant "never fabricate or weaken a benchmark number"
forbids, so they are deliberate curation rather than tail-of-session work:

- **B2 (larger corpus):** the plan specifies 80 to 150 PRs across more repositories and at least one more
  language. A fifth repository (Serilog, six git-verified PRs) is staged in
  `tests/benchmarks/corpus-candidates/serilog.json`; the harness scoring honors a reading set already. The full
  expansion plus the rebaseline of every published number (including the per-feature A/B deltas) is a deliberate
  multi-step pass.
- **Reading-set labels:** the Layer 2A harness scores recall against a per-PR `reading_set` when present; the
  labels themselves are per-PR human judgment that shifts the numbers, so they are curated with B2.
