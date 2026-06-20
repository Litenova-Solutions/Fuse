# Fuse Documentation Website Plan

Working plan for rebuilding the Fuse docs into a public documentation website. This is a planning artifact, not published documentation. Status as of 2026-06-20: approved framework, execution deferred (plan only).

- **Framework:** Astro Starlight, deployed to GitHub Pages (markdown-based).
- **Standard:** the `technical-docs` skill (three audiences, plain ASCII, no code paths in published pages).
- **Companion:** ROADMAP.md (repo root) lists the ranked feature ideas.

---

## Accuracy issues to fix first

The source review found drift between docs and code. Fix these in existing markdown before building the site so wrong facts do not carry forward.

| Issue | Detail |
|---|---|
| Stale repo layout | `Fuse.Analysis` was folded into `Fuse.Fusion`. Scoping, BM25, dependency graph, and git code now live under `Fuse.Fusion/Scoping` and `Fuse.Fusion/Enrichment`, but README.md and architecture.md still list Fuse.Analysis as a separate Core project. |
| Registry count | architecture.md says "Four registries" then lists five. |
| MCP defaults differ from CLI | `fuse_skeleton` defaults `all=true`; CLI `--all` defaults `false`. Undocumented. |
| Undocumented detail | Per-template extension/exclusion lists (26 templates), exact reducer transforms, 7 secret kinds, 6 pattern detectors, BM25 constants, cache key composition, manifest format are in code but not in docs. |

---

## Documentation strategy

Every page serves three audiences, declared per page:

- Stakeholder (first 2-3 minutes): what Fuse does, what problem it solves, token cost.
- New engineer / new user (intro + architecture): the mental model, the four-stage pipeline, first fusion.
- Expert / maintainer (selective lookup): exact flags, defaults, internals, failure modes.

The site splits into a learning track (read top to bottom) and a reference track (grep for lookup). This is the key structural decision: features.md currently mixes both.

---

## Information architecture

```
Home (landing: what Fuse is, one-call value, install + first run)
|
+-- Getting Started        [learning]
|   +-- Introduction       what problem, who it serves, when to use
|   +-- Installation       NuGet, dnx, from source, prerequisites
|   +-- Your First Fusion  run it, read the output, find the file
|   +-- Core Concepts      fusion, 4 stages, tokens, scoping, pipeline diagram
|
+-- Guides                 [learning, task-oriented]
|   +-- Fusing a .NET Project
|   +-- Reducing Tokens          reduction levels, --all, aggressive, skeleton
|   +-- Architecture Overview    skeleton + maps + semantic markers
|   +-- Scoping to What Matters  focus / search / changes (one page, three modes)
|   +-- Token Budgets & Splitting
|   +-- Output Formats           XML / Markdown / JSON with real samples
|   +-- Secret Redaction
|   +-- Configuration Files      fuse.json, .fuserc, precedence
|   +-- Watch Mode & Caching
|
+-- Agent Integration (MCP)   [learning + reference]
|   +-- MCP Overview & Setup    Claude Code, Cursor, Copilot
|   +-- Recommended Workflows   skeleton -> focus/search -> changes
|   +-- Tools Reference         all 6 tools, every parameter, defaults
|   +-- Resources Reference     all 5 fuse:// URIs
|
+-- Reference                 [lookup]
|   +-- Commands               fuse, dotnet, wiki, init, serve
|   +-- Options                every flag, grouped, alias, type, default
|   +-- Configuration Keys     every fuse.json key
|   +-- Templates              all 26, extensions + exclusions
|   +-- Reducers               per format + C#, exact transforms
|   +-- Tokenizers             models, encoding aliases, default
|   +-- Output Specification   XML/MD/JSON entry + manifest format
|   +-- Pattern Detectors      all 6, criteria, false-positive notes
|   +-- Secret Redaction Kinds all 7, patterns, limits
|
+-- Architecture              [understanding + contributing]
|   +-- Pipeline               the four stages
|   +-- Capability & Plugin Model
|   +-- Options Model
|   +-- Scoping Internals      BM25 (K1=1.2, B=0.75), dependency graph, seed resolution
|   +-- Caching Internals      XXHash64 keys, .fuse/cache
|
+-- Extending Fuse            [contributing]
|   +-- Add a Language Plugin
|   +-- Add a Template
|   +-- Add a Format Reducer
|   +-- Add a Pattern Detector
|
+-- Project
    +-- Performance & Benchmarking
    +-- Roadmap                (links ROADMAP.md)
    +-- Contributing
    +-- Changelog
```

