# Layer 2A results (scoping recall@budget)

Budgets: 10000, 25000, 50000. Headline budget: 50000. Focus/query depth: 2. PRs: 24.

Wasted tokens (B8) is the proportional estimate of budget spent on emitted files outside the truth set.
Cost-adjusted recall (B11) is mean recall times mean precision.

| Mode | Budget | Mean recall | Mean precision | Mean tokens | Wasted tokens | Recall x precision | N |
|------|-------:|------------:|---------------:|------------:|--------------:|-------------------:|--:|
| changes | 10000 | 76% | 79% | 8889 | 1617 | 60% | 24 |
| changes | 25000 | 79% | 60% | 14811 | 6541 | 48% | 24 |
| changes | 50000 | 87% | 50% | 29607 | 15006 | 44% | 24 |
| focus | 10000 | 54% | 11% | 9374 | 8283 | 6% | 24 |
| focus | 25000 | 69% | 5% | 22957 | 22194 | 4% | 24 |
| focus | 50000 | 77% | 5% | 41702 | 40215 | 4% | 24 |
| grep | 50000 | 38% | 11% | 41452 | 38024 | 4% | 24 |
| query | 10000 | 33% | 7% | 9755 | 8737 | 2% | 24 |
| query | 25000 | 45% | 3% | 23635 | 23046 | 1% | 24 |
| query | 50000 | 57% | 3% | 42386 | 41387 | 1% | 24 |

## Recall by change-set size (headline budget 50000)

| Mode | small (1-3) | medium (4-9) | large (10+) |
|------|-----:|-----:|-----:|
| changes | 97% (n=15) | 100% (n=6) | 12% (n=3) |
| focus | 93% (n=15) | 62% (n=6) | 24% (n=3) |
| query | 68% (n=15) | 56% (n=6) | 7% (n=3) |
| grep | 48% (n=15) | 29% (n=6) | 7% (n=3) |

## Recall by held-out split (headline budget 50000)

Split by PR-id parity (fixed). Tune on dev; publish test.

| Mode | dev | test |
|------|-----:|-----:|
| changes | 88% (n=14) | 86% (n=10) |
| focus | 76% (n=14) | 78% (n=10) |
| query | 62% (n=14) | 51% (n=10) |
| grep | 43% (n=14) | 30% (n=10) |

## Per repo (headline budget 50000)

| Repo | changes | focus | query | grep |
|------|-----:|-----:|-----:|-----:|
| AutoMapper | 92% | 92% | 46% | 29% |
| FluentValidation | 100% | 88% | 57% | 23% |
| MediatR | 100% | 100% | 94% | 94% |
| NewtonsoftJson | 56% | 28% | 32% | 5% |
