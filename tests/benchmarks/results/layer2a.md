# Layer 2A results (scoping recall@budget)

Budgets: 10000, 25000, 50000. Headline budget: 50000. Focus/query depth: 2. PRs: 24.

| Mode | Budget | Mean recall | Mean precision | Mean tokens | N |
|------|-------:|------------:|---------------:|------------:|--:|
| changes | 10000 | 77% | 78% | 13768 | 24 |
| changes | 25000 | 81% | 68% | 20436 | 24 |
| changes | 50000 | 88% | 61% | 34605 | 24 |
| focus | 10000 | 41% | 9% | 7831 | 24 |
| focus | 25000 | 44% | 8% | 18979 | 24 |
| focus | 50000 | 47% | 7% | 34101 | 24 |
| grep | 50000 | 38% | 11% | 41452 | 24 |
| query | 10000 | 37% | 7% | 9917 | 24 |
| query | 25000 | 45% | 3% | 24075 | 24 |
| query | 50000 | 49% | 3% | 44197 | 24 |
