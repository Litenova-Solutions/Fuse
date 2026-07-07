# Fuse roadmap

This folder holds the forward plans and the archived execution records for Fuse, newest plan last. Each plan is self-contained: a checklist, per-item work plus tests plus docs plus benchmark, sequencing, honest ceilings, the working conventions, and a progress log. The published, reader-facing roadmap is separate and lives at [site/content/docs/project/roadmap.mdx](../site/content/docs/project/roadmap.mdx); these files are the internal engineering plans.

**Start here (repo root):** [briefing.md](../briefing.md) is the single briefing document for the whole project (architecture, algorithms, benchmarks, gaps, plan history). Read it before [v4-plan.md](v4-plan.md) when orienting or planning; pass it to an LLM instead of the source tree when evidence-backed roadmap work is the goal.

## Files

- [briefing.md](../briefing.md) (repo root): project briefing. Self-contained: what Fuse is, how it is built, all canonical benchmark results with methodology, known issues, roadmap history, and the 4.0.0 oracle thesis. Updated against the live codebase (currently 4.0.0). Not the contributor guide ([AGENTS.md](../AGENTS.md)) and not the executable checklist ([v4-plan.md](v4-plan.md)).
- [v3-plan.md](v3-plan.md): the V3 forward plan (the R0 through R9 wave) that built the .NET semantic moat, hybrid retrieval, abstention, the wider analyzer set, warm latency, and the peer and agent runs. The archived V3 overhaul execution record (the original plan, the design spec, and the full progress log) is kept at the end of this same file for history.
- [v3.1-plan.md](v3.1-plan.md): the V3.1 wave. Made dense retrieval default and offline, served the first answer fast, added the git co-change signal, widened language breadth at the syntax tier, and rewrote the positioning. Landed A1, A2, A3, A6 in full; A4, A5, S10b, P4 partial; S4 and S9 assessed and deferred.
- [v3.2-plan.md](v3.2-plan.md): the warm-server finishing wave. Shipped the host-on-semantic-index migration (protocol 3) and the rich index panel. Left the resident Roslyn workspace (W1) and semantic-mode corpus coverage (W4) only partly landed; both are picked up in v4.
- [v4-plan.md](v4-plan.md): the current forward plan. The compiler-oracle release (4.0.0): L1/L2 governance first (Apache 2.0, DCO), then trustworthy floor, oracle tools, moonshot staging, and gated retrieval bets.

## Conventions

Every plan follows the same rules: one item at a time as engine plus tests plus website docs plus a benchmark in a single change; the three gates green per item (`dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`); every number sourced to `tests/benchmarks/results`; weaknesses published; no head-to-head claim the harness does not back; plain ASCII prose. The durable project context those plans build on is in [AGENTS.md](../AGENTS.md).
