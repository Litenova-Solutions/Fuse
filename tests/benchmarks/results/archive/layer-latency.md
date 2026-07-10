# Layer Latency results (scoped query call, cold vs warm)

Scoped fuse query call at a 50000 token budget, depth 2, 7 samples per cell.
Cold: no reduction cache, no persistent index. Warm: persistent index plus reduction cache, after a warmup run.
Times are milliseconds of end-to-end wall clock (the latency an agent waits on).
Absolute times are machine-dependent; read the warm-vs-cold ratio, not the raw numbers, across machines.

| Repo | Cold p50 | Cold p95 | Warm p50 | Warm p95 | Warm peak MB |
|------|---------:|---------:|---------:|---------:|-------------:|
| AutoMapper | 3373 | 3757 | 1333 | 1352 | 174.3 |
| FluentValidation | 1746 | 2198 | 862 | 898 | 156 |
| MediatR | 1191 | 1293 | 784 | 824 | 130.6 |
| NewtonsoftJson | 6348 | 8180 | 2178 | 2202 | 339.7 |
