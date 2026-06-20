# Layer 1 results (intrinsic)

| Repo | Size | Tool/Mode | Tokens | Reduction | Wall ms | Peak MB | Types | Methods | Routes |
|------|------|-----------|-------:|----------:|--------:|--------:|------:|--------:|-------:|
| MediatR | small | fuse/default | 77135 | 7.1% | 463 | 108 | 100% | 100% | 100% (0/0) |
| MediatR | small | fuse/all | 61282 | 26.2% | 459 | 113.6 | 100% | 100% | 100% (0/0) |
| MediatR | small | fuse/skeleton | 26405 | 68.2% | 442 | 108.8 | 100% | 84% | 100% (0/0) |
| MediatR | small | repomix/full | 86328 | -3.9% | 1893 | n/a | 100% | 100% | 100% (0/0) |
| FluentValidation | small-mid | fuse/default | 242458 | 7.0% | 499 | 111.1 | 100% | 100% | 100% (0/0) |
| FluentValidation | small-mid | fuse/all | 166740 | 36.0% | 505 | 115.7 | 100% | 100% | 100% (0/0) |
| FluentValidation | small-mid | fuse/skeleton | 51890 | 80.1% | 507 | 108.4 | 98% | 99% | 100% (0/0) |
| FluentValidation | small-mid | repomix/full | 264895 | -1.6% | 1468 | n/a | 100% | 100% | 100% (0/0) |
| AutoMapper | mid | fuse/default | 420320 | 9.8% | 587 | 116.6 | 99% | 100% | 100% (0/0) |
| AutoMapper | mid | fuse/all | 366121 | 21.5% | 632 | 131.3 | 99% | 100% | 100% (0/0) |
| AutoMapper | mid | fuse/skeleton | 159542 | 65.8% | 516 | 116.8 | 91% | 86% | 100% (0/0) |
| AutoMapper | mid | repomix/full | 475896 | -2.1% | 1645 | n/a | 99% | 100% | 100% (0/0) |
| NewtonsoftJson | large | fuse/default | 1337554 | 8.9% | 992 | 147 | 100% | 100% | 100% (0/0) |
| NewtonsoftJson | large | fuse/all | 879747 | 40.1% | 866 | 164.7 | 100% | 99% | 100% (0/0) |
| NewtonsoftJson | large | fuse/skeleton | 96953 | 93.4% | 516 | 128.6 | 71% | 4% | 100% (0/0) |
| NewtonsoftJson | large | repomix/full | 1486596 | -1.3% | 1662 | n/a | 100% | 100% | 100% (0/0) |
| SampleShop | micro (in-repo) | fuse/default | 552 | -12.7% | 411 | 93 | 100% | 100% | 100% (4/4) |
| SampleShop | micro (in-repo) | fuse/all | 480 | 2.0% | 416 | 93.6 | 100% | 100% | 100% (4/4) |
| SampleShop | micro (in-repo) | fuse/skeleton | 449 | 8.4% | 379 | 92.9 | 100% | 100% | 0% (0/4) |
| SampleShop | micro (in-repo) | repomix/full | 990 | -102.0% | 1478 | n/a | 100% | 100% | 100% (4/4) |
