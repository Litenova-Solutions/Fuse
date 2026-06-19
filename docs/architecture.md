# Fuse Architecture

**Version:** 2.0

This document describes the target architecture for Fuse 2.0. It covers structural rationale, project layout, object model, and implementation corrections. Where older code conflicts with this document, this document is authoritative.

---

## Why a Rewrite

The pre-2.0 codebase had the right ideas in the wrong structure. These concrete issues drove a clean rewrite:

1. **Architectural dishonesty:** `Fuse.Core`, `Fuse.Engine`, and `Fuse.Minifiers` named layers, not domain concepts.
2. **God object:** `FuseOptions` carried 30+ properties through every layer.
3. **Duplicated emission logic:** `OutputBuilder` and `InMemoryOutputBuilder` shared token-counting, trivial-content filtering, and ordering loops verbatim.
4. **Sync-over-async:** `GitIgnoreParser` blocked on async I/O with `.GetAwaiter().GetResult()`.
5. **Incorrect binary detection:** A `char > 255` heuristic misclassified some text files.
6. **Broken parallelism:** `.AsParallel()` combined with synchronous file reads in filters caused thread-pool contention.
7. **Static minifier dispatch:** `ContentProcessor` switched on extension with no `IContentReducer` abstraction.
8. **Temp file race:** `{baseFileName}.tmp` collided under concurrent MCP invocations.
9. **Dead abstractions:** `IProjectTemplate` existed but the registry used tuples.
10. **Duplicate tokenizer:** Both output builders instantiated `TikToken.GetEncoding("cl100k_base")` independently.
11. **Coarse MCP surface:** `get_optimized_context` exposed only a subset of CLI options.

---

## Architectural Philosophy

### Axis-Based Structure

Fuse is a processing pipeline with three axes of change:

- **Collection:** filtering rules, templates, gitignore
- **Reduction:** file types, minification strategies
- **Emission:** output format, token counting, file splitting

Each axis has its own vocabulary, object model, and reason to change.

### Screaming Architecture

Project and folder names reflect what Fuse does (collect, reduce, emit, fuse), not the patterns used to build it.

### Rich Domain Objects

Objects carry intrinsic behavior when it requires only their own data:

- `SourceFile` knows extension, file-type booleans, and normalized path
- `FusedContent` knows token count and triviality

External collaborators belong in services and pipelines.

### Ubiquitous Language

Use glossary terms consistently in folders, classes, methods, XML docs, CLI help, and MCP descriptions.

### Open Source Library Standards

- All public types and members have XML documentation
- `./docs/` contains human-readable documentation
- Public API surface is minimal and stable

---

## Ubiquitous Language Glossary

| Term | Definition |
|------|------------|
| **Fusion** | Complete end-to-end operation: collect, reduce, emit |
| **Source Directory** | Root directory scanned during fusion |
| **Source File** | File that passed all collection filters |
| **Candidate** | File discovered during enumeration, not yet filtered |
| **Filter** | Named predicate accepting or rejecting a candidate |
| **Collection** | Phase enumerating candidates and applying filters |
| **Reducer** | Component transforming raw file content into reduced content |
| **Reduction** | Phase applying reducers to produce fused content entries |
| **Fused Content** | Reduced content of one source file, ready for emission |
| **Emission** | Phase writing fused content within a token budget |
| **Token Budget** | Constraint on tokens before splitting or halting |
| **Template** | Named config for default extensions and exclusions |
| **Fusion Result** | Output: paths or content, token count, file counts, duration |

---

## Project Structure

### Solution Layout

```
Fuse.sln

src/
  Fuse.Collection/
  Fuse.Reduction/
  Fuse.Emission/
  Fuse.Fusion/
  Fuse.Cli/

tests/
  Fuse.Collection.Tests/
  Fuse.Reduction.Tests/
  Fuse.Fusion.Tests/

docs/
  architecture.md
  getting-started.md
  cli-reference.md
  mcp-integration.md
  templates.md
  extending.md
  contributing.md

.github/workflows/
  release.yml
  ci.yml
```

### Project Responsibilities

**Fuse.Collection** (no internal Fuse dependencies)

Handles file discovery, filtering, templates, and gitignore parsing.

```
Fuse.Collection/
  Filters/           IFileFilter implementations (11 filters)
  Templates/         IProjectTemplate, registry, 26 definition classes
  FileSystem/        IFileSystem, PhysicalFileSystem, GitIgnoreParser
  Models/            FileCandidate, SourceFile, CollectionResult
  Options/           CollectionOptions
  FileCollectionPipeline.cs
```

**Fuse.Reduction** (depends on Fuse.Collection for `SourceFile` only)

