# Extending Fuse

This guide covers adding a new project template and a new content reducer. Both require a class implementation and DI registration in `ServiceCollectionExtensions.AddFuse()`.

---

## Adding a Project Template

### 1. Add an Enum Value

Add a member to `ProjectTemplate` in `Fuse.Collection/Models/ProjectTemplate.cs`:

```csharp
/// <summary>
///     Template for MyLanguage projects.
/// </summary>
MyLanguage,
```

Add XML documentation describing included extensions and excluded directories.

### 2. Create the Template Class

Create `Fuse.Collection/Templates/Definitions/MyLanguageTemplate.cs`:

```csharp
using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for MyLanguage projects.
/// </summary>
public sealed class MyLanguageTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.MyLanguage);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".mylang", ".mylproj", ".yaml"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["build", "dist", ".git", "vendor"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludePatterns =>
        ["*.generated.mylang", "*.min.js"];
}
```

Rules:

- Extend `ProjectTemplateBase`
- Mark the class `sealed`
- `Name` must match the enum member name exactly
- Extensions include the leading dot
- Override `ExcludePatterns` only when the template has glob exclusions; otherwise the base returns an empty collection

### 3. Register in DI

Add the template to `RegisterProjectTemplates()` in `Fuse.Fusion/Extensions/ServiceCollectionExtensions.cs`:

```csharp
services.AddSingleton<IProjectTemplate, MyLanguageTemplate>();
```

`ProjectTemplateRegistry` discovers all registered `IProjectTemplate` instances automatically. No registry edit is required.

### 4. Verify

Build and test:

```bash
dotnet build Fuse.sln
dotnet test tests/Fuse.Fusion.Tests
```

Test via CLI (MCP or programmatic API):

```csharp
var result = await orchestrator.FuseAsync(
    new FusionRequestBuilder(templateRegistry)
        .WithSourceDirectory("/path/to/project")
        .WithTemplate(ProjectTemplate.MyLanguage)
        .Build());
```

Or via MCP:

```
fuse_generic(path="/path/to/project", template="MyLanguage")
```

Document the new template in [templates.md](templates.md).

---

## Adding a Content Reducer

### 1. Implement IContentReducer

Create `Fuse.Reduction/Reducers/Implementations/MyLanguageReducer.cs`:

```csharp
using Fuse.Reduction.Options;

namespace Fuse.Reduction.Reducers.Implementations;

/// <summary>
///     Reduces MyLanguage source files by removing comments and condensing whitespace.
/// </summary>
public sealed class MyLanguageReducer : IContentReducer
{
    /// <inheritdoc />
    public string Extension => ".mylang";

    /// <inheritdoc />
    public string Reduce(string content, ReductionOptions options)
    {
        // Apply extension-specific reduction.
        // Do NOT trim lines or collapse blank lines here;
        // ContentReductionPipeline handles whitespace normalization.
        return RemoveComments(content);
    }

    private static string RemoveComments(string content)
    {
        // Implementation
        return content;
    }
}
```

Rules:

- Implement `IContentReducer`
- Mark the class `sealed`
- `Extension` returns the primary extension with leading dot
- `Reduce` receives already-normalized content when `TrimContent`/`UseCondensing` are enabled
- Do not instantiate `ITokenCounter` inside the reducer

If a reducer handles multiple extensions (e.g., `.scss` and `.css`), create separate reducer classes or register one reducer per extension via `ReducerRegistry`.

### 2. Register in DI

Add the reducer to `RegisterContentReducers()` in `ServiceCollectionExtensions.cs`:

```csharp
services.AddSingleton<IContentReducer, MyLanguageReducer>();
```

`ReducerRegistry` builds its extension dictionary from all registered `IContentReducer` instances at startup.

### 3. Add Tests

Create `tests/Fuse.Reduction.Tests/MyLanguageReducerTests.cs`:

```csharp
using Fuse.Reduction.Options;
using Fuse.Reduction.Reducers.Implementations;
using Xunit;

namespace Fuse.Reduction.Tests;

public sealed class MyLanguageReducerTests
{
    private readonly MyLanguageReducer _reducer = new();

    [Fact]
    public void Reduce_RemovesSingleLineComments()
    {
        var input = "code // comment\nmore code";
        var result = _reducer.Reduce(input, new ReductionOptions());
        Assert.DoesNotContain("// comment", result);
    }
}
```

Pass content strings directly. No filesystem access is required.

### 4. Verify

```bash
dotnet test tests/Fuse.Reduction.Tests --filter MyLanguageReducer
```

Run a full fusion against a sample file to confirm the reducer is invoked:

```bash
fuse --directory ./samples --only-extensions .mylang
```

---

## DI Registration Reference

All extension points register in `Fuse.Fusion/Extensions/ServiceCollectionExtensions.cs`:

| Extension Point | Interface | Lifetime | Registration Method |
|-----------------|-----------|----------|---------------------|
| File filter | `IFileFilter` | Transient | `RegisterFileFilters()` |
| Content reducer | `IContentReducer` | Singleton | `RegisterContentReducers()` |
| Project template | `IProjectTemplate` | Singleton | `RegisterProjectTemplates()` |

Filter registration order equals evaluation order. Add new filters at the appropriate position in `RegisterFileFilters()`.

---

## Adding a File Filter

For completeness, the filter extension path follows the same pattern:

1. Create a class implementing `IFileFilter` in `Fuse.Collection/Filters/`
2. Implement `bool Include(FileCandidate candidate, CollectionOptions options)`
3. Register in `RegisterFileFilters()` at the desired evaluation position
4. Add tests in `tests/Fuse.Collection.Tests/` passing `FileCandidate` instances directly

Catch-and-swallow is permitted only when a file read failure should cause exclusion rather than abort. Include an inline comment explaining why.

---

## Coding Standards for Extensions

Follow the project conventions summarized in [contributing.md](contributing.md):

- Target `net10.0` with nullable reference types enabled
- Use explicit constructors, not primary constructors
- Mark concrete classes `sealed`
- Add XML documentation to all public members
- Use file-scoped namespaces
- Propagate `CancellationToken` in async methods

---

## Checklist

Before opening a PR for an extension:

- [ ] Enum value added (templates only)
- [ ] Implementation class created and sealed
- [ ] Registered in `ServiceCollectionExtensions`
- [ ] Unit tests added
- [ ] `dotnet build` and `dotnet test` pass
- [ ] `dotnet format --verify-no-changes` passes
- [ ] [templates.md](templates.md) updated (templates only)
