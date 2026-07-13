# .NET Agent Task Benchmark

This directory is the in-repo home of the public Fuse benchmark (B3). The benchmark lives in the monorepo, not a
separate repository (Decision D23): the harness, manifests, results, license posture, and adjudication protocol all
live under `tests/benchmarks/`, so the benchmark and the code it measures never drift, and reproduction needs only
the published `fuse` tool plus the in-repo manifest. Its public face is a reproduction page on fuse.codes; this
README and `LEADERBOARD.md` are its source. Nothing here redistributes any corpus repository's source: it publishes
the manifest, the extraction method, the harness invocation, and the health gate, so any team can run the same
yardstick on a clean machine.

## What the benchmark measures

A fail-to-pass, test-oracle task set mined from real merged changes in public .NET repositories. Each task is a
commit whose diff changes both non-test source and test files; the task is kept only when its changed tests FAIL
at the base commit and PASS at the merge commit (verified mechanically, not assumed). This turns a real merged
change into a task an agent can be scored green against with a compiler-and-test oracle, not file-overlap.

Two published measurements:

- **corpus-health**: for each repository, whether it reaches the semantic (tier-1) load and how many fail-to-pass
  oracle tasks it yields. This is the reproducible row an outsider can regenerate (the Validation below).
- **loop**: the referendum. An agent resolves each task in two arms (native filesystem tools; the Fuse MCP
  tools), scored on true pass@1 from the gold-test oracle post-check, false-done, iterations-to-green, and
  agent-visible build round-trips counted apart from speculative checks.

## The corpus manifest

`corpus-v2.json` (canonical copy: `tests/benchmarks/corpus-v2.json`) pins 24 public .NET repositories, each by
name, clone URL, and commit SHA. Per-repository licenses and the redistribution posture are in
`corpus-licenses.md` (canonical: `tests/benchmarks/corpus-licenses.md`). Two repositories (AutoMapper, MediatR)
are under a source-available license, not OSI; a reproducer clones them under those terms or excludes them.

## Reproduce a corpus-health row on a clean machine

Prerequisites: the .NET SDK bands the corpus needs (see `tests/benchmarks/corpus-v2.json` `$note`), `git`, and
disk for a NuGet cache and worktrees.

1. Build the Fuse global tool (or install the published `fuse` tool):

   ```
   dotnet build Fuse.slnx -c Release
   ```

2. Provision the corpus repositories at their pinned commits under a working directory (one clone per manifest
   entry, checked out at its `commit`):

   ```
   # for each {name, url, commit} in corpus-v2.json:
   git clone <url> <corpus>/<name> && git -C <corpus>/<name> checkout <commit>
   ```

3. Run the health suite, pointing at the manifest and the provisioned corpus. Use a warm NuGet cache directory to
   keep restores offline where possible:

   ```
   NUGET_PACKAGES=<cache> fuse eval corpus-health \
     --manifest corpus-v2.json --corpus <corpus> --restore \
     --verify-tasks 5 --repo-timeout 20
   ```

4. Compare `results/corpus-health.json` (the tier classification and verified-task counts) and
   `results/corpus-tasks-v2.json` (the persisted fail-to-pass tasks, each with its base/merge commits, test
   filter, and changed-test files) against the published row for a repository. A single-repository run
   (`--repo <name>`) reproduces just that row.

The health gate refuses a model-driven run unless a fresh `corpus-health.json` meets the minimum-N thresholds, so a
published loop run always rests on a passing (or explicitly reduced-scope) health artifact.

## The benchmark's parts (all in this monorepo)

The public benchmark is these in-repo files (references and scripts, never corpus source); the docs page is built
from them, and no second repository is created:

- `bench-release/README.md` (this file) and `bench-release/LEADERBOARD.md` - the reproduction guide and the
  recorded-runs leaderboard, the source of the public reproduction page.
- `tests/benchmarks/corpus-v2.json` - the pinned manifest (name, URL, commit per repository).
- `tests/benchmarks/corpus-licenses.md` - the per-repository license posture and the two-repo non-OSI caveat.
- `tests/benchmarks/adjudication-protocol.md` - the edge-adjudication method for WiringBench.
- `tests/benchmarks/results/*.json` - the recorded result files the leaderboard and the docs cite.
- The eval driver is the published `fuse` global tool (`fuse eval corpus-health|review|localize|ranking`), so a
  reproducer installs the tool rather than building the C# harness. Pin the tool version used.

## What is published, and what is not

Published: the manifest (references, not code), the extraction and verification method
(`tests/benchmarks/adjudication-protocol.md` and the corpus-health methodology), the harness invocation above, the
health gate, the license posture, and the leaderboard of recorded runs (`LEADERBOARD.md`). Never published: any
corpus repository's source. A reproducer clones each repository itself under that repository's own license.
