# F5 data-governance note (for maintainer review)

Status: DRAFT for maintainer review. This note is the F5 precondition: it must be reviewed and
accepted by the maintainer before any edit-outcome collection code lands. No collection code
exists yet; this document defines the contract that code must satisfy.

## What F5 is

Every speculative edit Fuse verifies produces a tuple: the workspace state, the edit, the
compiler/test verdict, the repair packet used (if any), and the outcome. Today those tuples are
computed and discarded. F5 is the opt-in, local-first flywheel that records them as a labeled
corpus (compiler-precision examples for tuning repair packets and selection, and a long-term
training asset). It is a consent and privacy feature before it is a data feature, so the
governance contract below is the product, not an afterthought.

## Principles (each is a hard requirement, not a goal)

1. **Off by default.** Nothing is recorded unless the user explicitly runs
   `fuse config flywheel on`. A fresh install records nothing. The default-off state is verified
   by a test.
2. **Local-first, no network path.** This item contains no code that transmits anything off the
   machine. Recording writes to a local store under the workspace's `.fuse/`; export writes a
   local file. There is no upload, no telemetry, no phone-home - not disabled, absent. A reviewer
   can grep the F5 code for any network client and find none.
3. **Fail-closed redaction.** Every field of every tuple passes through the secret redactor
   before it is written and again before it is exported. `fuse export-corpus` produces a file
   only when the redactor finds zero secrets across the whole export; a single planted or real
   secret kills the export with a nonzero exit and a message naming the finding. There is no
   "export anyway" flag.
4. **Consent is explicit and revocable.** `fuse config flywheel on` is the consent gesture;
   `fuse config flywheel off` stops recording; a delete command (or deleting the local store
   file, documented) removes what was recorded. The governance page tells the user exactly where
   the data lives so they can inspect or delete it directly.
5. **Human-reviewable export.** `fuse export-corpus` produces a human-readable file (not an
   opaque blob) so a user can see exactly what would leave their machine if they ever chose to
   share it manually. F5 itself never shares it.

## What is recorded (when flywheel is on)

Per verified edit, a redacted tuple:

- **State reference**: the owning project and the edited file path (repo-relative), not file
  contents. The path is redacted like any other field (a path can carry a secret, e.g. a token
  in a URL-shaped segment).
- **Edit shape**: the diagnostic-relevant shape of the change (e.g. the operator class, the
  token before/after for an API-shape repair), redacted. Not the raw source unless it survives
  redaction; when in doubt, the shape, not the text.
- **Verdict**: the compiler/test grade and diagnostic ids (e.g. `CS1061` cleared, build green).
  Diagnostic ids are safe; diagnostic messages pass through redaction (a message can quote a
  symbol or literal).
- **Packet used**: which repair packet (if any) was applied, by id/kind.
- **Outcome**: reached-green / still-red / abstained, and iteration count.

What is never recorded: secret values (redacted out, fail-closed), anything outside the
workspace, anything from a session where flywheel was off.

## Where it lives

A local SQLite table (or a local append-only file) under the workspace `.fuse/` directory,
alongside the existing index. It never leaves that directory except via an explicit
`fuse export-corpus` the user runs, and even then only to a local path the user names.

## How to turn it off and delete it

- `fuse config flywheel off` stops recording immediately.
- Deleting the local store (documented path under `.fuse/`) removes all recorded tuples; a
  `fuse export-corpus --purge` or equivalent delete verb is provided so the user need not hunt
  for the file.

## The governance docs page (to be written with the code)

When the code lands, a data-governance page (in `site/content/docs`) states, in plain language:
what is recorded, that it is off by default and local-only with no network path, where the data
lives, how to inspect/export/delete it, and the fail-closed redaction guarantee. The page is
written in the same change as the code, not after.

## Open questions for the maintainer

1. Storage: reuse the `.fuse/fuse.db` SQLite file (a new additive table) or a separate
   `.fuse/flywheel.db`? A separate file makes "delete everything F5 recorded" a single-file
   delete, which is cleaner for the delete guarantee. Recommendation: separate file.
2. Export format: JSONL of redacted tuples (machine + human readable) vs a rendered report.
   Recommendation: JSONL, since the near-term use is tuning and the long-term use is a dataset.
3. Redaction reuse: F5 must use the existing `DefaultSecretRedactor` (the same detector the
   secret diagnostics use) so there is one redaction definition, not two. Confirm.

## Sign-off

- [ ] Maintainer reviewed and accepts this governance contract.
- [ ] Storage location decided (open question 1).
- [ ] Export format decided (open question 2).

Only after sign-off does the F5 collection code land, satisfying the F5 precondition.
