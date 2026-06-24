# Layer 2A results (scoping recall@budget)

Budgets: 10000, 25000, 50000. Headline budget: 50000. Focus/query depth: 2. PRs: 24.

Wasted tokens (B8) is the proportional estimate of budget spent on emitted files outside the truth set.
Cost-adjusted recall (B11) is mean recall times mean precision.

| Mode | Budget | Mean recall | Mean precision | Mean tokens | Wasted tokens | Recall x precision | N |
|------|-------:|------------:|---------------:|------------:|--------------:|-------------------:|--:|
| changes | 10000 | 76% | 79% | 8889 | 1617 | 60% | 24 |
| changes | 25000 | 79% | 60% | 14811 | 6541 | 48% | 24 |
| changes | 50000 | 87% | 50% | 29607 | 15006 | 44% | 24 |
| focus | 10000 | 50% | 11% | 9643 | 8454 | 5% | 24 |
| focus | 25000 | 55% | 6% | 23229 | 22169 | 3% | 24 |
| focus | 50000 | 71% | 5% | 46543 | 44945 | 4% | 24 |
| grep | 50000 | 38% | 11% | 41452 | 38024 | 4% | 24 |
| query | 10000 | 34% | 7% | 9900 | 8890 | 2% | 24 |
| query | 25000 | 43% | 3% | 23871 | 23245 | 1% | 24 |
| query | 50000 | 51% | 2% | 46366 | 45391 | 1% | 24 |

## Recall by change-set size (headline budget 50000)

| Mode | small (1-3) | medium (4-9) | large (10+) |
|------|-----:|-----:|-----:|
| changes | 97% (n=15) | 100% (n=6) | 12% (n=3) |
| focus | 90% (n=15) | 51% (n=6) | 14% (n=3) |
| query | 61% (n=15) | 49% (n=6) | 5% (n=3) |
| grep | 48% (n=15) | 29% (n=6) | 7% (n=3) |

## Per repo (headline budget 50000)

| Repo | changes | focus | query | grep |
|------|-----:|-----:|-----:|-----:|
| AutoMapper | 92% | 88% | 29% | 29% |
| FluentValidation | 100% | 74% | 51% | 23% |
| MediatR | 100% | 100% | 94% | 94% |
| NewtonsoftJson | 56% | 21% | 30% | 5% |
