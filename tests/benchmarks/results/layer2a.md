# Layer 2A results (scoping recall@budget)

Budgets: 10000, 25000, 50000. Headline budget: 50000. Focus/query depth: 2. PRs: 24.

| Mode | Budget | Mean recall | Mean precision | Mean tokens | N |
|------|-------:|------------:|---------------:|------------:|--:|
| changes | 10000 | 76% | 79% | 8889 | 24 |
| changes | 25000 | 79% | 60% | 14811 | 24 |
| changes | 50000 | 87% | 50% | 29607 | 24 |
| focus | 10000 | 50% | 11% | 9643 | 24 |
| focus | 25000 | 55% | 6% | 23229 | 24 |
| focus | 50000 | 71% | 5% | 46543 | 24 |
| grep | 50000 | 38% | 11% | 41452 | 24 |
| query | 10000 | 34% | 7% | 9900 | 24 |
| query | 25000 | 43% | 3% | 23871 | 24 |
| query | 50000 | 51% | 2% | 46366 | 24 |

## Per repo (headline budget 50000)

| Repo | changes | focus | query | grep |
|------|-----:|-----:|-----:|-----:|
| AutoMapper | 92% | 88% | 29% | 29% |
| FluentValidation | 100% | 74% | 51% | 23% |
| MediatR | 100% | 100% | 94% | 94% |
| NewtonsoftJson | 56% | 21% | 30% | 5% |
