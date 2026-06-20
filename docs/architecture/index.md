---
title: Architecture
description: The four-stage pipeline, capability model, options model, and scoping and caching internals.
---

This section explains how Fuse is built, for engineers who want the mental model and maintainers who need implementation detail and failure modes. It is authoritative where it conflicts with older descriptions.

## Pages

- [The Fusion Pipeline](pipeline.md): the four stages and how data flows between them.
- [Capability and Plugin Model](capability-model.md): how language behavior is registered and resolved.
- [The Options Model](options-model.md): the request record, defaults, and validation rules.
- [Scoping Internals](scoping-internals.md): BM25 ranking, the dependency graph, and seed resolution.
- [Caching Internals](caching-internals.md): the reduction cache keys and layout.

## Next

Start with [The Fusion Pipeline](pipeline.md).