Handles content normalization and extension-specific reduction.

```
Fuse.Reduction/
  Reducers/          IContentReducer, ReducerRegistry, 10 implementations
  Models/            FusedContent
  Options/           ReductionOptions
  ContentReductionPipeline.cs
```

**Fuse.Emission** (depends on Fuse.Reduction for `FusedContent` only)

Handles token counting, output writing, and file splitting.

```
Fuse.Emission/
  Writers/           IOutputWriter, DiskOutputWriter, InMemoryOutputWriter
  Tokenization/      ITokenCounter, TikTokenCounter
  Models/            TokenBudget, FusionResult, FileTokenInfo, EmissionOptions
  OutputNamingService.cs
  EmissionPipeline.cs
```

**Fuse.Fusion** (depends on all three axis projects)

Orchestrates the pipeline and exposes DI registration.

```
Fuse.Fusion/
  FusionOrchestrator.cs
  FusionRequest.cs
  FusionRequestBuilder.cs
  FusionValidator.cs
  ServiceCollectionExtensions.cs
```

**Fuse.Cli** (depends on Fuse.Fusion only)

CLI commands, MCP server, and console UI.

```
Fuse.Cli/
  Commands/          FuseCliCommand, DotNetCommand, AzureDevOpsWikiCommand, McpServeCommand
  Mcp/               FuseTools, FuseResources
  Services/          ConsoleUI, StderrConsoleUI, IConsoleUI
  Program.cs
```

### Dependency Graph

```
Fuse.Collection     (none)
Fuse.Reduction      -> Fuse.Collection
Fuse.Analysis       -> Fuse.Collection, Fuse.Reduction
Fuse.Emission       -> Fuse.Reduction, Fuse.Analysis
Fuse.Fusion         -> Fuse.Collection, Fuse.Reduction, Fuse.Analysis, Fuse.Emission
Fuse.Cli            -> Fuse.Fusion
```

NuGet packages: `DotNet.Glob` in Collection; `TiktokenSharp` in Emission; CLI packages unchanged.

---

## Options Decomposition

The monolithic `FuseOptions` is replaced by three scoped records composed in `FusionRequest`.

### CollectionOptions (14 properties)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SourceDirectory` | `string` | Current directory | Root directory to scan |
| `Template` | `ProjectTemplate?` | `null` | Template for default extensions and exclusions |
| `Extensions` | `IReadOnlyCollection<string>` | `[]` | Resolved extension list after template merging |
| `ExcludeDirectories` | `IReadOnlyCollection<string>` | `[]` | Directory names to skip |
| `ExcludeFiles` | `IReadOnlyCollection<string>` | `[]` | Specific file names to exclude |
| `ExcludePatterns` | `IReadOnlyCollection<string>` | `[]` | Glob patterns to exclude |
| `MaxFileSizeKb` | `int` | `0` | Max file size in KB; 0 = unlimited |
| `Recursive` | `bool` | `true` | Scan subdirectories |
| `IgnoreBinaryFiles` | `bool` | `true` | Skip binary files |
| `ExcludeEmptyFiles` | `bool` | `true` | Skip zero-byte files |
| `ExcludeAutoGenerated` | `bool` | `true` | Skip auto-generated files |
| `ExcludeTestProjects` | `bool` | `false` | Exclude all test project directories |
| `ExcludeUnitTestProjects` | `bool` | `false` | Exclude only unit test directories |
| `RespectGitIgnore` | `bool` | `true` | Honor `.gitignore` rules |

`IncludeExtensions`, `ExcludeExtensions`, and `OnlyExtensions` are CLI inputs resolved into `Extensions` by `FusionRequestBuilder`. They are not properties on `CollectionOptions`.

### ReductionOptions (13 properties)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `TrimContent` | `bool` | `true` | Trim leading and trailing whitespace per line |
| `UseCondensing` | `bool` | `true` | Collapse consecutive blank lines |
| `RemoveCSharpComments` | `bool` | `false` | Remove C# comments |
| `RemoveCSharpUsings` | `bool` | `false` | Remove C# using directives |
| `RemoveCSharpNamespaces` | `bool` | `false` | Remove C# namespace declarations |
| `RemoveCSharpRegions` | `bool` | `false` | Remove C# region directives |
| `AggressiveCSharpReduction` | `bool` | `false` | Apply aggressive C# reduction |
| `MinifyXmlFiles` | `bool` | `true` | Minify XML-based files |
| `MinifyHtmlAndRazor` | `bool` | `true` | Minify HTML and Razor files |
| `SkeletonMode` | `bool` | `false` | Emit C# structural skeleton only |
| `IncludeSemanticMarkers` | `bool` | `false` | Prepend semantic marker comments |
| `IncludePatternSummary` | `bool` | `false` | Run pattern detectors after emission |

