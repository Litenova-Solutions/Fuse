# Fuse roadmap

This folder holds the forward plans and the archived execution records for Fuse, newest plan last. Each plan is self-contained: a checklist, per-item work plus tests plus docs plus benchmark, sequencing, honest ceilings, the working conventions, and a progress log. The published, reader-facing roadmap is separate and lives at [site/content/docs/project/roadmap.mdx](../site/content/docs/project/roadmap.mdx); these files are the internal engineering plans.

## Files

- [v3-overhaul-history.md](v3-overhaul-history.md): the archived V3 overhaul execution record (the original plan, the design spec, and the full progress log). Complete; kept for history.
- [v3-plan.md](v3-plan.md): the V3 forward plan (the R0 through R9 wave) that built the .NET semantic moat, hybrid retrieval, abstention, the wider analyzer set, warm latency, and the peer and agent runs.
- [v3.1-plan.md](v3.1-plan.md): the V3.1 wave. Made dense retrieval default and offline, served the first answer fast, added the git co-change signal, widened language breadth at the syntax tier, and rewrote the positioning. Landed A1, A2, A3, A6 in full; A4, A5, S10b, P4 partial; S4 and S9 assessed and deferred.
- [v3.2-plan.md](v3.2-plan.md): the current forward plan. Finishes the warm-server experience (resident workspace, default cold start, freshness), lifts the corpus index-mode ceiling, makes the semantic-tier seam load-bearing, adds the tree-sitter native engine, and completes the go-to-market assets.

## Conventions

Every plan follows the same rules: one item at a time as engine plus tests plus website docs plus a benchmark in a single change; the three gates green per item (`dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`); every number sourced to `tests/benchmarks/results`; weaknesses published; no head-to-head claim the harness does not back; plain ASCII prose. The durable project context those plans build on is in [AGENTS.md](../AGENTS.md).
