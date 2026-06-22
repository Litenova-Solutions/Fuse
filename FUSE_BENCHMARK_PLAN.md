# Fuse Benchmark Plan: token and round-trip reduction, measured honestly

A self-contained implementation plan for a fresh Claude Code session. Read it top to bottom before
editing. It was validated against the harness on branch `feature/v2-roadmap`; every file path below was
confirmed against current source. Plain ASCII only, no em or en dashes (a stop hook rejects them). Measured
numbers are exact and sourced; never fabricate a number, and label any bound or model as such.

## What we are trying to represent

Fuse makes two claims. This plan makes both measurable, reproducible by any user from one command, and
benchmarked against the two honest comparators: **no Fuse** (raw source the agent would otherwise read) and a
**generic packer** (Repomix). It is explicitly NOT a comparison against previous Fuse versions.

1. **Fuse reduces the input tokens** needed to give an agent the context for a task.
2. **Fuse reduces the round-trips** an agent makes to assemble that context.

### The honest framing (read this before designing anything)

The two claims are two axes of one quantity: the cost to acquire the context a task needs. Put the three
approaches on those axes and the real story is unambiguous, and it does not overclaim:

- **No Fuse (blind exploration):** the agent discovers and reads files one at a time. Many round-trips; tokens
  grow with what it reads.
- **Generic packer (Repomix):** one dump. Round-trips collapse to 1, but the input is the whole repository.
  It trades round-trips for tokens.
- **Fuse, scoped (`--query` / `--focus` / `--changed-since`):** one call. Round-trips collapse to 1 (it ties
  Repomix here, it does not beat it), AND the payload is the scoped, reduced set the task needs, far smaller
  than the whole repo.

So the two defensible headline sentences are:

- Versus no Fuse: one Fuse call replaces N explore round-trips (N bounded below by the number of files the
  task needs).
- Versus a generic packer at the same one call: Fuse delivers the needed context in far fewer input tokens
  (scoped and reduced, not the whole repo), while still including the files the task touches.

Do not write "Fuse beats Repomix on round-trips" (it ties at one call). Do not write "Fuse reduces tokens X
percent" as the headline without saying it still includes the needed files (recall). The fair claim couples
tokens with recall: Fuse reaches recall R of the task's files in one call at T tokens, where the generic
packer needs many times T tokens for the same single call, and blind exploration needs at least K round-trips.

## What already exists (do not rebuild)

The harness lives in `tests/benchmarks/harness/`, dot-sources `common.ps1`, and writes to
`tests/benchmarks/results/`. One command runs everything: `pwsh -File tests/benchmarks/harness/run-all.ps1`
(optionally `-Compare results/baseline.layer1.json` to gate layer 1 against the committed baseline).

- `common.ps1`: paths and helpers. `Get-Corpus` (reads `corpus.json`), `Resolve-RepoPath`, `Get-Tokens`
  (exact `o200k_base` via the `tokencount` tool), `Measure-Process` (wall-clock + peak memory),
  `Get-CsFiles` (excludes bin/obj, `*.g.cs`, `*.Designer.cs`), `New-CsMirror` (C#-only mirror so raw, Fuse,
  and Repomix see one file set), `Get-BodyIntegrity`, `Compare-Results`.
- `setup-corpus.ps1`: clones the pinned corpus (MediatR, FluentValidation, AutoMapper, NewtonsoftJson) into
  `tests/benchmarks/.corpus/` at fixed commits. The corpus is already present in this checkout.
- `gen-prs.ps1`: writes `tests/benchmarks/prs.json`, the ground truth: real merged PRs per corpus repo that
  change 2 to 25 `.cs` files, each with `{ repo, pr, base, head, title, changed_cs[] }`. The `changed_cs`
  array is the set of files the task needs. This is the basis for the new scenario layer.
- `layer1.ps1`: intrinsic per-repo token reduction and fidelity. Arms: `raw` (no Fuse, full concatenation),
  the Fuse levels (`none`, `standard`, `aggressive`, `skeleton`, `publicapi`), and a `repomix` arm
  (`npx --yes repomix ... --include '**/*.cs' --style xml`). Writes `results/layer1.{json,csv,md}`. This is
  the whole-repo token-reduction comparison and it already includes raw and Repomix.