The `--all` flag sets each individual C# reduction property to `true` at the CLI boundary. Agentic flags (`SkeletonMode`, etc.) are independent of `--all`.

### FusionRequest focus and change scoping

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Focus` | `FocusOptions?` | `null` | Dependency-aware file scoping |
| `Changes` | `ChangeOptions?` | `null` | Git change-scoped file selection |

---

## Fuse.Analysis Axis

`Fuse.Analysis` hosts regex-based code analysis used by agentic features:

| Concern | Types | Integration |
|---------|-------|-------------|
| Skeleton | `CSharpSkeletonExtractor` | `ContentReductionPipeline` after reduction |
| Semantic markers | `CSharpSemanticMarkerGenerator` | `ContentReductionPipeline` after skeleton |
| Dependencies | `DependencyGraphBuilder`, `FocusSeedResolver` | `FusionOrchestrator` between collection and reduction |
| Change detection | `GitChangeDetector` | `FusionOrchestrator` between collection and reduction |
| Patterns | Six `IPatternDetector` implementations | `FusionOrchestrator` after emission |

Interfaces `ISkeletonExtractor` and `ISemanticMarkerGenerator` live in `Fuse.Reduction` to avoid circular project references. Implementations register from `Fuse.Fusion` DI.

Dependency graphs are best-effort approximations (no Roslyn). They may miss dynamic dispatch or produce false positives from type names in comments.

### EmissionOptions (8 properties)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `OutputDirectory` | `string` | My Documents/Fuse | Directory for output files |
| `OutputFileName` | `string?` | `null` | Custom filename without extension |
| `Overwrite` | `bool` | `true` | Overwrite existing output files |
| `IncludeMetadata` | `bool` | `false` | Include file size and modification date |
| `MaxTokens` | `int?` | `null` | Hard token limit |
| `SplitTokens` | `int?` | `800000` | Token threshold for splitting output |
| `ShowTokenCount` | `bool` | `true` | Display token count on completion |
| `TrackTopTokenFiles` | `bool` | `false` | Track and display top 5 token-consuming files |

### FusionRequest

```
FusionRequest
  CollectionOptions   Collection
  ReductionOptions    Reduction
  EmissionOptions     Emission
  bool                InMemory      // true for MCP tool invocations
