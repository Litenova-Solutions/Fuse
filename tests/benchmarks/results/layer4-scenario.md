# Layer 4 results (context acquisition: Fuse vs no Fuse vs Repomix)

Tokenizer o200k_base. Budgets 10000, 25000, 50000; headline budget 50000. PRs: 108. Fuse query depth 2.
Means across the PRs at the headline budget. no-fuse round-trips is a structural lower bound (a blind agent reads each needed file at least once, and more while exploring), not a measured agent. Repomix is the cost-of-not-scoping baseline: a generic full-dump packer that never scopes, so its recall is 1.00 by construction and its role is to show what blind packing costs in tokens, not to contest scoping. no-fuse and Repomix both have recall 1.00 by construction.

| Arm | Round-trips | Input tokens | Recall of needed files |
|-----|------------:|-------------:|-----------------------:|
| no-fuse (blind, whole repo) | >= 6.8 | 348,147 | 1.00 |
| no-fuse (relevant set) | >= 6.8 | 22,511 | 1.00 |
| Repomix (one dump) | 1 | 363,039 | 1.00 |
| Fuse (--query) | 1 | 31,978 | 49% |

## Per repo (headline budget)

| Repo | no-fuse K (>=) | no-fuse rel tok | whole-repo tok | Repomix tok | Fuse tok | Fuse recall |
|------|---------------:|----------------:|---------------:|------------:|---------:|------------:|
| AutoMapper | 6.3 | 12,706 | 445,473 | 462,521 | 37,748 | 35% |
| eShopOnWeb | 5.7 | 2,373 | 47,349 | 59,975 | 8,434 | 47% |
| FluentValidation | 4.7 | 10,003 | 243,044 | 255,104 | 34,361 | 42% |
| MediatR | 5.5 | 7,113 | 75,013 | 80,033 | 30,338 | 83% |
| NewtonsoftJson | 11.9 | 86,590 | 1,108,834 | 1,145,234 | 39,678 | 30% |
| Serilog | 6.9 | 16,280 | 169,169 | 175,367 | 41,307 | 55% |

## Routed arms (headline budget)

The change-scoped arm is the routed default when a git base is available; the ask arm is what Fuse picks from the task text; the query arm is the stress floor (a sentence, no base, picked search). All are one call.

| Arm | Recall | Mean tokens |
|-----|-------:|------------:|
| fuse --changed-since (routed) | 91% | 25,805 |
| fuse ask (routed) | 49% | 32,369 |
| fuse --query (stress floor) | 49% | 31,978 |

Tokens to reach 80% recall (smallest budget whose mean recall clears it):

| Arm | Budget reached | Mean tokens there |
|-----|---------------:|------------------:|
| fuse --changed-since | 25000 | 15,053 |
| fuse ask | not reached at <= 50000 | n/a |
| fuse --query | not reached at <= 50000 | n/a |