- `layer2a.ps1`: per-PR scoping recall and precision at budgets 10000/25000/50000, modes `changes`, `focus`,
  `query`, and a `grep` agent-native baseline. It already records, per PR per mode per budget, the emitted
  `tokens` and `recall`/`precision` against `changed_cs`. Writes `results/layer2a.{json,md}`.
- `layer2b.ps1`: change-scoping recall over the 24 PRs (the published 88 percent / 61 percent headline).
- `layer3.ps1`: an ILLUSTRATIVE round-trip model (naive/guided/ask), grounded in real Fuse token counts but
  explicitly not a measured agent. This is the piece to replace (see Task B).
- `tools/`: `TokenCount`, `Fidelity`, `BodyIntegrity` (built by `run-all.ps1`).

Claims are made in these files (every one must end up consistent with the new numbers):
`AGENTS.md` (Measured Results, lines around 43 to 48), `CLAUDE.md` (imports AGENTS.md),
`README.md`, `site/content/docs/project/benchmarks.mdx`, `site/content/docs/project/performance.mdx`,
`site/content/docs/start/what-is-fuse.mdx`, `site/content/docs/start/why-fuse.mdx`,
`site/content/docs/concepts/scoping.mdx`, and the `site/content/docs/scenarios/*.mdx` pages
(`ask-one-question`, `context-for-an-agent`, `scope-a-pr`, `survey-cheaply`, `stay-under-a-budget`).

## Environment (this machine) - read before you start

- Build, test, format gates (also in AGENTS.md): `dotnet build Fuse.slnx -c Release`,
  `dotnet test Fuse.slnx -c Release --no-build`, `dotnet format Fuse.slnx --verify-no-changes`. Build first.