```

CLI commands construct `FusionRequest` via `FusionRequestBuilder`. The builder owns extension and template resolution logic from the former `ConfigurationResolver`.

---

## Key Type Definitions

### Collection Axis

**FileCandidate** is an immutable record with `FullPath`, `RelativePath`, and `FileInfo`. No behavior.

**SourceFile** wraps a `FileCandidate` with behavioral properties:

- `Extension` (lowercase with leading dot)
- `IsCSharp`, `IsRazor`, `IsHtml`, `IsCss`, `IsJson`, `IsXml`, `IsMarkdown`, `IsYaml`, `IsJavaScript`
- `NormalizedRelativePath` (backslashes replaced with forward slashes)

**CollectionResult** contains `IReadOnlyList<SourceFile>` plus the candidate count evaluated for reporting.

**IFileFilter**

```csharp
bool Include(FileCandidate candidate, CollectionOptions options);
```

All filters register in DI as `IFileFilter`. `FileCollectionPipeline` receives `IEnumerable<IFileFilter>`. Registration order equals evaluation order.

**IProjectTemplate**

```csharp
string Name { get; }
IReadOnlyCollection<string> Extensions { get; }
IReadOnlyCollection<string> ExcludeDirectories { get; }
IReadOnlyCollection<string> ExcludePatterns { get; }
```

26 concrete classes live in `Templates/Definitions/`. `ProjectTemplateRegistry` discovers templates from DI, keyed by the `ProjectTemplate` enum.

### Reduction Axis

**FusedContent** is an immutable record with:

- `SourceFile` (originating file)
- `Content` (reduced content string)
- `TokenCount` (computed once at construction via `ITokenCounter`)
- `IsTrivial` (true if whitespace-only, `{}`, `[]`, or short self-closing XML tag)
- `NormalizedPath` (delegates to `SourceFile.NormalizedRelativePath`)

Trivial entries are filtered in `ContentReductionPipeline`. Emission never sees them.

**IContentReducer**

```csharp
string Extension { get; }
string Reduce(string content, ReductionOptions options);
```

Registered in DI as `IContentReducer`. `ReducerRegistry` builds a dictionary keyed by extension. When no reducer matches, content passes through unchanged after whitespace normalization.

**ContentReductionPipeline** for each `SourceFile`:

1. Read content
2. Apply whitespace normalization once (trim per line, collapse blank lines) when `TrimContent`/`UseCondensing` apply
3. Resolve and invoke reducer
4. Construct `FusedContent` with token count
5. Filter trivial entries
6. Return `IReadOnlyList<FusedContent>`

Normalization does not occur inside individual reducers.

### Emission Axis

**ITokenCounter**

```csharp
int Count(string content);
```

Single implementation: `TikTokenCounter` wrapping `TikToken.GetEncoding("cl100k_base")`. Registered as a singleton in DI.

**TokenBudget** tracks consumption against `MaxTokens` and `SplitTokens`:

- `Consume(int tokens)` returns `BudgetConsumeResult` (continue, split, or halt)
- `IsExhausted` is true when `MaxTokens` is reached
- `CurrentPartTokens` counts tokens in the current output part
- `TotalTokens` counts tokens across all parts

**IOutputWriter**

```csharp
Task WriteEntryAsync(FusedContent content, CancellationToken cancellationToken);
Task<FusionResult> CompleteAsync(CancellationToken cancellationToken);
```

Implementations: `DiskOutputWriter` and `InMemoryOutputWriter`. Both receive `EmissionOptions` and `ITokenCounter` via constructor injection.

**EmissionPipeline** accepts `IReadOnlyList<FusedContent>`, `EmissionOptions`, and `IOutputWriter`:

- Iterates entries in descending size order
- Calls `TokenBudget.Consume` per entry
- Handles splits by creating a new writer part
- Delegates to `IOutputWriter.WriteEntryAsync`
- Returns `FusionResult`

**FusionResult** contains:

- `IReadOnlyList<string> GeneratedPaths` (disk emission only)
- `string? InMemoryContent` (in-memory emission only)
- `long TotalTokens`
- `int ProcessedFileCount`
- `int TotalFileCount`
- `TimeSpan Duration`
- `IReadOnlyList<FileTokenInfo> TopTokenFiles`

### Fusion Axis

**FusionOrchestrator** is the single public programmatic entry point:

```csharp
Task<FusionResult> FuseAsync(FusionRequest request, CancellationToken cancellationToken);
```

It sequences `FileCollectionPipeline`, `ContentReductionPipeline`, and `EmissionPipeline`. The orchestrator is stateless; all per-run state is local to `FuseAsync`.

**FusionValidator** validates `FusionRequest` before execution. Examples:

- Contradictory combinations (`OnlyExtensions` + `Template`)
- Missing or invalid `SourceDirectory`

Throws `FusionValidationException` with errors. No silent resolution.

---

## Implementation Corrections

| Issue | Fix |
|-------|-----|
| Binary detection | Read first 8000 bytes as raw bytes; if any byte is `0x00`, classify as binary |
| GitIgnore async | `ParseAsync` uses `File.ReadAllTextAsync` with `CancellationToken`; no blocking |
| Temp file naming | `Path.GetTempFileName()` or GUID suffix; never derive from output filename alone |
| Parallelism | No `.AsParallel()` in collection; sequential enumeration |
| Whitespace | Normalization once in `ContentReductionPipeline` before reducer; not in reducers |
| ApplyAllOptions | Removed from all option types; `--all` sets individual flags at CLI |
| MCP concurrency | Stateless `FusionOrchestrator` plus unique temp files enables safe concurrent invocations |

Catch-and-swallow is permitted only in `AutoGeneratedFileFilter` and `BinaryFileFilter` when file read fails. Each case requires an inline comment explaining why.

---

## Pipeline Flow

```
FusionRequest
    |
    v
FusionValidator
    |
    v
FileCollectionPipeline  (enumerate candidates, apply filters)
    |
    v
[Optional] Focus or Change filter  (FusionOrchestrator + Fuse.Analysis)
    |
    v
ContentReductionPipeline  (read, normalize, reduce, skeleton, markers, filter trivial)
    |
    v
EmissionPipeline  (order by size, token budget, write)
    |
    v
[Optional] Pattern summary  (FusionOrchestrator + IPatternDetector)
    |
    v
FusionResult
```

---

## Dependency Injection

All services register in `ServiceCollectionExtensions.AddFuse()` in `Fuse.Fusion`:

| Lifetime | Types |
|----------|-------|
| Singleton | `ITokenCounter`, `FusionOrchestrator`, `FusionValidator`, `ProjectTemplateRegistry`, `ReducerRegistry` |
| Transient | `FileCollectionPipeline`, `ContentReductionPipeline`, `EmissionPipeline` |
| All as interface | Every `IFileFilter`, `IContentReducer`, `IProjectTemplate` |

Filter registration order equals evaluation order.
