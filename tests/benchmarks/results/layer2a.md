# Layer 2A results (scoping recall@budget)

Budgets: 10000, 25000, 50000. Headline budget: 50000. Focus/query depth: 2. PRs: 24.

Wasted tokens (B8) is the proportional estimate of budget spent on emitted files outside the truth set.
Cost-adjusted recall (B11) is mean recall times mean precision.

| Mode | Budget | Mean recall | Mean precision | Mean tokens | Wasted tokens | Recall x precision | N |
|------|-------:|------------:|---------------:|------------:|--------------:|-------------------:|--:|
| changes | 10000 | 76% | 79% | 8889 | 1617 | 60% | 24 |
| changes | 25000 | 79% | 60% | 14811 | 6541 | 48% | 24 |
| changes | 50000 | 87% | 50% | 29607 | 15006 | 44% | 24 |
| focus | 10000 | 72% | 6% | 8682 | 8256 | 5% | 24 |
| focus | 25000 | 88% | 6% | 21843 | 20918 | 5% | 24 |
| focus | 50000 | 92% | 6% | 40527 | 38857 | 5% | 24 |
| grep | 50000 | 38% | 11% | 41452 | 38024 | 4% | 24 |
| query | 10000 | 46% | 16% | 8679 | 7289 | 7% | 24 |
| query | 25000 | 54% | 10% | 20003 | 18473 | 5% | 24 |
| query | 50000 | 61% | 7% | 39189 | 36329 | 5% | 24 |

## Recall by change-set size (headline budget 50000)

| Mode | small (1-3) | medium (4-9) | large (10+) |
|------|-----:|-----:|-----:|
| changes | 97% (n=15) | 100% (n=6) | 12% (n=3) |
| focus | 97% (n=15) | 94% (n=6) | 60% (n=3) |
| query | 71% (n=15) | 61% (n=6) | 10% (n=3) |
| grep | 48% (n=15) | 29% (n=6) | 7% (n=3) |

## Recall by held-out split (headline budget 50000)

Split by PR-id parity (fixed). Tune on dev; publish test.

| Mode | dev | test |
|------|-----:|-----:|
| changes | 88% (n=14) | 86% (n=10) |
| focus | 91% (n=14) | 93% (n=10) |
| query | 70% (n=14) | 48% (n=10) |
| grep | 43% (n=14) | 30% (n=10) |

## Per repo (headline budget 50000)

| Repo | changes | focus | query | grep |
|------|-----:|-----:|-----:|-----:|
| AutoMapper | 92% | 92% | 50% | 29% |
| FluentValidation | 100% | 100% | 57% | 23% |
| MediatR | 100% | 100% | 94% | 94% |
| NewtonsoftJson | 56% | 74% | 42% | 5% |
