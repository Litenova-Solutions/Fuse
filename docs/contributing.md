# Contributing to Fuse

Thank you for contributing to Fuse. This guide covers setup, development workflow, and coding standards.

---

## Getting Started

### Clone and Build

```bash
git clone https://github.com/Litenova-Solutions/Fuse.git
cd Fuse
dotnet restore Fuse.slnx
dotnet build Fuse.slnx
```

On Windows, `install.bat` builds, packs, and installs the global tool locally for manual testing.

### Run Tests

```bash
dotnet test Fuse.slnx --configuration Release
```

Test projects for Fuse 2.0:

| Project | Scope |
|---------|-------|
| `tests/Fuse.Collection.Tests` | One test class per file filter |
| `tests/Fuse.Reduction.Tests` | One test class per content reducer |
| `tests/Fuse.Fusion.Tests` | Validator unit tests and orchestrator integration tests |

### Format Check

CI enforces formatting. Verify locally before pushing:

```bash
dotnet format Fuse.slnx --verify-no-changes
```

Apply fixes automatically:

```bash
dotnet format Fuse.slnx
```

---

## Development Workflow

### Branch and PR

1. Fork the repository or create a feature branch from `main`
2. Make focused changes with clear commit messages
3. Run build, test, and format check locally
4. Open a pull request against `main`
5. CI runs on every PR: build, test, and format verification on Windows with .NET 10.0

### Commit Messages

Use imperative mood, 72 characters or fewer for the subject line, no trailing period:

```
Add Kotlin template exclude patterns for build output

Fix binary detection to scan raw bytes instead of char values
```

Describe what changed and why, not what the code does line by line.

### What to Change Where

| Change Type | Location |
|-------------|----------|
| File filtering | `Fuse.Collection/Filters/` |
| Template defaults | `Fuse.Collection/Templates/Definitions/` |
| Content reduction | `Fuse.Reduction/Reducers/Implementations/` |
| Output and tokens | `Fuse.Emission/` |
| Pipeline orchestration | `Fuse.Fusion/` |
| CLI commands | `Fuse.Cli/Commands/` |
| MCP tools | `Fuse.Cli/Mcp/` |
| DI registration | `Fuse.Fusion/Extensions/ServiceCollectionExtensions.cs` |

See [extending.md](extending.md) for step-by-step guides on adding templates and reducers.

---

## Coding Standards

### Documentation overview

Fuse uses two documentation layers:

| Surface | Mechanism | Audience |
|---------|-----------|----------|
| `public` / `protected` types and members | XML (`///`) | API consumers, IDE tooltips, future docgen |
| Non-obvious `private` / `internal` logic | `//` comments | Maintainers and agents |

Agents should read [AGENTS.md](../AGENTS.md) for a short checklist. This section is the full standard.

### XML documentation (public API)

Base standard: [Microsoft XML documentation comments](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/xmldoc/).

**Scope:** Every `public` and `protected` type and member in `src/Fuse.*`.

**Source of truth:** Document on the interface or abstract base first. Implementations use `<inheritdoc />` unless they add behavior worth calling out.

**Required tags:**

| Tag | When |
|-----|------|
| `<summary>` | Always. One complete sentence. State what it does and, when non-obvious, when or why. |
| `<param>` | Every parameter, including `CancellationToken`. |
| `<returns>` | Every non-`void` method. Describe meaning, not just the type. |
| `<exception cref="...">` | Every intentionally thrown exception. |
| `<remarks>` | Ordering guarantees, side effects, performance, null semantics, or constraints that do not fit in `<summary>`. |
| `<see cref="..."/>` | Cross-references to related types instead of repeating docs. |

**Style:**

- Indent summary body with four spaces after `///`.
- Property summaries: noun phrase describing the value. Never "Gets or sets".
- Use `<c>` for literals (`[REDACTED:kind]`, `.gitignore`, magic strings).
- Describe nullability in `<param>` or `<returns>` when behavior depends on it.
- Constructors: one-line summary is enough. Full `<param>` per argument when names are not self-explanatory.

**Do not XML-document:**

- Obvious pass-through properties when name and type are sufficient.
- Trivial one-liners where the summary would repeat the identifier.
- `private` members.
- `internal` types unless they form a cross-assembly contract.

**Fuse-specific:**

- Orchestration types (`FusionOrchestrator`, `*Pipeline`): use `<remarks>` for stage ordering and what they delegate.
- Language plugin interfaces (`IContentReducer`, `ISkeletonExtractor`, etc.): document thoroughly; thin implementations use `<inheritdoc />`.
- Heuristic detectors: summary plus remarks on false-positive tradeoffs.
- Options/DTO records: property summary when the name alone is ambiguous.

