# Agent Instructions

Read this file before editing Fuse source. Full rules live in [docs/contributing.md](docs/contributing.md). Pipeline context is in [docs/architecture.md](docs/architecture.md).

## Documentation (two layers)

### Public API: XML (`///`)

Apply to every `public` and `protected` type and member in `src/**/Fuse.*`.

1. Document on the **interface or abstract base** first; implementations use `<inheritdoc />` unless they add behavior worth noting.
2. Required tags: `<summary>`, `<param>` (every parameter, including `CancellationToken`), `<returns>` (non-void), `<exception cref="...">` (intentionally thrown).
3. Use `<remarks>` for ordering guarantees, side effects, performance, null semantics, or algorithm constraints.
4. Use `<see cref="..."/>` to link related types instead of repeating docs.
5. Style: four-space indent after `///`; property summaries as noun phrases (never "Gets or sets"); `<c>` for literals.

Templates: `GitIgnoreParser`, `ISecretRedactor` in `src/Core/Fuse.Collection` and `src/Core/Fuse.Reduction`.

Do **not** add XML to `private` members.

### Internal complexity: `//` comments

Use regular comments for non-obvious `private` or `internal` logic: heuristics, state machines, regex pipelines, invariants, edge cases.

- One line above the method or block: what it does and any non-obvious constraint.
- Inline `//` at branch points for edge cases.
- Explain **why**, not **what**. Skip obvious code.

Comment when a reader must hold mental state (depth counters, accumulation, thresholds) to change the code safely.

## Fuse-specific rules

| Area | Public XML | Private `//` |
|------|------------|--------------|
| Orchestration (`FusionOrchestrator`, `*Pipeline`) | `<remarks>` on stage ordering and delegation | Phase logic inside private helpers |
| Language plugins | Full docs on capability interfaces; thin impls use `<inheritdoc />` | Regex/scan heuristics |
| Detectors and reducers | Summary + remarks on false-positive tradeoffs | Non-obvious matching rules |
| Options/DTO records | Summary when the name alone is ambiguous | Rarely needed |

## Code changes

- Match existing C# conventions in [docs/contributing.md](docs/contributing.md#c-conventions).
- New public API without XML docs is incomplete.
- Do not add XML to private members or restate `architecture.md` in file comments.
