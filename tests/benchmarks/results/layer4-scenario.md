# Layer 4 results (context acquisition: Fuse vs no Fuse vs Repomix)

Tokenizer o200k_base. Budgets 10000, 25000, 50000; headline budget 50000. PRs: 90. Fuse query depth 2.
Means across the PRs at the headline budget. no-fuse round-trips is a structural lower bound (a blind agent reads each needed file at least once, and more while exploring), not a measured agent. no-fuse and Repomix have recall 1.00 by construction.

| Arm | Round-trips | Input tokens | Recall of needed files |
|-----|------------:|-------------:|-----------------------:|
| no-fuse (blind, whole repo) | >= 6.9 | 409,154 | 1.00 |
| no-fuse (relevant set) | >= 6.9 | 25,809 | 1.00 |
| Repomix (one dump) | 1 | 424,511 | 1.00 |
| Fuse (--query) | 1 | 36,990 | 49% |

## Per repo (headline budget)

| Repo | no-fuse K (>=) | no-fuse rel tok | whole-repo tok | Repomix tok | Fuse tok | Fuse recall |
|------|---------------:|----------------:|---------------:|------------:|---------:|------------:|
| AutoMapper | 5.5 | 9,060 | 449,709 | 466,816 | 39,266 | 35% |
| FluentValidation | 4.7 | 10,003 | 243,044 | 255,104 | 34,361 | 42% |
| MediatR | 5.5 | 7,113 | 75,013 | 80,033 | 30,338 | 83% |
| NewtonsoftJson | 11.9 | 86,590 | 1,108,834 | 1,145,234 | 39,678 | 30% |
| Serilog | 6.9 | 16,280 | 169,169 | 175,367 | 41,307 | 55% |

## Routed arms (headline budget)

The change-scoped arm is the routed default when a git base is available; the ask arm is what Fuse picks from the task text; the query arm is the stress floor (a sentence, no base, picked search). All are one call.

| Arm | Recall | Mean tokens |
|-----|-------:|------------:|
| fuse --changed-since (routed) | 91% | 29,539 |
| fuse ask (routed) | 50% | 37,512 |
| fuse --query (stress floor) | 49% | 36,990 |

Tokens to reach 80% recall (smallest budget whose mean recall clears it):

| Arm | Budget reached | Mean tokens there |
|-----|---------------:|------------------:|
| fuse --changed-since | 25000 | 16,957 |
| fuse ask | not reached at <= 50000 | n/a |
| fuse --query | not reached at <= 50000 | n/a |
