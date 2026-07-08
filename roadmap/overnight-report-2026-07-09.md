# Overnight autonomous run report - 2026-07-09

Program: Fuse v4.1 (the resident verified-edit runtime). Branch: `feature/v4-compiler-oracle`.
All work committed with DCO sign-off and pushed after each item. No PRs, merges, tags, version
bumps, or publishing. Tree is green and pushed at HEAD `b926431` (plus this report/plan checkpoint).
Full test suite re-certified green after U2 completion and U1b: all 16 projects pass, 0 failures
(Fuse.Cli.Tests 130, Fuse.Workspace.Tests 38, Fuse.GoldenOutput.Tests 14, Fuse.Retrieval.Tests 139).

## Session tally: U2, U1b, U3 DONE (Wave 3 complete); G3 + G3b DONE. Next: gated frontier

- **G3b DONE** (commit `b926431`): the working-tree diff and handoff-preview panel views, completing the G3
  Ships list. A new read-only `fuse/session-diff` host RPC (protocol bumped 5 to 6, lockstep with
  `protocol.ts`/client/contract tests) returns the `git diff HEAD` file set plus a `BuildHandoffAsync` preview;
  the panel gains a root-level "Working tree (vs HEAD)" node (sibling of the session rows) expanding to changed
  files (click to open) and a handoff-preview node, plus a git-free "files touched" summary under each session.
  Full .NET suite green (16 projects, Cli 133); extension typecheck/lint/build clean, `test:contract` 20 pass.
  The git spawn is not E2E-tested here (test-host crash class) but reuses the production-proven change source and
  is covered by DTO-shape + panel-shaping tests.



