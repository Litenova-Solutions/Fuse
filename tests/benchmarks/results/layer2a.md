# Layer 2A results (scoping recall@budget)

Budgets: 10000, 25000, 50000. Headline budget: 50000. Focus/query depth: 2. PRs: 108.

Wasted tokens (B8) is the proportional estimate of budget spent on emitted files outside the truth set.
Cost-adjusted recall (B11) is mean recall times mean precision.

| Mode | Budget | Mean recall | Mean precision | Mean tokens | Wasted tokens | Recall x precision | N |
|------|-------:|------------:|---------------:|------------:|--------------:|-------------------:|--:|
| changes | 10000 | 67% | 80% | 9900 | 1491 | 54% | 107 |
| changes | 25000 | 81% | 66% | 16107 | 4845 | 54% | 107 |
| changes | 50000 | 89% | 53% | 28905 | 11811 | 47% | 107 |
| focus | 10000 | 55% | 6% | 7948 | 7577 | 3% | 107 |
| focus | 25000 | 66% | 6% | 18415 | 17675 | 4% | 107 |
| focus | 50000 | 74% | 6% | 34480 | 33084 | 4% | 107 |
| grep | 50000 | 38% | 13% | 35914 | 32202 | 5% | 107 |
| query | 10000 | 37% | 13% | 7897 | 6663 | 5% | 107 |
| query | 25000 | 43% | 8% | 17527 | 15887 | 3% | 107 |
| query | 50000 | 48% | 6% | 32142 | 29758 | 3% | 107 |

## Recall by change-set size (headline budget 50000)

| Mode | small (1-3) | medium (4-9) | large (10+) |
|------|-----:|-----:|-----:|
| changes | 98% (n=48) | 95% (n=34) | 65% (n=25) |
| focus | 86% (n=48) | 64% (n=34) | 65% (n=25) |
| query | 64% (n=48) | 45% (n=34) | 23% (n=25) |
| grep | 51% (n=48) | 39% (n=34) | 12% (n=25) |

## Recall by held-out split (headline budget 50000)

Split by PR-id parity (fixed). Tune on dev; publish test.

| Mode | dev | test |
|------|-----:|-----:|
| changes | 89% (n=53) | 89% (n=54) |
| focus | 79% (n=53) | 70% (n=54) |
| query | 51% (n=53) | 46% (n=54) |
| grep | 33% (n=53) | 42% (n=54) |

## Adversarial-case reporting (B7, query mode, headline budget 50000)

Merge-noise titles carry no task vocabulary, so they are an adversarial case for query mode. Reported
with and without them, never dropped silently.

| Set | Mean recall |
|-----|------------:|
| all PRs | 48% (n=107) |
| adversarial only (merge-noise titles) | 87% (n=8) |
| excluding adversarial | 45% (n=99) |

## Per repo (headline budget 50000)

| Repo | changes | focus | query | grep |
|------|-----:|-----:|-----:|-----:|
| AutoMapper | 97% | 70% | 35% | 24% |
| eShopOnWeb | 91% | 55% | 47% | 52% |
| FluentValidation | 100% | 83% | 42% | 25% |
| MediatR | 99% | 92% | 83% | 79% |
| NewtonsoftJson | 49% | 74% | 26% | 11% |
| Serilog | 97% | 72% | 55% | 35% |
