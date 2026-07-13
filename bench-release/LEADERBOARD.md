# Leaderboard (recorded runs only)

This leaderboard lists **recorded** benchmark runs only: a run appears here after its result files are committed to
the Fuse repository under `tests/benchmarks/results`. Unverified or self-reported submissions are not listed. Each
row names the run's date, the environment (machine class, SDK, model and CLI for model-driven runs), the exact
result file, and whether the run met its pre-registered minimum-N (a below-minimum run is labeled a pilot with
confidence intervals and no headline).

## corpus-health (the reproducible substrate)

| Date | Corpus | Tier-1 repos | Verified oracle tasks | Meets minimums | Result file |
|------|--------|--------------|-----------------------|----------------|-------------|
| 2026-07-11 | corpus-v2 (24 repos) | 15 / 24 | 59 (across 15 repos, each with gold test files) | No (59/60 tasks, 15/20 tier-1; reduced-scope) | `results/corpus-health.json` |

## loop (the referendum)

Metric: TRUE pass@1 from the gold-test oracle post-check, with false-done, iterations-to-green, and agent-visible
build round-trips counted apart from speculative `fuse_check` turns (D22a). Model-driven, so not byte-reproducible;
the model id and CLI version are recorded with each run.

| Date | Arena | Model / CLI | Rollouts/arm | Fuse true pass@1 | Native true pass@1 | False-done (fuse/native) | Headline | Result file |
|------|-------|-------------|--------------|------------------|--------------------|--------------------------|----------|-------------|
| 2026-07-12 | corpus-v2 (234 rollouts) | claude-sonnet-4-6 / claude 2.1.181 | 2 | 89% (75/84), CI 82-95 | 82% (66/80), CI 74-90 | 8 / 9 | No (reduced-scope, 15/20 tier-1 and 59/60 tasks, D21) | `results/loop.json` |
| 2026-07-11 | corpus-v2 (pilot) | claude-sonnet-4-6 / claude 2.1.181 | 1 | proxy only (pre-oracle) | proxy only | not evaluable | No (reduced-scope pilot, D21) | `results/loop-pilot.json` |

Gate reading for the 2026-07-12 run (two of three pre-registered gates hold on the true oracle metric):
pass@1-within-5-points HOLDS (fuse 7 points higher); false-done-at-most-native HOLDS (8 vs 9);
build-invocations-at-most-half MISSES and is now cleanly measured (fuse 3.1 vs native 3.2 agent-visible
build+test round-trips, with fuse_check separated at 0.7 vs 0.0). The Headline column is No because the corpus is
just below full minimums; a full-minimums arena (20 tier-1 repos, 60 tasks) is the remaining gate to a headline.

## Peer comparison

See `tests/benchmarks/results/layer6-peers.json` for the recorded change-impact peer comparison (Fuse, CodeGraph,
coa-codesearch, serena) with its caveats; token columns across peers are not directly comparable (some return
source, some return path or snippet lists).