Mapping from existing files: README intro + getting-started.md feed Getting Started; cli-reference.md becomes Reference/Commands + Options; mcp.md and mcp-integration.md become Agent Integration; features.md splits across Guides and Reference; templates.md becomes Reference/Templates; architecture.md and extending.md feed Architecture and Extending.

---

## Per-section content requirements

Each page follows the skeleton: professional opening with the problem statement, a scope section using the right header variant (Configuration Context, Implementation Context, Architectural Rationale), concrete examples with real values, a dependencies/failure-modes table, an honest "What This Does Not Cover", and a "Next" pointer.

| Page | Must contain (from source inventory) |
|---|---|
| Reference/Options | All ~50 flags across CommandBase and DotNetCommand: name, type, default, description. Grouped (directory, output, search, tokens, security, exclusions, extensions, scoping, cache/watch, C# reduction). Exact defaults: --split-tokens 800000, --depth 1, --query-top 10, --parallelism = processor count. |
| Reference/Templates | All 26 templates with full extension lists and exclusion lists, including the long DotNet exclude-patterns set. |
| Reference/Reducers | 9 web-format reducers plus CSharpReducer, exact transforms each applies (comment stripping, whitespace collapse rules, aggressive-mode, string-literal preservation). |
| Reference/Pattern Detectors | All 6 (CQRS, DI registration, exception handling, logging, async, repository) with detection criteria and false-positive caveats. |
| Reference/Secret Redaction Kinds | All 7 kinds (aws-access-key, aws-secret-key, jwt, pem-private-key, connection-string, api-token, high-entropy) with entropy threshold (>=32 chars, entropy >=4.5) and false-positive disclaimer. |
| Reference/Output Specification | XML, Markdown, JSON entry structures plus manifest format (YAML-style comment vs JSON DTO), provenance comment format, metadata fields. |
| Agent Integration/Tools | All 6 tools, full parameter tables including the all=true skeleton default and the ~25-parameter fuse_dotnet. |
| Architecture/Scoping Internals | BM25 constants and identifier-splitting behavior; seed resolution order (path, filename, type, directory prefix); depth bounds (1-10); provenance chains. |
| Architecture/Caching | XXHash64 content + options hash, the 17 reduction flags in the key, .fuse/cache layout. |
| Guides/Configuration | The real fuse init scaffold and all supported keys with precedence (CLI > config > default). |

---

## Standards enforced across all pages

1. Plain ASCII only; no em dashes, no banned jargon.
2. No file paths or line numbers in published pages. Logical names only. Code links stay in contributing/architecture pages where useful.
3. Every default value stated explicitly and verified against source.
4. Diagrams show data flow (four-stage pipeline, agent workflow decision tree), not class structure. Reuse the corrected pipeline mermaid.
5. Every reference table uses the same column shape.
6. One canonical source of truth per fact; guides link to the options table rather than restating defaults.

---

## Execution phases

1. Accuracy reconciliation (fix the drift above in current markdown).
2. Site scaffold (Astro Starlight, nav, GitHub Pages deploy, IA tree as stubs).
3. Reference track (lookup tables, fully populated).
4. Learning track (Getting Started + Guides in the three-audience voice).
5. Architecture + Extending (rebuilt from corrected internals).
6. Polish (diagrams, cross-links, search tuning, a contributor docs guide).

Recommended first step: phase 1 (accuracy reconciliation).
