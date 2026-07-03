# Layer 1 results (intrinsic)

| Repo | Size | Tool/Mode | Tokens | Reduction | Wall ms | Peak MB | Types | Methods | Routes | Literals |
|------|------|-----------|-------:|----------:|--------:|--------:|------:|--------:|-------:|---------:|
| MediatR | small | fuse/none | 77135 | 7.1% | 4448 | 109.7 | 100% | 100% | 100% (0/0) | 100% |
| MediatR | small | fuse/standard | 64835 | 21.9% | 506 | 109.3 | 100% | 100% | 100% (0/0) | 100% |
| MediatR | small | fuse/aggressive | 61282 | 26.2% | 506 | 110.4 | 100% | 100% | 100% (0/0) | 100% |
| MediatR | small | fuse/skeleton | 39865 | 52.0% | 633 | 127.8 | 100% | 100% | 100% (0/0) | n/a |
| MediatR | small | fuse/publicapi | 34680 | 58.2% | 641 | 128.7 | 100% | 100% | 100% (0/0) | n/a |
| MediatR | small | repomix/full | 86308 | -3.9% | 6876 | n/a | 100% | 100% | 100% (0/0) | n/a |
| FluentValidation | small-mid | fuse/none | 242458 | 7.0% | 578 | 120.5 | 100% | 100% | 100% (0/0) | 100% |
| FluentValidation | small-mid | fuse/standard | 179608 | 31.1% | 571 | 115.7 | 100% | 100% | 100% (0/0) | 100% |
| FluentValidation | small-mid | fuse/aggressive | 166740 | 36.0% | 547 | 116.2 | 100% | 100% | 100% (0/0) | 100% |
| FluentValidation | small-mid | fuse/skeleton | 115635 | 55.6% | 764 | 138.4 | 100% | 100% | 100% (0/0) | n/a |
| FluentValidation | small-mid | fuse/publicapi | 104133 | 60.1% | 745 | 138.3 | 99% | 100% | 100% (0/0) | n/a |
| FluentValidation | small-mid | repomix/full | 264875 | -1.6% | 1471 | n/a | 100% | 100% | 100% (0/0) | n/a |
| AutoMapper | mid | fuse/none | 420320 | 9.8% | 645 | 121.3 | 99% | 100% | 100% (0/0) | 99% |
| AutoMapper | mid | fuse/standard | 397621 | 14.7% | 668 | 125.4 | 99% | 100% | 100% (0/0) | 99% |
| AutoMapper | mid | fuse/aggressive | 366121 | 21.5% | 694 | 129.6 | 99% | 100% | 100% (0/0) | 97% |
| AutoMapper | mid | fuse/skeleton | 258427 | 44.6% | 1061 | 157.6 | 99% | 100% | 100% (0/0) | n/a |
| AutoMapper | mid | fuse/publicapi | 223606 | 52.0% | 980 | 154 | 99% | 100% | 100% (0/0) | n/a |
| AutoMapper | mid | repomix/full | 475876 | -2.1% | 1452 | n/a | 99% | 100% | 100% (0/0) | n/a |
| NewtonsoftJson | large | fuse/none | 1337544 | 8.9% | 1052 | 156.8 | 100% | 100% | 100% (0/0) | 80% |
| NewtonsoftJson | large | fuse/standard | 957958 | 34.7% | 926 | 160.6 | 100% | 100% | 100% (0/0) | 80% |
| NewtonsoftJson | large | fuse/aggressive | 873992 | 40.5% | 921 | 180.6 | 100% | 99% | 100% (0/0) | 79% |
| NewtonsoftJson | large | fuse/skeleton | 694862 | 52.7% | 1427 | 196.4 | 100% | 100% | 100% (0/0) | n/a |
| NewtonsoftJson | large | fuse/publicapi | 618517 | 57.9% | 1363 | 193.6 | 100% | 100% | 100% (0/0) | n/a |
| NewtonsoftJson | large | repomix/full | 1486576 | -1.3% | 1654 | n/a | 100% | 100% | 100% (0/0) | n/a |
| Serilog | small-mid | fuse/none | 184078 | 9.0% | 543 | 114 | 99% | 100% | 100% (0/0) | 99% |
| Serilog | small-mid | fuse/standard | 120213 | 40.6% | 512 | 112.6 | 99% | 100% | 100% (0/0) | 99% |
| Serilog | small-mid | fuse/aggressive | 109238 | 46.0% | 546 | 121.4 | 99% | 100% | 100% (0/0) | 99% |
| Serilog | small-mid | fuse/skeleton | 123985 | 38.7% | 736 | 139.5 | 99% | 100% | 100% (0/0) | n/a |
| Serilog | small-mid | fuse/publicapi | 104393 | 48.4% | 734 | 139.5 | 99% | 100% | 100% (0/0) | n/a |
| Serilog | small-mid | repomix/full | 206378 | -2.1% | 1470 | n/a | 99% | 100% | 100% (0/0) | n/a |
| eShopOnWeb | mid (application) | fuse/none | 65044 | 6.5% | 513 | 110.4 | 100% | 100% | 100% (10/10) | 88% |
| eShopOnWeb | mid (application) | fuse/standard | 55178 | 20.6% | 511 | 109.6 | 100% | 100% | 100% (10/10) | 88% |
| eShopOnWeb | mid (application) | fuse/aggressive | 48576 | 30.1% | 514 | 110.4 | 100% | 100% | 100% (10/10) | 69% |
| eShopOnWeb | mid (application) | fuse/skeleton | 37659 | 45.8% | 635 | 126.1 | 100% | 100% | 60% (6/10) | n/a |
| eShopOnWeb | mid (application) | fuse/publicapi | 34817 | 49.9% | 631 | 125.9 | 100% | 100% | 60% (6/10) | n/a |
| eShopOnWeb | mid (application) | repomix/full | 73958 | -6.4% | 1720 | n/a | 100% | 100% | 100% (10/10) | n/a |
| SampleShop | micro (in-repo) | fuse/none | 552 | -12.7% | 422 | 94.7 | 100% | 100% | 100% (4/4) | 100% |
| SampleShop | micro (in-repo) | fuse/standard | 497 | -1.4% | 439 | 94.6 | 100% | 100% | 100% (4/4) | 100% |
| SampleShop | micro (in-repo) | fuse/aggressive | 480 | 2.0% | 447 | 95.3 | 100% | 100% | 100% (4/4) | 100% |
| SampleShop | micro (in-repo) | fuse/skeleton | 532 | -8.6% | 509 | 107.1 | 100% | 100% | 100% (4/4) | n/a |
| SampleShop | micro (in-repo) | fuse/publicapi | 502 | -2.5% | 516 | 107.3 | 100% | 100% | 100% (4/4) | n/a |
| SampleShop | micro (in-repo) | repomix/full | 970 | -98.0% | 1424 | n/a | 100% | 100% | 100% (4/4) | n/a |
