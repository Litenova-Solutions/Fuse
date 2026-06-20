---
title: Generating an Architecture Overview
description: Use the structural-map features to build a first-pass view of an unfamiliar .NET solution before reading any implementation.
---

Faced with an unfamiliar solution, a reader needs to know its shape before its detail: which types exist, what implements what, where the routes live, and how the projects reference each other. Fuse builds that view directly. This guide walks through the structural-map features, each of which extracts or annotates structure rather than emitting full source, and shows how to combine them into one orientation pass.

This page is for engineers and agents opening a solution for the first time, and for leads who want a structural summary without reading code.

## When This Applies

Reach for these features at the start of work on a codebase you do not know, or when producing a high-level map for a review. They suit a first pass: a small, structural fusion that orients the reader, after which a scoped fusion brings in the detail for the area that matters. The [Scoping to What Matters](scoping.md) guide covers that second step, and the [agent integration workflows](../agent-integration/workflows.md) show the two used together.

## Skeletons

The `--skeleton` flag emits class, interface, and method signatures with no method bodies. It is the foundation of an architecture pass: the type and member surface of the whole solution at a fraction of the token cost. The [Reducing Tokens](reducing-tokens.md) guide covers skeleton extraction in the context of the reduction range.

```bash
fuse dotnet --directory ./src --skeleton
```

## Semantic Markers

The `--semantic-markers` flag prepends a structural annotation comment to each type, so a reader learns a type's role without reading its body. Each marker takes this form:

```xml
<!-- fuse:type OrderService | kind:class | implements:IOrderService -->
```

The marker records the type name, its kind, the interfaces it implements, the types it depends on, and its constructor parameter types. Markers pair naturally with skeletons, annotating each signature with its relationships.

## Route Maps

The `--route-map` flag prepends an ASP.NET route map: a table of HTTP verb, path, and handler drawn from controllers and minimal API endpoints. It answers what the application exposes and where each endpoint is handled, which is often the fastest way into a web service.

```bash
fuse dotnet --directory ./src --route-map
```

## Public API Surface

The `--public-api` flag emits only public and protected member skeletons, dropping private and internal members. The result is the contract a project presents to its consumers, which suits documenting a library or reasoning about a boundary between projects.

## Project Graphs

The `--project-graph` flag prepends the solution and project reference structure: which projects exist and how they depend on one another. It shows the layering of a solution at a glance, before any file content.

## Pattern Summaries

The `--pattern-summary` flag detects and appends a summary of the conventions in use across the codebase, such as recurring structural patterns. The kinds Fuse detects are in the [Pattern Detectors reference](../reference/pattern-detectors.md).

## Combine Into One Pass

The structural maps prepend to the output, so several can run in a single fusion to produce a layered overview. A first pass on a web solution might combine a route map, a project graph, and skeletons:

```bash
fuse dotnet --directory ./src --route-map --project-graph --skeleton
```

The output opens with the project graph and route map, then the skeleton of every type, giving a reader the solution's structure, its layering, and its endpoints in one small payload.

## Structural Features

| Feature | Flag | Produces |
|---------|------|----------|
| Skeleton | `--skeleton` | Type and method signatures, no bodies |
| Semantic markers | `--semantic-markers` | Per-type annotation comments |
| Route map | `--route-map` | HTTP verb, path, and handler table |
| Public API | `--public-api` | Public and protected member skeletons only |
| Project graph | `--project-graph` | Solution and project reference structure |
| Pattern summary | `--pattern-summary` | Detected conventions across the codebase |

## What This Does Not Cover

This page covers the structural-map features. It does not cover narrowing the overview to one feature area, which is scoping; see [Scoping to What Matters](scoping.md). The dependency relationships the markers and graph rely on are best-effort and regex-based, with their limits documented in the [Pattern Detectors reference](../reference/pattern-detectors.md).

## Next

Continue to [Scoping to What Matters](scoping.md) to follow a structural pass with a focused fusion, or to the [agent integration workflows](../agent-integration/workflows.md) to see the sequence an agent runs.
