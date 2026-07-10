# Layer 5 results (agent-in-the-loop context sufficiency)

> MODEL-DEPENDENT LAYER. These numbers are NOT byte-reproducible. They depend on the model, its
> sampling, and the day. Read them as a distribution over rollouts, not a fixed measurement.

- Model (pinned, the driver): claude-sonnet-4-6
- Run date: 2026-06-26
- Rollouts per (PR, arm): 3
- PRs sampled (18): AutoMapper#4608, AutoMapper#4607, AutoMapper#4605, eShopOnWeb#949, eShopOnWeb#878, eShopOnWeb#876, FluentValidation#2158, FluentValidation#1823, FluentValidation#761, MediatR#1171, MediatR#1159, MediatR#1157, NewtonsoftJson#1159, NewtonsoftJson#1158, NewtonsoftJson#1153, Serilog#1452, Serilog#1472, Serilog#1442
- Arms: native, fuse, codegraph, serena
- Max turns per rollout: 25
- Tool restriction enforced per arm via --allowedTools; out-of-arm calls are denied by the CLI.
- codegraph index build (codegraph init) is setup, measured separately and excluded from the per-run tool-call and token counts.
- Recall and precision are objective (vs the PR change set). Sufficiency is a model-scored 0/1 verdict (labeled model-scored).

## Per arm (median and inter-quartile range over all rollouts)

| Arm | Tool calls (median, IQR) | Cumulative input tokens (median, IQR) | Mean recall | Mean precision | Sufficiency rate |
|-----|--------------------------|---------------------------------------|------------:|---------------:|-----------------:|
| native | 6 (2.2-9) | 197,034 (120,878-284,522) | 24% | 35% | 0.15 |
| fuse | 6 (4-8.8) | 195,486 (125,121-279,097) | 25% | 23% | 0.22 |
| codegraph | 4 (2-6) | 154,430 (86,621-207,155) | 18% | 25% | 0.11 |
| serena | 3 (2-6) | 116,774 (86,681-183,172) | 13% | 16% | 0.07 |

## Per repo, per arm (mean recall / mean cumulative tokens)

| Repo | Arm | Mean recall | Mean tokens | Rollouts |
|------|-----|------------:|------------:|---------:|
| AutoMapper | native | 19% | 146,123 | 9 |
| AutoMapper | fuse | 42% | 154,952 | 9 |
| AutoMapper | codegraph | 14% | 134,404 | 9 |
| AutoMapper | serena | 11% | 111,738 | 9 |
| eShopOnWeb | native | 4% | 133,001 | 9 |
| eShopOnWeb | fuse | 0% | 145,056 | 9 |
| eShopOnWeb | codegraph | 1% | 140,184 | 9 |
| eShopOnWeb | serena | 1% | 106,940 | 9 |
| FluentValidation | native | 23% | 268,002 | 9 |
| FluentValidation | fuse | 30% | 253,307 | 9 |
| FluentValidation | codegraph | 51% | 204,171 | 9 |
| FluentValidation | serena | 33% | 271,978 | 9 |
| MediatR | native | 48% | 271,442 | 9 |
| MediatR | fuse | 33% | 308,248 | 9 |
| MediatR | codegraph | 7% | 132,369 | 9 |
| MediatR | serena | 0% | 107,335 | 9 |
| NewtonsoftJson | native | 43% | 321,334 | 9 |
| NewtonsoftJson | fuse | 40% | 303,140 | 9 |
| NewtonsoftJson | codegraph | 25% | 282,147 | 9 |
| NewtonsoftJson | serena | 20% | 182,140 | 9 |
| Serilog | native | 7% | 200,259 | 9 |
| Serilog | fuse | 7% | 245,700 | 9 |
| Serilog | codegraph | 9% | 96,628 | 9 |
| Serilog | serena | 13% | 149,104 | 9 |

## How to read this

The contest is cost to acquire sufficient context. Read tool calls and tokens together with recall:
an arm that gathered fewer files cheaply but missed needed files is not a win. This is a small,
model-dependent sample; the distribution and per-repo rows are shown so a single arm-vs-arm point
is never mistaken for a settled result. Arms whose tool was not installed are omitted, not stubbed.
