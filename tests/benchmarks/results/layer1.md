# Layer 1 results (intrinsic)

| Repo | Size | Tool/Mode | Tokens | Reduction | Wall ms | Peak MB | Types | Methods | Routes | Literals |
|------|------|-----------|-------:|----------:|--------:|--------:|------:|--------:|-------:|---------:|
| MediatR | small | fuse/none | 77135 | 7.1% | 510 | 108.8 | 100% | 100% | 100% (0/0) | 100% |
| MediatR | small | fuse/standard | 64835 | 21.9% | 482 | 108.9 | 100% | 100% | 100% (0/0) | 100% |
| MediatR | small | fuse/aggressive | 61282 | 26.2% | 461 | 112.5 | 100% | 100% | 100% (0/0) | 100% |
| MediatR | small | fuse/skeleton | 26405 | 68.2% | 455 | 108.6 | 100% | 84% | 100% (0/0) | n/a |
| MediatR | small | fuse/publicapi | 23083 | 72.2% | 471 | 109.0 | 100% | 84% | 100% (0/0) | n/a |
| MediatR | small | repomix/full | 86328 | -3.9% | 1893 | n/a | 100% | 100% | 100% (0/0) | n/a |
| FluentValidation | small-mid | fuse/none | 242458 | 7.0% | 522 | 112.2 | 100% | 100% | 100% (0/0) | 100% |
| FluentValidation | small-mid | fuse/standard | 179608 | 31.1% | 506 | 116.0 | 100% | 100% | 100% (0/0) | 100% |
| FluentValidation | small-mid | fuse/aggressive | 166740 | 36.0% | 526 | 116.6 | 100% | 100% | 100% (0/0) | 100% |
| FluentValidation | small-mid | fuse/skeleton | 51890 | 80.1% | 453 | 110.2 | 98% | 99% | 100% (0/0) | n/a |
| FluentValidation | small-mid | fuse/publicapi | 44609 | 82.9% | 456 | 109.3 | 98% | 98% | 100% (0/0) | n/a |
| FluentValidation | small-mid | repomix/full | 264895 | -1.6% | 1468 | n/a | 100% | 100% | 100% (0/0) | n/a |
| AutoMapper | mid | fuse/none | 420320 | 9.8% | 615 | 121.3 | 99% | 100% | 100% (0/0) | 99% |
| AutoMapper | mid | fuse/standard | 397621 | 14.7% | 643 | 121.4 | 99% | 100% | 100% (0/0) | 99% |
| AutoMapper | mid | fuse/aggressive | 366121 | 21.5% | 637 | 129.2 | 99% | 100% | 100% (0/0) | 97% |
| AutoMapper | mid | fuse/skeleton | 159542 | 65.8% | 555 | 117.9 | 91% | 86% | 100% (0/0) | n/a |
| AutoMapper | mid | fuse/publicapi | 136413 | 70.7% | 570 | 118.9 | 91% | 84% | 100% (0/0) | n/a |
| AutoMapper | mid | repomix/full | 475896 | -2.1% | 1645 | n/a | 99% | 100% | 100% (0/0) | n/a |
| NewtonsoftJson | large | fuse/none | 1337544 | 8.9% | 992 | 158.7 | 100% | 100% | 100% (0/0) | 80% |
| NewtonsoftJson | large | fuse/standard | 957958 | 34.8% | 882 | 158.4 | 100% | 100% | 100% (0/0) | 80% |
| NewtonsoftJson | large | fuse/aggressive | 873992 | 40.5% | 903 | 168.4 | 100% | 99% | 100% (0/0) | 79% |
| NewtonsoftJson | large | fuse/skeleton | 96953 | 93.4% | 633 | 141.7 | 71% | 4% | 100% (0/0) | n/a |
| NewtonsoftJson | large | fuse/publicapi | 86989 | 94.1% | 583 | 146.1 | 71% | 4% | 100% (0/0) | n/a |
| NewtonsoftJson | large | repomix/full | 1486596 | -1.3% | 1662 | n/a | 100% | 100% | 100% (0/0) | n/a |
| SampleShop | micro (in-repo) | fuse/none | 552 | -12.7% | 432 | 94.4 | 100% | 100% | 100% (4/4) | 100% |
| SampleShop | micro (in-repo) | fuse/standard | 497 | -1.4% | 445 | 94.3 | 100% | 100% | 100% (4/4) | 100% |
| SampleShop | micro (in-repo) | fuse/aggressive | 480 | 2.0% | 473 | 95.1 | 100% | 100% | 100% (4/4) | 100% |
| SampleShop | micro (in-repo) | fuse/skeleton | 449 | 8.4% | 446 | 94.1 | 100% | 100% | 0% (0/4) | n/a |
| SampleShop | micro (in-repo) | fuse/publicapi | 413 | 15.7% | 458 | 94.4 | 100% | 100% | 0% (0/4) | n/a |
| SampleShop | micro (in-repo) | repomix/full | 990 | -102.0% | 1478 | n/a | 100% | 100% | 100% (4/4) | n/a |