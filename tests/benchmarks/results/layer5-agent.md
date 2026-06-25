# Layer 5 results (agent-in-the-loop context sufficiency)

> MODEL-DEPENDENT LAYER. These numbers are NOT byte-reproducible. They depend on the model, its
> sampling, and the day. Read them as a distribution over rollouts, not a fixed measurement.

- Model (pinned): claude-haiku-4-5-20251001
- Run date: 2026-06-25
- Rollouts per (PR, arm): 2
- PRs sampled (10): AutoMapper#4634, AutoMapper#4616, FluentValidation#2158, FluentValidation#1823, MediatR#1171, MediatR#1159, NewtonsoftJson#1159, NewtonsoftJson#1158, Serilog#1452, Serilog#1472
- Arms: native, fuse
- Max turns per rollout: 25
- Tool restriction enforced per arm via --allowedTools; out-of-arm calls are denied by the CLI.
- codegraph index build (codegraph init) is setup, measured separately and excluded from the per-run tool-call and token counts.
- Recall and precision are objective (vs the PR change set). Sufficiency is a model-scored 0/1 verdict (labeled model-scored).

## Per arm (median and inter-quartile range over all rollouts)

| Arm | Tool calls (median, IQR) | Cumulative input tokens (median, IQR) | Mean recall | Mean precision | Sufficiency rate |
|-----|--------------------------|---------------------------------------|------------:|---------------:|-----------------:|
| native | 12 (6-14) | 440,085 (186,224-539,000) | 46% | 35% | 0.35 |
| fuse | 12.5 (4.8-16.8) | 324,348 (216,252-564,769) | 46% | 38% | 0.42 |

## Per repo, per arm (mean recall / mean cumulative tokens)

| Repo | Arm | Mean recall | Mean tokens | Rollouts |
|------|-----|------------:|------------:|---------:|
| AutoMapper | native | 0% | 360,595 | 4 |
| AutoMapper | fuse | 0% | 437,592 | 4 |
| FluentValidation | native | 64% | 492,596 | 4 |
| FluentValidation | fuse | 56% | 421,003 | 4 |
| MediatR | native | 79% | 483,873 | 4 |
| MediatR | fuse | 100% | 424,164 | 4 |
| NewtonsoftJson | native | 50% | 510,205 | 4 |
| NewtonsoftJson | fuse | 50% | 398,226 | 4 |
| Serilog | native | 35% | 197,530 | 4 |
| Serilog | fuse | 26% | 199,979 | 4 |

## How to read this

The contest is cost to acquire sufficient context. Read tool calls and tokens together with recall:
an arm that gathered fewer files cheaply but missed needed files is not a win. This is a small,
model-dependent sample; the distribution and per-repo rows are shown so a single arm-vs-arm point
is never mistaken for a settled result. Arms whose tool was not installed are omitted, not stubbed.
