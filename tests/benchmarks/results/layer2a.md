# Layer 2A results (scoping recall@budget)

Budgets: 10000, 25000, 50000. Headline budget: 50000. Focus/query depth: 2. PRs: 90.

Wasted tokens (B8) is the proportional estimate of budget spent on emitted files outside the truth set.
Cost-adjusted recall (B11) is mean recall times mean precision.

| Mode | Budget | Mean recall | Mean precision | Mean tokens | Wasted tokens | Recall x precision | N |
|------|-------:|------------:|---------------:|------------:|--------------:|-------------------:|--:|
| changes | 10000 | 63% | 86% | 10580 | 1128 | 55% | 89 |
| changes | 25000 | 80% | 69% | 18035 | 5229 | 55% | 89 |
| changes | 50000 | 89% | 54% | 33099 | 13829 | 48% | 89 |
| focus | 10000 | 56% | 6% | 8592 | 8233 | 3% | 89 |
| focus | 25000 | 69% | 5% | 21081 | 20288 | 4% | 89 |
| focus | 50000 | 78% | 6% | 40384 | 38810 | 4% | 89 |
| grep | 50000 | 34% | 13% | 40508 | 36317 | 4% | 89 |
| query | 10000 | 35% | 14% | 8119 | 6729 | 5% | 89 |
| query | 25000 | 42% | 9% | 19585 | 17702 | 4% | 89 |
| query | 50000 | 48% | 7% | 37238 | 34461 | 3% | 89 |

## Recall by change-set size (headline budget 50000)

| Mode | small (1-3) | medium (4-9) | large (10+) |
|------|-----:|-----:|-----:|
| changes | 97% (n=43) | 97% (n=24) | 63% (n=22) |
| focus | 88% (n=43) | 71% (n=24) | 68% (n=22) |
| query | 62% (n=43) | 47% (n=24) | 24% (n=22) |
| grep | 46% (n=43) | 34% (n=24) | 12% (n=22) |

## Recall by held-out split (headline budget 50000)

Split by PR-id parity (fixed). Tune on dev; publish test.

| Mode | dev | test |
|------|-----:|-----:|
| changes | 89% (n=47) | 89% (n=42) |
| focus | 82% (n=47) | 75% (n=42) |
| query | 53% (n=47) | 43% (n=42) |
| grep | 32% (n=47) | 37% (n=42) |

## Adversarial-case reporting (B7, query mode, headline budget 50000)

Merge-noise titles carry no task vocabulary, so they are an adversarial case for query mode. Reported
with and without them, never dropped silently.

| Set | Mean recall |
|-----|------------:|
| all PRs | 48% (n=89) |
| adversarial only (merge-noise titles) | 87% (n=8) |
| excluding adversarial | 45% (n=81) |

## Per repo (headline budget 50000)

| Repo | changes | focus | query | grep |
|------|-----:|-----:|-----:|-----:|
| AutoMapper | 97% | 71% | 35% | 21% |
| FluentValidation | 100% | 83% | 42% | 25% |
| MediatR | 99% | 92% | 83% | 79% |
| NewtonsoftJson | 49% | 74% | 26% | 11% |
| Serilog | 97% | 72% | 55% | 35% |
