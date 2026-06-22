# Layer 1 results (intrinsic)

| Repo | Size | Tool/Mode | Tokens | Reduction | Wall ms | Peak MB | Types | Methods | Routes | Literals |
|------|------|-----------|-------:|----------:|--------:|--------:|------:|--------:|-------:|---------:|
| MediatR | small | fuse/none | 77135 | 7.1% | 468 | 108.9 | 100% | 100% | 100% (0/0) | 100% |
| MediatR | small | fuse/standard | 64835 | 21.9% | 485 | 109.1 | 100% | 100% | 100% (0/0) | 100% |
| MediatR | small | fuse/aggressive | 61282 | 26.2% | 477 | 109.5 | 100% | 100% | 100% (0/0) | 100% |
| MediatR | small | fuse/skeleton | 26405 | 68.2% | 446 | 109.5 | 100% | 84% | 100% (0/0) | n/a |
| MediatR | small | fuse/publicapi | 23083 | 72.2% | 449 | 109.4 | 100% | 84% | 100% (0/0) | n/a |
| MediatR | small | repomix/full | 86308 | -3.9% | 1687 | n/a | 100% | 100% | 100% (0/0) | n/a |
| FluentValidation | small-mid | fuse/none | 242458 | 7.0% | 553 | 115.9 | 100% | 100% | 100% (0/0) | 100% |
| FluentValidation | small-mid | fuse/standard | 179608 | 31.1% | 520 | 116.4 | 100% | 100% | 100% (0/0) | 100% |
| FluentValidation | small-mid | fuse/aggressive | 166740 | 36.0% | 513 | 112.2 | 100% | 100% | 100% (0/0) | 100% |
| FluentValidation | small-mid | fuse/skeleton | 51890 | 80.1% | 462 | 110.1 | 98% | 99% | 100% (0/0) | n/a |
| FluentValidation | small-mid | fuse/publicapi | 44609 | 82.9% | 468 | 109.2 | 98% | 98% | 100% (0/0) | n/a |
| FluentValidation | small-mid | repomix/full | 264875 | -1.6% | 1516 | n/a | 100% | 100% | 100% (0/0) | n/a |
| AutoMapper | mid | fuse/none | 420320 | 9.8% | 616 | 119.5 | 99% | 100% | 100% (0/0) | 99% |
| AutoMapper | mid | fuse/standard | 397621 | 14.7% | 640 | 120.6 | 99% | 100% | 100% (0/0) | 99% |
| AutoMapper | mid | fuse/aggressive | 366121 | 21.5% | 628 | 136.8 | 99% | 100% | 100% (0/0) | 97% |
| AutoMapper | mid | fuse/skeleton | 159542 | 65.8% | 550 | 118.6 | 91% | 86% | 100% (0/0) | n/a |
| AutoMapper | mid | fuse/publicapi | 136413 | 70.7% | 553 | 118.1 | 91% | 84% | 100% (0/0) | n/a |
| AutoMapper | mid | repomix/full | 475876 | -2.1% | 1666 | n/a | 99% | 100% | 100% (0/0) | n/a |
| NewtonsoftJson | large | fuse/none | 1337544 | 8.9% | 1030 | 162 | 100% | 100% | 100% (0/0) | 80% |
| NewtonsoftJson | large | fuse/standard | 957958 | 34.7% | 919 | 159 | 100% | 100% | 100% (0/0) | 80% |
| NewtonsoftJson | large | fuse/aggressive | 873992 | 40.5% | 919 | 174 | 100% | 99% | 100% (0/0) | 79% |
| NewtonsoftJson | large | fuse/skeleton | 96953 | 93.4% | 576 | 137.3 | 71% | 4% | 100% (0/0) | n/a |
| NewtonsoftJson | large | fuse/publicapi | 86989 | 94.1% | 554 | 137.2 | 71% | 4% | 100% (0/0) | n/a |
| NewtonsoftJson | large | repomix/full | 1486576 | -1.3% | 1619 | n/a | 100% | 100% | 100% (0/0) | n/a |
| SampleShop | micro (in-repo) | fuse/none | 552 | -12.7% | 396 | 93.5 | 100% | 100% | 100% (4/4) | 100% |
| SampleShop | micro (in-repo) | fuse/standard | 497 | -1.4% | 415 | 94.3 | 100% | 100% | 100% (4/4) | 100% |
| SampleShop | micro (in-repo) | fuse/aggressive | 480 | 2.0% | 443 | 94.5 | 100% | 100% | 100% (4/4) | 100% |
| SampleShop | micro (in-repo) | fuse/skeleton | 449 | 8.4% | 410 | 94 | 100% | 100% | 0% (0/4) | n/a |
| SampleShop | micro (in-repo) | fuse/publicapi | 413 | 15.7% | 397 | 94.9 | 100% | 100% | 0% (0/4) | n/a |
| SampleShop | micro (in-repo) | repomix/full | 970 | -98.0% | 1550 | n/a | 100% | 100% | 100% (4/4) | n/a |
