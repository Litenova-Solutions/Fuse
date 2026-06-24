# Layer 4 results (context acquisition: Fuse vs no Fuse vs Repomix)

Tokenizer o200k_base. Budgets 10000, 25000, 50000; headline budget 50000. PRs: 24. Fuse query depth 2.
Means across the PRs at the headline budget. no-fuse round-trips is a structural lower bound (a blind agent reads each needed file at least once, and more while exploring), not a measured agent. no-fuse and Repomix have recall 1.00 by construction.

| Arm | Round-trips | Input tokens | Recall of needed files |
|-----|------------:|-------------:|-----------------------:|
| no-fuse (blind, whole repo) | >= 5.8 | 493,661 | 1.00 |
| no-fuse (relevant set) | >= 5.8 | 27,473 | 1.00 |
| Repomix (one dump) | 1 | 511,574 | 1.00 |
| Fuse (--query) | 1 | 39,947 | 61% |

## Per repo (headline budget)

| Repo | no-fuse K (>=) | no-fuse rel tok | whole-repo tok | Repomix tok | Fuse tok | Fuse recall |
|------|---------------:|----------------:|---------------:|------------:|---------:|------------:|
| AutoMapper | 2.3 | 3,342 | 456,599 | 473,904 | 45,331 | 50% |
| FluentValidation | 3.5 | 5,711 | 256,538 | 268,083 | 41,118 | 57% |
| MediatR | 2.7 | 3,326 | 79,282 | 84,894 | 32,384 | 94% |
| NewtonsoftJson | 14.5 | 97,514 | 1,182,224 | 1,219,416 | 40,954 | 42% |

## Routed arms (headline budget)

The change-scoped arm is the routed default when a git base is available; the ask arm is what Fuse picks from the task text; the query arm is the stress floor (a sentence, no base, picked search). All are one call.

| Arm | Recall | Mean tokens |
|-----|-------:|------------:|
| fuse --changed-since (routed) | 88% | 26,825 |
| fuse ask (routed) | 57% | 40,506 |
| fuse --query (stress floor) | 61% | 39,947 |

Tokens to reach 80% recall (smallest budget whose mean recall clears it):

| Arm | Budget reached | Mean tokens there |
|-----|---------------:|------------------:|
| fuse --changed-since | 25000 | 14,631 |
| fuse ask | not reached at <= 50000 | n/a |
| fuse --query | not reached at <= 50000 | n/a |
