# Fuse expansion and monetization plan (post-v4)

Created 2026-07-09 by maintainer decision D19 (recorded in
[v4-plan.md](v4-plan.md) Decision records): the monetization, enterprise, and expansion
items moved out of the v4 program so v4 ships the laptop product, the proof, and the release.
This file holds what comes after. Each item carries its original body (moved from the v4
plan), an **Opening trigger**, and its role in the monetization map. The v4 plan's execution
protocol, decision records, metrics dictionary, and honesty conventions apply unchanged to any
item worked from this file.

Rule: nothing here starts before its trigger is met AND the maintainer opens it in writing
(a dated note in this file's log). Triggers are mostly market signals, not engineering
readiness; that is deliberate. A solo-maintainer project wants demand risk to sit on the
world, not on the roadmap.

## Checklist

- [ ] E1 Daemon-served supervision web UI (trigger: G5 shipped, plus warden pull or direct
      user demand)
- [ ] G1 GitHub PR bot: plan, impact, and verify comments (trigger: C2 shipped; first
      external CI adoption strengthens the case)
- [ ] G6 Monorepo scale: cone loading, LRU paging, multi-solution sessions, scoped verify
      (trigger: a real large-solution user or enterprise conversation)
- [ ] G7 Coverage-map-augmented test selection (trigger: C2 shipped and a team collecting
      coverage in CI)
- [ ] F1 Warden mode: the transaction boundary (trigger: B1 recorded, plus org-level pull
      for enforced verification)
- [ ] F4 Team cloud: hosted artifacts, warm cache, verification history [maintainer]
      (trigger: observed external adoption of C2 capture plus inbound asks)
- [ ] F5 Edit-outcome corpus: the opt-in flywheel (governance contract SIGNED 2026-07-09,
      below; trigger: maintainer opens it; no urgency)
- [ ] F6 Language two: TypeScript via tsserver (trigger: B1 recorded plus adoption feedback
      demanding TS; entry bar in the item)
- [ ] F7 Streaming diagnostics via MCP notifications (trigger: the host-support matrix shows
      at least one major harness delivering subscriptions end to end)

## The monetization map (how these compose into a business)

The open-source core stays whole; nothing in v4 is withheld. The paid surfaces this file
holds compose in adoption order:

1. **F4 team cloud** is the paid product proper: hosted capture bundles per branch and
   commit, team warm cache, verification history. Its unit of value (the capture bundle)
   ships free in v4's C2; hosting, retention, and team sharing are what money buys.
2. **G1 PR bot** is free distribution first (the evidence packet in every PR sells the
   substrate to every reviewer), with a natural pro tier later (org policies, gate
   enforcement, private runners).
3. **F1 warden** is the enterprise sell: enforced verification policy, waivers, audit. It
   converts the trust layer from a feature into governance, which is what organizations
   budget for.
4. **G6 monorepo scale** is enterprise credibility rather than a SKU: the capability that
   makes the other three purchasable by large shops.
5. **E1** is the human surface those buyers expect to see (supervision, approvals, audit).
6. **F5** is the long-horizon asset: the only verified corpus of .NET edit-outcomes in
   existence, opt-in and local-first by contract.

Prerequisites recorded from the 2026-07-09 monetization discussion, to protect the option
before it is exercised: decide copyright posture (CLA versus Apache-forever pledge) BEFORE
meaningful outside contributions arrive via G2's community on-ramp, verify the employment IP
question, and run the trademark registrability check on the "Fuse" name (crowded namespace).

---

## E1. Daemon-served supervision web UI

**Origin.** Decision D15 (the VS Code extension removal): the supervision-surface concept
(watch your agent work) survives the extension it shipped in. Recorded during the 2026-07-09
restructuring; the G3/G3b feature set is the functional spec.

**Why.** The one durable human-facing job v4 created is supervising an autonomous agent:
live sessions, introduced-versus-preexisting diagnostics, claim grades going stale or
contradicted, staged diffs, handoff state. An editor-bound panel reaches a minority of .NET
developers (Visual Studio and Rider demography) and carries a mirror-protocol tax; a local
web view served by the G5 daemon (`fuse ui` opening localhost) reaches every editor and
terminal user from one codebase, with no marketplace pipeline and no protocol mirror.

**Opening trigger.** G5 shipped, plus either warden-mode pull (F1 needs an approval and
audit surface) or direct user demand for live supervision. Not before.

**Ships (when opened).** A read-only local web view served by the daemon: sessions,
per-session introduced and resolved diagnostics, the claim ledger, the working-tree diff,
and the handoff preview (the G3/G3b feature set, re-homed); an approval surface only if F1
is open. Localhost only; zero remote network.

**Gate.** Renders live session data on a fixture; nothing listens beyond localhost.

**Kill risk.** GUI hours competing with substrate hours. Mitigation: the trigger, and the
read-only-first scope.

---

## G1. GitHub PR bot: plan, impact, and verify comments

**Moved from** v4 plan Wave 6 (D19, 2026-07-09). **Opening trigger.** C2 shipped; open
when a first real repo can run it end to end.

**Origin.** In-session analysis (distribution built from existing pieces); the product
analysis's CI gate.

**Current state.** All the pieces exist after v4 (review, handoff packet, T0/T1 verdicts,
the C2 action pattern); nothing composes them into the place reviewers look.

**Why.** A PR comment with the semantic blast radius, the API delta, and "14 covering tests
green, 3 not-runnable" is the product demonstrating itself to every reviewer on the repo,
with zero agent involvement; it is the cheapest credible distribution available.

**Expected result.** An opted-in repo's PRs carry one continuously-updated Fuse comment
rendering the handoff packet, and optionally a failing check when the gate is red; fork PRs
get comment-only treatment with no secrets exposure; a fixture dry-run transcript is
recorded.

**Size.** M. Main uncertainty: branch-protection and permissions variance across repos; the
comment-only fallback bounds it.

**Preconditions.** C2 action pattern established; U2 handoff packet shape shipped (both are
v4 deliverables).

**Ships.** A GitHub Action mode that, on pull_request, restores or rehydrates, runs `fuse
review --handoff` plus `fuse test` (selection, execution where runnable), and posts or
updates one comment; an optional failing check when the gate is red. Rate-limit and fork-PR
safety documented (no secrets to forks; comment-only mode there).

**Tests.** Harness-level: comment rendering golden test from a fixture handoff packet.

**Docs.** CI recipe page extension; marketplace listing prep [maintainer].

**Validation.** A dry run on a fixture repo PR (can be in-org); paste the rendered comment.

**Gate.** Dry-run comment renders correctly with grades and verdicts. Fallback: comment-only
(no check gate) ships first if the gate path fights branch protections.

**Kill risk.** Noisy comments erode goodwill. Mitigation: one updated comment per PR, empty
sections elided.

---

## G6. Monorepo scale: cone loading, LRU paging, multi-solution sessions, scoped verify

**Moved from** v4 plan Wave 6 (D19, 2026-07-09). **Opening trigger.** A real
large-solution user or enterprise conversation; do not build for an imagined monorepo.

**Origin.** In-session analysis (enterprise credibility); the substrate analysis
(solution-level sessions, scoped verify).

**Current state.** S1 loads whole solutions resident and records RSS at corpus scale;
nothing pages, nothing spans solutions, and a 1,000-project codebase would not fit hot.

**Why.** The monorepo answer is partial residency: the full graph and symbol store on disk,
only the dependency cone of files under edit live, paged by LRU, with verify scoped to the
affected cone. No .NET agent tool has a credible story here; the one that does wins the
enterprise conversations.

**Expected result.** On a scripted 300-project synthetic: RSS stays under the bound proposed
from S1 data and recorded before the run; delta check p95 stays under 2 s; an edit in one
cone does not stale an unrelated cone's answers and does stale a dependent one; a session can
span two solutions with a unified project graph.

**Size.** XL. Main uncertainty: cone invalidation correctness under paging; the correctness
tests are the item's center.

**Preconditions.** S1 RSS numbers on corpus repos; a synthetic large fixture generator (a
scripted 300-project solution) added to the harness for this item; C2 bundles as the load
source (paging from bundle is cheaper than paging from builds).

**Ships.** Cone loader with LRU page-out; workspace roots accepting multiple solutions with a
unified project graph; T0's scoped build using the same cone computation.

**Tests.** Cross-cone edit invalidation correctness (an edit in cone A does not stale cone
B's answers, and does stale a genuinely dependent cone); page-out and reload round-trip.

**Docs.** Scale page with the measured envelope.

**Validation.** RSS and delta-check latency on the 300-project synthetic recorded to
`performance.json`.

**Gate.** Bounded RSS on the synthetic (bound proposed from S1 data and recorded before the
run); delta check p95 under 2 s on the synthetic. Fallback: publish the measured envelope and
the honest scale ceiling.

**Kill risk.** Cone computation subtly wrong equals stale answers at scale. Mitigation: the
invalidation correctness tests are the item's center, not an afterthought.

---

## G7. Coverage-map-augmented test selection

**Moved from** v4 plan Wave 6 (D19, 2026-07-09). **Opening trigger.** C2 shipped and a
team actually collecting coverage in CI (the map has to come from somewhere real).

**Origin.** The substrate analysis (coverage-mapped test impact); deferred until the artifact
channel existed (C2).

**Current state.** Selection is static: R5 `tests` edges, DI-resolved. Sound for what the
graph sees; blind to paths reflection and runtime composition create.

**Why.** A coverage map from an instrumented CI run (test id to method set) closes part of
that blindness at zero query-time cost, riding the C2 bundle. The safety contract stays
strict: the map may add candidates, never remove the graph floor.

**Expected result.** With a map present, selection returns the union (each candidate tagged
with its source), selection safety on the mutant harness is at least as good as graph-only,
and at least one recorded mutant class is killed only via the map; the staleness caveat (the
map ages with the code; the bundle commit bounds it) appears in output.

**Size.** M. Main uncertainty: collector output normalization across frameworks.

**Preconditions.** Confirm a coverage collector (Microsoft.CodeCoverage or coverlet) produces
per-test method maps on a fixture; confirm the C2 bundle format can carry the map.

**Ships.** The CI action gains an optional coverage-collection mode; the bundle carries the
map; selection becomes the union of graph-based and coverage-based candidates, each candidate
tagged with its source; selection-safety measured on the H1/T1 mutant harness with and
without the map.

**Tests.** Map ingestion; union tagging; a mutant whose killer only coverage finds.

**Docs.** Test selection page: sources, staleness caveat.

**Validation.** Selection-safety A/B recorded to `testexec.json`.

**Gate.** Selection safety with the map at least equal to without on the mutant set, and at
least one recorded class of kill only the map finds. Fallback: ship tagged-union anyway if
neutral (more evidence, no regression), or hold behind a flag if it regresses safety
(analyze why first).

**Kill risk.** Stale maps mislead. Mitigation: the commit stamp, the staleness caveat in
output, and graph-selection as the never-removed floor.

---

## F1. Warden mode: the transaction boundary (opt-in)

**Moved from** v4 plan Wave 7 (D19, 2026-07-09). **Opening trigger.** B1 recorded, plus
org-level pull for enforced verification (a team asking for it, not the roadmap wanting it).

**Origin.** Convergent across three of four analyses ("the agent never has write access; Fuse
owns the transaction boundary"); Decision D2's deliberate inversion.

**Current state.** D2's default posture: Fuse never writes the tree; the agent applies diffs
through its own tools. Nothing enforces that a mutation passed verification before reaching
disk; enforcement is the harness's permission prompt and the human's judgment.

**Why.** For teams running agents at scale, "nothing reaches the tree that did not survive
the compiler, the repo's analyzers, and the covering tests" is a policy they want enforced,
not requested. Warden mode is that policy as software: sessions become the only mutation
path, and promote carries a contract. It is opt-in because a tool that fights the harness for
write authority before earning trust gets uninstalled; after B1, trust has a number.

**Expected result.** With `--warden` and the harness deny-config installed: a scripted agent
flow cannot write the tree except via promote; a promote with red diagnostics, failing
covering tests (unwaived), unredacted secrets in the diff, or an unacknowledged breaking API
delta is refused with the evidence packet; every promote appends an audit entry. Turning it
off is one flag.

**Size.** L. Main uncertainty: per-harness write-deny mechanics; the support matrix bounds
the claim.

**Preconditions.** B1 recorded (the referendum shapes the promote contract's default
strictness); U1/U2/T1/S4 shipped (all v4); confirm per-harness mechanisms to deny native
writes (hook-based deny or permission config) and record the support matrix per harness.

**Ships.** `fuse mcp serve --warden`: sessions become mandatory for mutation (check-with-
content, refactor, staged diffs); `fuse_workspace apply` becomes `promote` with a contract:
compile clean (oracle or build grade), repo analyzers clean (S4), covering tests green or
explicitly waived with the waiver recorded (T1), secret scan clean on the diff, API delta
acknowledged when breaking (T2). Optional contract extension: no new known-vulnerable
packages in touched projects (`dotnet list package --vulnerable` as the mechanism). An audit
log of promotes with their evidence packets (U2). Harness deny-config shipped for the
harnesses in the support matrix.

**Tests.** Scripted end-to-end: a simulated agent flow cannot reach the tree except via
promote; a red promote is refused with the packet; the waiver path records.

**Docs.** A warden page: the contract, the posture, the opt-in, the escape hatch, and the
honest positioning (enforcement for teams, not a default cage).

**Validation.** The scripted flow on a fixture; audit log excerpt pasted.

**Gate.** The flow holds (no tree writes outside promote; red refused). Adoption is the real
gate and it is not this item's to declare; record installs when telemetry exists (F5 consent
rules apply to any telemetry).

**Kill risk.** Warden fights the harness and gets uninstalled. Mitigation: opt-in, per-
harness support matrix, and D2's default posture unchanged.

---

## F4. Team cloud: hosted artifacts, warm cache, verification history [maintainer]

**Moved from** v4 plan Wave 7 (D19, 2026-07-09). **Opening trigger.** Observed external
adoption of C2 capture in at least one real CI, plus inbound asks. Explicitly parked until
then (the 2026-07-09 decision).

**Origin.** The substrate analysis (hosted service) and the trust analysis (team verification
history, org graph); scoped in adjudication to preparation-only until demand is observed.

**Current state.** C2 bundles live in each repo's own CI artifacts; nothing is hosted,
shared, or retained across a team, and no commercial surface exists.

**Why.** The program's natural paid tier: hosted capture bundles per branch and commit,
team-shared warm state, verification history (flaky-test signatures, CI-only failure
patterns), and the org-level dependency graph. The open-source tool stays whole without it;
building a service before demand is the named failure mode, so this item prepares and stops.

**Expected result.** The bundle protocol supports authenticated remote fetch (`fuse index
--from-capture <url>` with a token) against a static host; a reviewed design document covers
storage, tenancy, retention, and the privacy line (bundles contain code derivatives and are
treated as source). Nothing else is built until the maintainer says go.

**Size.** M for the preparation scope. Main uncertainty: none (the service itself is out of
scope here).

**Ships (scoped to preparation).** The bundle protocol versioned for remote fetch (auth,
integrity, range requests); `fuse index --from-capture <url>` with token auth; the design
document.

**Gate.** Protocol and design doc reviewed by the maintainer; the URL fetch path tested
against a static host. Fallback: stays a design doc; the local artifact path is complete
without it.

**Kill risk.** Building a service before demand. Mitigation: the precondition is observed
external adoption, not enthusiasm.

---

## F5. Edit-outcome corpus: the opt-in flywheel, local-first

**Moved from** v4 plan Wave 7 (D19, 2026-07-09). **Opening trigger.** Maintainer opens it;
no urgency, nothing depends on it. The precondition (a maintainer-reviewed governance
contract) is SATISFIED: signed 2026-07-09, contract below.

**Origin.** The substrate analysis's crazy idea (the verified edit-outcome stream as the
long-term asset), gated in adjudication behind consent and locality.

**Current state.** Sessions produce exactly the tuples the idea needs (state, edit, verdict,
packet used, outcome) and discard them; U2's ledger defines the record shape.

**Why.** Every speculative edit paired with its compiler and test verdict is a labeled
example at compiler precision, and no other .NET tool produces the stream. Near-term it tunes
repair packets and selection; long-term it is a training asset. It is a consent and privacy
problem before it is either, which is why this item is local-first, off by default, and
fail-closed on redaction.

**Ships.** Local-only recording (off by default; `fuse config flywheel on`); `fuse
export-corpus` with consent, the redactor over every field, a human-reviewable file, and a
zero-findings requirement. No network transmission exists in this item. Tests: off-by-
default; redaction fails closed on a planted secret; export review file shape. Docs: the
data-governance page, written in the same change as the code.

**Gate.** Zero-findings export requirement enforced; off by default verified.

**Kill risk.** Any perception of code exfiltration. Mitigation: local-only, opt-in,
fail-closed redaction, and the signed contract below.

### The signed governance contract (folded from f5-data-governance-note.md, file removed)

Hard requirements, each verified by a test where testable:

1. **Off by default.** Nothing is recorded unless the user explicitly runs `fuse config
   flywheel on`. A fresh install records nothing.
2. **Local-first, no network path.** No code in this item transmits anything off the
   machine: recording writes under the workspace `.fuse/`; export writes a local file. Not
   disabled, absent; a reviewer can grep the F5 code for a network client and find none.
3. **Fail-closed redaction.** Every field of every tuple passes the secret redactor before
   write and again before export; `fuse export-corpus` produces a file only at zero
   findings; there is no "export anyway" flag.
4. **Consent is explicit and revocable.** `flywheel on` is the consent gesture, `flywheel
   off` stops recording, a delete verb removes what was recorded, and the docs name where
   the data lives.
5. **Human-reviewable export.** The export is readable, so a user sees exactly what would
   leave the machine if they ever chose to share it manually; F5 itself never shares it.

What is recorded per verified edit (redacted): a state reference (project and repo-relative
path, not contents), the edit shape (operator class and token before/after, not raw source
unless it survives redaction), the verdict (grade and diagnostic ids; messages pass
redaction), the packet used (by id/kind), and the outcome (reached-green / still-red /
abstained, iteration count). Never recorded: secret values, anything outside the workspace,
anything from a session with flywheel off.

**Maintainer sign-off (2026-07-09): ACCEPTED**, with the three open questions decided:

- [x] Storage: a separate `.fuse/flywheel.db`, so "delete everything F5 recorded" is a
      single-file delete.
- [x] Export format: JSONL of redacted tuples (the near-term use is tuning; the long-term
      use is a dataset).
- [x] Redaction: reuse the existing `DefaultSecretRedactor`; one redaction definition, not
      two.

---

## F6. Language two: TypeScript via tsserver

**Moved from** v4 plan Wave 7 (D19, 2026-07-09). **Opening trigger.** The entry bar below,
in full; adoption feedback (not the B1 corpus, which is .NET-only by construction) is the
demand signal.

**Origin.** 4.0's multi-language deferral, kept; Decision D10 writes the entry bar down so
the question stays answered.

**Why.** The multi-language story that does not dilute is binding each language's real
compiler service. TypeScript is next in line because tsserver is a genuine language service
with project understanding. The item exists so its bar is written, not so it starts soon.

**Expected result (when opened).** The oracle interface (find, check, impact) served for TS
projects via a resident tsserver, with the same grades, the same abstention contract, and an
H1-equivalent mutation calibration for TS, gated like the .NET substrate was.

**Size.** XL when opened. Main uncertainty: everything; that is what the entry bar is for.

**Entry bar (all required before work starts).** B1 recorded; the .NET loop wins or the
program has re-planned around its miss; a tsserver spike shows speculative-content
diagnostics and project-graph queries at latencies compatible with the S1 gates; maintainer
sign-off that the team can afford a second compiler service without starving the first.

**Gate.** Same class of gates as the .NET substrate, re-derived then. Fallback: stays
closed.

**Kill risk.** Dilution. Mitigation: the entry bar is the item.

---

## F7. Streaming diagnostics via MCP notifications

**Moved from** v4 plan Wave 7 (D19, 2026-07-09). **Opening trigger.** A recorded
host-support matrix showing at least one major harness delivering
`notifications/resources/updated` (or equivalent) to the model end to end, tested with a
probe server, dated.

**Origin.** The product analysis proposed notifications as the ambient channel; adjudication
(D3) chose hooks now and made notifications the gated upgrade path.

**Current state.** Ambient truth flows through S3's hooks (a shell hop per edit). MCP defines
server-initiated notifications and resource subscriptions, but hosts do not reliably surface
them to the model mid-turn, which is why D3 chose hooks.

**Why.** When a major harness surfaces subscriptions end to end, the shell hop disappears and
session diagnostics become a subscribed resource; the hook path stays as the fallback
transport. Building this before host support exists would be building for a transport nobody
delivers.

**Expected result (when opened).** On the supporting harness, an agent subscribed to the
session diagnostics resource receives deltas without any hook configured, demonstrated by a
probe transcript; the host-support matrix page records which harnesses deliver what, dated.

**Size.** S when opened. Main uncertainty: entirely external (host support).

**Ships (when opened).** Session diagnostics as a subscribable resource; hooks remain the
fallback; the matrix published.

**Gate.** The probe transcript on the supporting harness. Fallback: stays closed; hooks are
sufficient.

**Kill risk.** Building for a transport nobody surfaces. Mitigation: the precondition is the
item.

---

## Log

(Dated maintainer notes: item openings, trigger observations, decisions.)

- 2026-07-09: file created (D19). F5 governance contract signed with the three answers
  recorded above. F4 parked pending observed C2 adoption. E1 added from D15 (the extension
  removal) as the future supervision surface.