- Native AOT publish works locally once `vswhere.exe` is reachable. Prepend
  `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH, then from PowerShell:
  `dotnet publish src/Host/Fuse.Cli/Fuse.Cli.csproj -c Release /p:PublishProfile=aot-win-x64`. This plan does
  not touch the AOT path, but keep it green.
- The pinned corpus is already cloned under `tests/benchmarks/.corpus/`. `run-all.ps1` and the layer scripts
  run offline EXCEPT the Repomix arm.
- Repomix needs network and `npx`. In a restricted environment `npx --yes repomix` emits a ~380-token stub
  instead of a real dump. When that happens the current harness silently records the broken row. Fixing that
  is Task A. When you genuinely cannot fetch Repomix, do not commit a stub row; carry the prior committed
  Repomix rows, exactly as the layer 1 refresh does today.
- The benchmark CLI is the framework-dependent Release build at
  `src/Host/Fuse.Cli/bin/Release/net10.0/fuse.exe` (rebuilt by `run-all.ps1`). Member-level query retrieval
  and reduction-aware packing are already in this build (delivered in 2.2 and 3.1).

---

## Task A: make the generic-packer comparison reliable

The whole story leans on a valid Repomix row. Today `layer1.ps1` shells out to `npx --yes repomix` and, on
failure or a stub, silently records a broken row (tokens near 380, fidelity 0).

1. Detect an unusable Repomix result: `npx` missing, the call throwing, or an output whose token count is
   below a small sanity floor (for example under 1000 tokens for any real repo, or fidelity types ratio 0 on
   a repo with known types). On detection, do NOT write the row. Print a clear one-line notice
   (`repomix unavailable (no network or npx); carrying the prior committed row`) and leave the committed
   `layer1.json` Repomix row in place, mirroring how the layer-1 refresh is handled now.
2. Document the prerequisite in the harness header and in `run-all.ps1`: a fresh run needs `dotnet`, `git`,
   network, and `npx` (Node) for the Repomix arm. Without `npx`, every Fuse-versus-raw number is still valid;
   only the Fuse-versus-generic-packer comparison is carried from the committed baseline.
3. Verify on a machine with network that a fresh `layer1.ps1` produces a real Repomix row (tens of thousands
   of tokens, not 380). If you cannot get network here, leave Task A's detection-and-carry logic in and note
   in the PR that the live Repomix refresh must be run in an environment with `npx`.

Acceptance: a fresh run either produces a real Repomix row or clearly states it is carrying the committed one;
it never silently publishes a 380-token stub.

---

## Task B: the scenario layer (the real deliverable)

Add a per-task context-acquisition benchmark that puts no-fuse, Repomix, and Fuse on the two axes. Build it on
`prs.json` (real PR change sets), so it reuses grounded ground truth and is reproducible.

Create `tests/benchmarks/harness/layer4-scenario.ps1` (and wire it into `run-all.ps1` after `layer2a.ps1`).
For each PR task in `prs.json`, reconstruct the head state in a git worktree (copy the worktree pattern from
`layer2a.ps1`), then measure three arms at the headline budget (50000, plus the 10000/25000 points for the
curve):

- **no-fuse (relevant set):** the files the task needs (`changed_cs`). `input_tokens` = exact tokens of those
  files read in full (use `Get-Tokens` / the `tokencount` tool over the set). `round_trips` = the count of
  files in `changed_cs` (a structural LOWER BOUND: a blind agent must read each needed file at least once,
  and in practice reads more while exploring). `recall` = 1.0 by construction. Also record a second figure,
  `no_fuse_whole_repo_tokens` = exact tokens of every `.cs` file in the repo, as the "explore blind" ceiling
  (what an agent pays if it cannot find the right files).
- **repomix:** one dump of the repo (reuse the `layer1.ps1` invocation). `input_tokens` = exact tokens of the
  dump. `round_trips` = 1. `recall` = 1.0 by construction (it contains everything). Apply Task A's
  unavailable-detection here too; when Repomix is unavailable, omit the arm for that run rather than stub it.
- **fuse:** one scoped call. Use `--query "<PR title>"` (fall back to changed type names when the title is
  merge noise, copying the query construction in `layer2a.ps1`) with `--depth 2 --max-tokens <budget>
  --level standard`. `input_tokens` = exact tokens of the emitted output. `round_trips` = 1.
  `recall` = fraction of `changed_cs` present in the output (reuse `Get-EmittedPaths` / `Measure-Recall` from
  `layer2a.ps1`). Optionally also run a `--focus` arm and a `--changed-since` arm so the table shows the best
  scoping mode per task; `query` is the honest default because it needs no prior knowledge of the change.

Emit `results/layer4-scenario.{json,csv,md}`. The markdown table is the artifact the docs will cite. Suggested
columns, aggregated as means across the 24 PRs at the headline budget:

```
| Arm                 | Round-trips | Input tokens | Recall of needed files |
|---------------------|------------:|-------------:|-----------------------:|
| no-fuse (blind)     |         >=K |  whole-repo  |                  1.00  |
| no-fuse (rel. set)  |         >=K |  rel-set tok |                  1.00  |
| Repomix (one dump)  |           1 |  whole-repo  |                  1.00  |
| Fuse (--query)      |           1 |  fuse tokens |                  R     |
```

Report per-repo rows too (means hide the spread). Keep the JSON row-per-(repo, pr, arm, budget) so the curve
and per-repo views are reconstructable, matching `layer2a.json`.

### Honesty rules for Task B (do not violate)

- `round_trips` for no-fuse is a LOWER BOUND derived from ground truth, not a simulated or measured agent.
  Label it that way in the JSON (`round_trips_is_lower_bound = true`), the markdown, and every doc claim.
- Tokens are exact (`o200k_base`); state the tokenizer.
- Always pair Fuse's token number with its recall in the same sentence. A low token count that dropped needed
  files is not a win.
- Repomix and no-fuse have recall 1.0 by construction (they include everything / the whole relevant set);
  say so, so the comparison is not mistaken for a recall contest. The contest is tokens at one call, and
  round-trips versus blind exploration.

---

## Task C: replace the illustrative layer 3, fold in the bound

`layer3.ps1` is a hand-built round-trip model. Replace its round-trip claim with the ground-truth-bounded
number from Task B (no-fuse round-trips bounded by the relevant-file count from real PRs; Fuse and Repomix at
one call). Keep a clearly labeled "live-agent traces are out of scope for the keyless harness" note, exactly
as `benchmarks.mdx` already says. The goal is to stop presenting a formula and start presenting a bound from
real data. If layer 3's prefill-cost-grows-quadratically illustration is still wanted, keep it but subordinate
it under the measured bound and keep the illustrative label.

---

## Task D: update every claim site

Make the prose match the new numbers. Lead with the two-axis story. Keep plain ASCII, outcome-first on user
pages, dense on reference pages, and the AGENTS.md honesty rules (label bounds and models; never present a
model as a benchmark).

- `AGENTS.md` Measured Results: add the scenario-layer numbers (Fuse versus no-fuse versus Repomix: tokens at
  one call, recall, and the no-fuse round-trip lower bound). Keep "agent wall-clock and live round-trip
  traces are not benchmarked" but replace the implication that round-trips are unmeasured with the bounded
  result. Update `CLAUDE.md` only if it restates numbers (it imports AGENTS.md, so usually no change).
- `README.md`: a short headline using the coupled claim (one call instead of N round-trips; far fewer tokens
  than a generic packer at that one call, while still including the task's files). Point to the benchmarks
  page and the one-command reproduction.
- `site/content/docs/project/benchmarks.mdx`: add a "Context acquisition (Fuse vs no Fuse vs Repomix)"
  section with the scenario table and the reproduction command; revise the layer-3 round-trip section to the
  bounded result.
- `site/content/docs/start/what-is-fuse.mdx` and `why-fuse.mdx`: align the round-trip and token claims to the
  measured/bounded numbers.
- `site/content/docs/scenarios/*.mdx` (`ask-one-question`, `context-for-an-agent`, `survey-cheaply`,
  `stay-under-a-budget`, `scope-a-pr`): where a page asserts a token or round-trip benefit, cite the scenario
  number rather than an unsourced figure.
- `site/content/docs/project/performance.mdx`: if it carries round-trip prose, reconcile it.
- Optional: regenerate the figure in `assets/` (there is a chart-generating script there) to include the
  two-axis scenario chart, and reference it from `benchmarks.mdx`.

---

## Reproducibility (the user-facing promise)

A user must be able to reproduce every published number with one command and inspect the outputs.

- `pwsh -File tests/benchmarks/harness/run-all.ps1` builds the CLI and tools, clones the corpus, regenerates
  `prs.json`, and runs layers 1, 2A, 2B, the new scenario layer, and layer 3. Results land in
  `tests/benchmarks/results/`.
- Document prerequisites at the top of `run-all.ps1` and in `benchmarks.mdx`: `dotnet` SDK, `git`, network for
  the corpus clone and the Repomix arm, and `npx` (Node) for Repomix. State what still works without `npx`
  (everything except the Fuse-versus-generic-packer row).
- Commit the refreshed `results/*.json|csv|md`. If the Repomix arm was unavailable, carry the prior committed
  Repomix rows and say so in the PR description; do not commit stub rows.

---

## Delivery order

1. Task A (Repomix reliability) - smallest, unblocks the generic-packer comparison.
2. Task B (scenario layer) - the real deliverable; produces the numbers.
3. Task C (replace illustrative layer 3 with the bound).
4. Task D (update all claim sites and the figure).

Land each as its own commit with build, test, and format green. Do not merge, self-approve, or enable
auto-merge; leave merges to reviewers (the work is on `feature/v2-roadmap`, draft PR #12, unless a new branch
is requested).

## Acceptance

- A fresh `run-all.ps1` produces `results/layer4-scenario.{json,csv,md}` with, per task, the three arms and
  the two metrics, and either a real Repomix row or an explicit carried-row notice.
- The scenario markdown shows Fuse at one call delivering the needed context in far fewer tokens than the
  generic packer at one call, with its recall stated, and the no-fuse round-trip lower bound versus one call.
- Every claim site cites the scenario numbers; no unsourced or model-as-benchmark token or round-trip claim
  remains. Round-trip numbers for no-fuse are labeled lower bounds everywhere.
- `dotnet build`, `dotnet test --no-build`, `dotnet format --verify-no-changes`, and the AOT publish are
  green. The layer-1 Fuse arms still match `baseline.layer1.json` (the new work does not change the default
  path).

## Honesty constraints (carry verbatim into the work)

- Plain ASCII; no em dash (U+2014) or en dash (U+2013) in any prose. A stop hook enforces this.
- Measured numbers are exact and sourced from `tests/benchmarks/results`. Never fabricate or weaken a number.
- A round-trip lower bound is a bound, not a measurement; a live-agent trace is illustrative. Label both.
- Couple every Fuse token claim with recall. Do not claim Fuse beats Repomix on round-trips (it ties at one
  call); the Repomix win is on tokens at that one call.
- Do not commit a broken Repomix stub; carry the prior committed rows when the arm is unavailable.