**Example (public):**

```csharp
/// <summary>
///     Asynchronously parses all <c>.gitignore</c> files from the starting directory up to the repository root.
/// </summary>
/// <param name="startDirectory">Absolute path to begin upward traversal.</param>
/// <param name="cancellationToken">Token used to cancel file reads.</param>
/// <returns>
///     Compiled glob patterns for absolute path matching. Empty when no <c>.gitignore</c> files exist.
/// </returns>
```

### Internal comments (`//`)

Use for private methods, nested loops, heuristics, state machines, regex pipelines, and invariants.

- One line above the method or block: what it does and any non-obvious constraint.
- Inline `//` at branch points for edge cases.
- Explain why, not what. Skip code that reads clearly.

Comment when a reader must hold mental state (depth counters, accumulation, entropy thresholds) to change the code safely.

**Example (private):**

```csharp
// Treat long mixed-character literals as secrets when Shannon entropy >= 4.5
// to catch API keys not matched by named regex patterns.
```

### Build enforcement

Non-test projects set `GenerateDocumentationFile` in `Directory.Build.props` (projects whose name does not end in `.Tests`). Missing public XML docs produce compiler warning CS1591. Fix warnings when you touch a file; do not add new CS1591 warnings in pull requests.

### C# Conventions

- Target: `net10.0`
- Nullable reference types enabled
- `record` for immutable data; `sealed record` for leaf types
- `required` properties where a value must be provided at construction
- `init` setters on all record properties
- No primary constructors; use explicit constructors with field assignments
- `sealed` on concrete classes not designed for inheritance
- `internal` on non-public API types
- Collection expressions (`[]`) for empty collections
- Public methods return `IReadOnlyList<T>` or `IReadOnlyCollection<T>`, never `IEnumerable<T>`
- `var` when the type is obvious; explicit types otherwise
- Expression-bodied members for single-line properties and methods
- All async methods accept and propagate `CancellationToken`
- Implicit usings enabled; file-scoped namespaces
- Inline comments explain why, not what

### Error Handling

- Throw specific custom exceptions, not generic `InvalidOperationException`
- `FusionValidationException` from `FusionValidator`
- `FusionException` from `FusionOrchestrator` for unrecoverable runtime errors
- Catch-and-swallow only in `AutoGeneratedFileFilter` and `BinaryFileFilter` with an explanatory comment

### Dependency Injection

Register all services in `AddFuse()`:

| Lifetime | Types |
|----------|-------|
| Singleton | `ITokenCounter`, `FusionOrchestrator`, `FusionValidator`, registries |
| Transient | Pipeline classes (`FileCollectionPipeline`, etc.) |
| Interface registration | All `IFileFilter`, `IContentReducer`, `IProjectTemplate` |

Filter registration order equals evaluation order.

### Testing Requirements

| Requirement | Detail |
|-------------|--------|
| Filter tests | Pass `FileCandidate` directly; no filesystem |
| Reducer tests | Pass content strings directly |
| Validator tests | Cover all validation rules |
| Orchestrator tests | Use real temp directories with sample files |
| Focus | Test real behavior, not trivial assertions |

### Native AOT and golden files

- Publish locally: `dotnet publish src/Fuse.Cli/Fuse.Cli.csproj /p:PublishProfile=aot-win-x64` (Windows) or use `./build/pack-aot.ps1`.
- Linux AOT requires `clang` and `zlib1g-dev`.
- Regenerate golden output: `UPDATE_GOLDEN_FILES=1 dotnet test tests/Fuse.GoldenOutput.Tests`.
- Tokenizer reference vectors live in `tests/Fuse.Emission.Tests/TokenizerReferenceTests.cs`.

---

## Architecture Overview

Fuse 2.0 uses an axis-based pipeline:

```
Fuse.Cli -> Fuse.Fusion -> Collection / Reduction / Emission
```

Read [architecture.md](architecture.md) before making structural changes. The axis boundaries exist to keep each concern isolated. Do not add cross-axis dependencies.

---

## Documentation

When your change affects user-facing behavior:

- Update the relevant file in `docs/`
- Add new templates to [templates.md](templates.md)
- Add new CLI options to [cli-reference.md](cli-reference.md)
- Add new MCP parameters to [mcp-integration.md](mcp-integration.md)

Use ASCII-only prose. No em dashes.

---

## CI Pipeline

The CI workflow (`.github/workflows/ci.yml`) runs on pull requests and pushes to `main`:

1. Checkout
2. Setup .NET 10.0
3. Restore
4. Build (Release)
5. Test (Release)
6. Format check

All steps must pass before merge.

---

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
