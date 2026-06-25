# Layer 1 results (intrinsic)

| Repo | Size | Tool/Mode | Tokens | Reduction | Wall ms | Peak MB | Types | Methods | Routes | Literals |
|------|------|-----------|-------:|----------:|--------:|--------:|------:|--------:|-------:|---------:|
| MediatR | small | fuse/none | 77135 | 7.1% | 487 | 109.2 | 100% | 100% | 100% (0/0) | 100% |
| MediatR | small | fuse/standard | 64835 | 21.9% | 506 | 110.3 | 100% | 100% | 100% (0/0) | 100% |
| MediatR | small | fuse/aggressive | 61282 | 26.2% | 514 | 110.1 | 100% | 100% | 100% (0/0) | 100% |
| MediatR | small | fuse/skeleton | 39865 | 52.0% | 639 | 127.8 | 100% | 100% | 100% (0/0) | n/a |
| MediatR | small | fuse/publicapi | 34680 | 58.2% | 612 | 129.4 | 100% | 100% | 100% (0/0) | n/a |
| MediatR | small | repomix/full | 86308 | -3.9% | 1685 | n/a | 100% | 100% | 100% (0/0) | n/a |
| FluentValidation | small-mid | fuse/none | 242458 | 7.0% | 573 | 116.7 | 100% | 100% | 100% (0/0) | 100% |
| FluentValidation | small-mid | fuse/standard | 179608 | 31.1% | 546 | 111.8 | 100% | 100% | 100% (0/0) | 100% |
| FluentValidation | small-mid | fuse/aggressive | 166740 | 36.0% | 546 | 116.5 | 100% | 100% | 100% (0/0) | 100% |
| FluentValidation | small-mid | fuse/skeleton | 115635 | 55.6% | 737 | 139.5 | 100% | 100% | 100% (0/0) | n/a |
| FluentValidation | small-mid | fuse/publicapi | 104133 | 60.1% | 701 | 138.3 | 99% | 100% | 100% (0/0) | n/a |
| FluentValidation | small-mid | repomix/full | 264875 | -1.6% | 1405 | n/a | 100% | 100% | 100% (0/0) | n/a |
| AutoMapper | mid | fuse/none | 420320 | 9.8% | 4805 | 120.9 | 99% | 100% | 100% (0/0) | 99% |
| AutoMapper | mid | fuse/standard | 397621 | 14.7% | 630 | 121.2 | 99% | 100% | 100% (0/0) | 99% |
| AutoMapper | mid | fuse/aggressive | 366121 | 21.5% | 650 | 129 | 99% | 100% | 100% (0/0) | 97% |
| AutoMapper | mid | fuse/skeleton | 258427 | 44.6% | 1039 | 158.2 | 99% | 100% | 100% (0/0) | n/a |
| AutoMapper | mid | fuse/publicapi | 223606 | 52.0% | 985 | 153.3 | 99% | 100% | 100% (0/0) | n/a |
| AutoMapper | mid | repomix/full | 475876 | -2.1% | 6419 | n/a | 99% | 100% | 100% (0/0) | n/a |
| NewtonsoftJson | large | fuse/none | 1337544 | 8.9% | 1019 | 166.9 | 100% | 100% | 100% (0/0) | 80% |
| NewtonsoftJson | large | fuse/standard | 957958 | 34.7% | 883 | 166.3 | 100% | 100% | 100% (0/0) | 80% |
| NewtonsoftJson | large | fuse/aggressive | 873992 | 40.5% | 878 | 175.4 | 100% | 99% | 100% (0/0) | 79% |
| NewtonsoftJson | large | fuse/skeleton | 694862 | 52.7% | 1426 | 199.4 | 100% | 100% | 100% (0/0) | n/a |
| NewtonsoftJson | large | fuse/publicapi | 618517 | 57.9% | 1353 | 191.8 | 100% | 100% | 100% (0/0) | n/a |
| NewtonsoftJson | large | repomix/full | 1486576 | -1.3% | 1757 | n/a | 100% | 100% | 100% (0/0) | n/a |
| Serilog | small-mid | fuse/none | 184078 | 9.0% | 524 | 116.1 | 99% | 100% | 100% (0/0) | 99% |
| Serilog | small-mid | fuse/standard | 120213 | 40.6% | 507 | 112.8 | 99% | 100% | 100% (0/0) | 99% |
| Serilog | small-mid | fuse/aggressive | 109238 | 46.0% | 556 | 117.4 | 99% | 100% | 100% (0/0) | 99% |
| Serilog | small-mid | fuse/skeleton | 123985 | 38.7% | 729 | 139.1 | 99% | 100% | 100% (0/0) | n/a |
| Serilog | small-mid | fuse/publicapi | 104393 | 48.4% | 706 | 141.1 | 99% | 100% | 100% (0/0) | n/a |
| Serilog | small-mid | repomix/full | 206378 | -2.1% | 1492 | n/a | 99% | 100% | 100% (0/0) | n/a |
| SampleShop | micro (in-repo) | fuse/none | 552 | -12.7% | 421 | 95.1 | 100% | 100% | 100% (4/4) | 100% |
| SampleShop | micro (in-repo) | fuse/standard | 497 | -1.4% | 447 | 95.9 | 100% | 100% | 100% (4/4) | 100% |
| SampleShop | micro (in-repo) | fuse/aggressive | 480 | 2.0% | 445 | 95.2 | 100% | 100% | 100% (4/4) | 100% |
| SampleShop | micro (in-repo) | fuse/skeleton | 532 | -8.6% | 483 | 108.2 | 100% | 100% | 100% (4/4) | n/a |
| SampleShop | micro (in-repo) | fuse/publicapi | 502 | -2.5% | 523 | 108 | 100% | 100% | 100% (4/4) | n/a |
| SampleShop | micro (in-repo) | repomix/full | 970 | -98.0% | 1375 | n/a | 100% | 100% | 100% (4/4) | n/a |
