# Fuse Roadmap

Planned and desired features, ranked by impact on agentic efficiency. Each item names its primary lever: accuracy, token cost, or round-trips. This document states intent, not commitment. Shipped work moves to the [CHANGELOG](CHANGELOG.md).

Architecture context for these items: [docs/architecture.md](docs/architecture.md).

---

## 1. Roslyn-backed semantic plugin (opt-in)

Accurate `IDependencyExtractor`, `ISkeletonExtractor`, and `ITypeNameLocator` implementations registered behind the existing `CapabilityRegistry<T>` boundary. Gives true call graphs, find-references, inheritance hierarchies, and accurate skeletons.

The regex plugin stays the AOT-fast default; Roslyn becomes the precision mode.

**Lever:** accuracy. Removes the "best-effort, may miss dynamic dispatch" ceiling. This is the durable moat.

## 2. Symbol-level scoping

Emit only the relevant members of a large file instead of the whole file (for example, `OrderService.Charge` and what it calls, not all 2000 lines). Depends on #1 for precise member resolution.

**Lever:** token cost. Large reduction on real codebases where god-classes dominate the budget.

## 3. Persistent incremental index

Keep the dependency graph and BM25 index on disk (`.fuse/index`), keyed by content hash, updated incrementally. Watch mode keeps it warm.

**Lever:** latency and token cost across a session. An agent making several MCP calls in one task pays the index cost once, not per call.

## 4. Auto-scope tool (`fuse_ask`)

A single MCP tool that takes a natural-language task plus a token budget and internally decides skeleton vs focus vs search, then packs to budget.

**Lever:** round-trips. Collapses the manual skeleton-then-focus-then-search orchestration into one call.

## 5. Session-delta emission

A session-aware mode that tracks what was already sent to the agent and emits only new material (for example, "you already have `OrderService`; here is the one new dependency").

**Lever:** token cost in multi-turn sessions, which is where agents actually operate.

## 6. Call-site context on focus

When focusing on a type, include how it is used (call sites), not just what it depends on.

**Lever:** accuracy and fewer follow-up calls. Agents routinely need "how is this called."

## 7. Hybrid retrieval (BM25 plus embeddings rerank)

Optional, AOT-friendly on-disk vectors reranking BM25 candidates so intent-based queries resolve (for example, "where is rate limiting handled" matching `TokenBucketMiddleware`).

**Lever:** accuracy on intent-based queries that do not match lexically.

## 8. Eval harness: task success vs token cost

Extend [tests/benchmarks](tests/benchmarks) into a real eval: run the same coding tasks with and without Fuse and measure tokens-to-correct-answer.

**Lever:** proof and regression guard. Produces claims like "Fuse cut tokens by N times with no loss in task success."

---

## Suggested first release

Items 1, 3, and 4 together attack accuracy, cost, and round-trips at once: Roslyn makes the output trustworthy, the persistent index makes repeated calls cheap, and `fuse_ask` makes the whole thing one call instead of three.
