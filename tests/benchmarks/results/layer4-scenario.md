# Layer 4 results (context acquisition: Fuse vs no Fuse vs Repomix)

Tokenizer o200k_base. Budgets 10000, 25000, 50000; headline budget 50000. PRs: 24. Fuse query depth 2.
Means across the PRs at the headline budget. no-fuse round-trips is a structural lower bound (a blind agent reads each needed file at least once, and more while exploring), not a measured agent. no-fuse and Repomix have recall 1.00 by construction.

| Arm | Round-trips | Input tokens | Recall of needed files |
|-----|------------:|-------------:|-----------------------:|
| no-fuse (blind, whole repo) | >= 5.8 | 493,661 | 1.00 |
| no-fuse (relevant set) | >= 5.8 | 27,473 | 1.00 |
| Repomix (one dump) | 1 | 511,574 | 1.00 |
| Fuse (--query) | 1 | 40,041 | 51% |

## Per repo (headline budget)

| Repo | no-fuse K (>=) | no-fuse rel tok | whole-repo tok | Repomix tok | Fuse tok | Fuse recall |
|------|---------------:|----------------:|---------------:|------------:|---------:|------------:|
| AutoMapper | 2.3 | 3,342 | 456,599 | 473,904 | 47,989 | 29% |
| FluentValidation | 3.5 | 5,711 | 256,538 | 268,083 | 27,713 | 49% |
| MediatR | 2.7 | 3,326 | 79,282 | 84,894 | 36,351 | 89% |
| NewtonsoftJson | 14.5 | 97,514 | 1,182,224 | 1,219,416 | 48,110 | 38% |
