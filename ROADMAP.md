# Fuse Roadmap

Planned and desired features, ranked by impact on agent efficiency. Each item names its primary lever: accuracy, token cost, or round-trips. This document states intent, not commitment. Shipped work moves to the [CHANGELOG](CHANGELOG.md).

Architecture context for these items: [docs/architecture/index.md](docs/architecture/index.md).

## Recently shipped

The most recent additions are recorded in the [CHANGELOG](CHANGELOG.md): the Roslyn semantic plugin, symbol-level scoping, the persistent on-disk index, the `fuse_ask` auto-scope tool, session-delta emission, the table-of-contents survey, review-shaped change emission, cross-language reduction, generated-code collapse, and the hybrid-retrieval reranker. The items below are what remains.

---

## 1. Learned embedding model for hybrid retrieval

The hybrid reranker shipped with a deterministic lexical hashing embedding, which sharpens identifier matching but does not bridge a true semantic gap. The remaining work is an optional, AOT-friendly learned embedding model behind the existing `IEmbeddingModel` interface and on-disk vector store, so an intent query such as "where is rate limiting handled" resolves to `TokenBucketMiddleware` even with no shared vocabulary.

**Lever:** accuracy on intent-based queries that do not match lexically. The reranker plumbing and Layer 2B regression guard are already in place.

## 2. Call-site context on focus

When focusing on a type, include how it is used (call sites), not only what it depends on. Review-shaped change emission already pairs a changed file with its direct callers; this extends that pairing to focus scoping and surfaces the call sites themselves, not just the calling files.

**Lever:** accuracy and fewer follow-up calls. Agents routinely need "how is this called."

## 3. Benchmark and measurement suite

Extend [tests/benchmarks](tests/benchmarks) into a published, reproducible benchmark that solidifies Fuse's claims. Built in layers by cost and persuasiveness:

- **Layer 1, intrinsic (built):** deterministic, CI-able metrics over a fixed corpus of real OSS .NET repos pinned by commit. Per reduction mode: token count and reduction ratio vs raw concatenation and vs a competitor packer, cold wall-clock, peak memory, and per-mode fidelity. Fidelity is the integrity guard: it proves reduction is not deletion.
- **Layer 2, scoping recall (built):** from real merged PRs, take changed-file sets as ground truth and measure recall at a token budget and precision for focus, query, and changes against a grep baseline, plus a single-turn localization task.
- **Layer 3, task resolution (aspirational):** a SWE-bench-style .NET harness (issue, agent patch, `dotnet test` oracle) driving a pinned programmatic agent across arms. The only end-to-end efficacy proof, but confounded, expensive, and scaffold-specific, so it is directional evidence rather than universal proof.

Remaining work on the built layers: fold the precision-tier skeleton fidelity into the harness as a permanent arm (the figures are recorded in [benchmarks.md](docs/project/benchmarks.md) from a manual run over the same mirrors, where the Roslyn skeleton lifts Newtonsoft.Json method fidelity from 4 to 100 percent), and add a TypeScript or SQL repository to the corpus to confirm a cross-language reduction ratio.

Honesty rules across all layers: never fabricate numbers, open-source the harness and corpus manifest, make every layer reproducible with one command, report arms where Fuse ties or loses, and state that reduction ratios transfer across models even though absolute token counts do not.

**Lever:** proof and regression guard.
