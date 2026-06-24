# Layer 2A results (scoping recall@budget)

Budgets: 10000, 25000, 50000. Headline budget: 50000. Focus/query depth: 2. PRs: 24.

| Mode | Budget | Mean recall | Mean precision | Mean tokens | N |
|------|-------:|------------:|---------------:|------------:|--:|
| changes | 10000 | 77% | 67% | 15632 | 24 |
| changes | 25000 | 81% | 56% | 23407 | 24 |
| changes | 50000 | 88% | 47% | 40432 | 24 |
| focus | 10000 | 50% | 11% | 9643 | 24 |
| focus | 25000 | 55% | 6% | 23229 | 24 |
| focus | 50000 | 71% | 5% | 46543 | 24 |
| grep | 50000 | 38% | 11% | 41452 | 24 |
| query | 10000 | 37% | 7% | 9907 | 24 |
| query | 25000 | 45% | 3% | 23871 | 24 |
| query | 50000 | 49% | 2% | 46396 | 24 |