- **G3 DONE** (Gate PASS): the VS Code agent observability panel, in three sub-steps.
  - **sub-step 1** (`aaffeed`): host RPC read-only session observability - a store `ListSessionsAsync` enumerator
    (unions `check_sessions` + `claim_ledger`, root-filtered) and two methods, `fuse/sessions` and
    `fuse/session-view` (per-session introduced/resolved diagnostics + rendered claim ledger). Host protocol
    bumped 4 to 5 with `protocol.ts`, the client, and the DTO shapes in lockstep (change-safety invariant);
    contract suites moved together (.NET +2, extension +1, store +2).
  - **sub-step 2** (`27f961d`): the read-only Agent Sessions TreeDataProvider - sessions expand to their
    introduced diagnostics (click to open), a claim-ledger node, and info rows. Pure shaping split into a
    vscode-free CommonJS module so a fixture-driven panel data test runs headless (`test:contract` now 15 pass).
  - **sub-step 3** (`6a6ec2a`): docs - the panel + refresh command on the extension page (swept two stale
    post-K1/U1 lines there), the new methods on the host-rpc page, the CHANGELOG G3 entry.
  - Fallback invoked: the git-dependent staged-diff and handoff-preview views split to **G3b** (a gated tail
    item) because spawning git inside the long-lived host is the documented fragile path; the Gate ("panel
    renders live session data") is met by the sessions + diagnostics + claims panel.



- **U3 DONE** (Gate PASS): playbook prompts, session resources, and CLI parity, in four sub-steps.
  - **sub-step 1** (`beae6aa`): 5 playbook prompts (`FusePrompts`, `[McpServerPromptType]`, registered via
    `.WithPrompts<>`) - fix-build-error, implement-feature, review-pr, rename-symbol, add-endpoint - each anchored
    and expanding into a loop-shaped plan. Precondition confirmed: the pinned MCP library (0.8.0-preview.1) exposes
    prompt support.
  - **sub-step 2** (`9ca28ac`): three read-only resources - `fuse://status/{path}`, `fuse://diff/{path}/{session}`
    (the check-delta, read-only: never establishes a baseline), `fuse://diagnostics/{path}/{session}` - joining the
    U2 session ledger resource.
  - **sub-step 3** (`da2d083`): CLI parity - a new `fuse impact` command (blast radius + F3 package-upgrade mode)
    and `--handoff`/`--check-session` on `fuse review` (reusing the exact handoff builder). `fuse check --delta` and
    `fuse test` were already CLI-first.
  - **sub-step 4** (`c98aca1`): docs - prompts + session resources on the resources reference, a new CI recipe
    scenario page (review + test on a PR), the commands reference, and the CHANGELOG U3 entry.
  - Validation: the MCP integration test lists the prompts and resources over the wire; `fuse impact --package
    System.Text.Json --from-version 4.7.2 --to-version 8.0.0` returned the F3 break set end to end through the new
    CLI command.



- **U2 DONE** (Gate PASS): claim grades, the evidence ledger, and the PR handoff packet. This session closed the
  last three sub-steps:
  - **sub-step 9** (commit `647e3a9`): the fuse_review graded-claims block, threaded through
    `SemanticContextEmitter.Emit` -> `SemanticManifestBuilder.Build` (and the JSON `ContextJsonDto.Claims` field)
    as an optional `claimsSection`, computed in `FuseReviewAsync` (changed-file count = git-truth `verified`;
    public-API surface delta = graph-grade `partially verified`). The golden test passes null, so `v3-review.golden`
    is unchanged.
  - **sub-step 10** (commit `3504095`): a golden pin `v3-review-claims.golden` fixing the claims-block manifest
    shape (claims after the api-delta, ahead of the seeds).
  - **sub-step 11** (commit `4a97b04`): docs - a new concepts page `claim-grades.mdx` (grade table with one
    example each, the graph-grade cap, where claims appear, the session ledger, the handoff gate), the fuse_review
    reference updated (handoff/checkSession params + claims + handoff paragraphs), and the CHANGELOG U2 Added entry.
  - Gate "Golden tests green; every grade reachable in tests" -> PASS (14 golden green; ClaimLedgerTests reaches
    Verified, PartiallyVerified, Stale, Contradicted). All four claims blocks (impact/find-wiring/test/review),
    the ledger resource, the handoff + red-refusal, and the stale/contradicted transitions ship.

- **U1b DONE** (Gate PASS): `fuse_find kind=signatures` resolves referenced-assembly metadata when a resident
  workspace serves the root (commit `f546383`) - the hallucinated-package-API check. New additive seam
  `IResidentWorkspaceProvider.TryGetSignature` (default null) + `ResidentWorkspace.GetSignatures` (via
  `GetTypeByMetadataName` / `GetSymbolsWithName` over the held compilations) returning a `ResidentSignature` record;
  `FuseSignaturesAsync` tries resident-first per name and falls through to the store when no resident workspace
  serves the root or the name does not resolve. Tests: the core metadata resolution (framework
  `System.Text.StringBuilder` type + `StringBuilder.Append` overloads + a source type by simple name, guarded-skip
  when the SDK cannot binlog here) and two tool-wiring tests (resident stub renders the metadata signature; null
  provider falls back to the store). Metadata resolution needs a qualified name (documented, not a silent limit).
  U1 is now fully complete - every sub-item `[x]`.

## Gates (this session)

- `dotnet build Fuse.slnx -c Release` -> 0 errors (pre-existing XML-doc warnings only).
- `dotnet test Fuse.slnx -c Release --no-build` -> all 16 projects green, 0 failures.
- `dotnet format Fuse.slnx --verify-no-changes` -> clean (exit 0).

## Blockers / not-done (unchanged from 2026-07-08)

- **S3 [!]**: maintainer decision on the sub-100ms cold-start floor (managed .NET floor ~155-182ms; unbeatable
  without AOT/R2R, which was decommissioned). Needs a written maintainer call.
- **B1/B3/B4, C-track, F-track, G1/G3-G7**: corpus/install/model/maintainer/dependency-gated (model-driven suites
  need C4 + a fresh green corpus-health.json, pinned to claude-sonnet-5).

## Exact next action

Wave 3 (the U-track) is complete and G3 is done. Re-read the Master checklist in `roadmap/v4.1-plan.md`
top-to-bottom for the next eligible todo. The remaining eligible items are narrow and mostly gated:
- **G3b** (depends G3 `[x]`): eligible, but needs the host git-spawn fragility solved before the git-dependent
  diff/handoff views can land - a real follow-up, not a quick unit.
- **C1** `[>]`: the report-only remediation core is done; the remaining sub-steps are consent-gated installs +
  the 17-repo up-report corpus gate (install/corpus-gated).
- **G2** `[>]`: the next analyzer iteration is C4-gated (corpus-v2 frequency data).
- **B1/B3/B4, C2/C3/C4, F-track, G1/G4-G7**: corpus/install/model/maintainer/dependency-gated.
- **S3** `[!]`: maintainer decision on the sub-100ms cold-start floor.
This session also advanced **F5** as far as its maintainer gate allows: F5's dependency (U2) is met, but its
precondition is a maintainer-reviewed data-governance note before any collection code. That note is drafted at
`roadmap/f5-data-governance-note.md` (off-by-default, local-only with no network path, fail-closed redaction,
zero-findings export, disable/delete, three maintainer open questions); F5 is marked `[!]` pending the review, no
collection code landed. And the G3b "git fragility" was investigated: it is test-host-only (the production host
already spawns git for `changes` scoping), so G3b is unblocked - its real gate is a small design call (what git
base a session maps to), and it is a deliberate deferred unit, not stuck.

**Honest gated-frontier read:** with G3 and G3b done, the eligible-and-ungated frontier is exhausted for this
environment. Every remaining checklist todo is gated:
- **S3 `[!]` / F4 `[maintainer]` / F5 `[!]`**: maintainer decisions. F5's governance note is prepared and awaiting
  the maintainer review its precondition requires (`roadmap/f5-data-governance-note.md`).
- **C1 remainder**: consent-gated installs + a 17-repo corpus gate (install-nothing + corpus).
- **C2 / C3 / C4**: portable capture + corpus + possibly installs.
- **B1 (then B3), F1 / F2 / F6**: a model run gated behind C4's corpus-health, plus B1 recorded for the F-track.
- **B4, G2 next-iteration**: corpus (WiringBench / corpus-v2 frequency data).
- **G1 / G4 / G5 / G6 / G7**: gated on C2 or S3.
Next session: obtain the maintainer decisions (S3 cold-start floor; F4; F5 governance-note sign-off), or set up
`D:\fuse-work` + a corpus to unblock the C-track (C2 portable capture is the top C-track item once C1's
install-gated remainder is resolved). Tree green and pushed at every step; nothing is half-done.
