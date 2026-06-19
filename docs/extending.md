# Extending Fuse

Fuse 2.0 extends through language plugins and project templates. Language plugins register capabilities by file extension. Templates define which extensions and directories a subcommand includes by default.

This guide covers both paths. For a feature overview, see [features.md](features.md).

---

## Language plugin model

A language plugin is a .NET assembly that registers one or more capabilities via a DI extension method:

```csharp
public static IServiceCollection AddMyLanguage(this IServiceCollection services)
{
    services.AddSingleton<IContentReducer, MyLanguageReducer>();
    services.AddSingleton<IDependencyExtractor, MyLanguageDependencyExtractor>();
    // ... other capabilities as needed
    return services;
}
```

Wire the plugin in `ServiceCollectionExtensions.AddFuse()`:

```csharp
services.AddFuseCore();
services.AddCSharpLanguage();
services.AddFormatReducers();
services.AddMyLanguage();   // your plugin
```

### ILanguageCapability

All capability interfaces extend `ILanguageCapability`:

```csharp
public interface ILanguageCapability
{
    IReadOnlyCollection<string> SupportedExtensions { get; }
}
```

Declare every extension your capability handles, including the leading dot. A single class can handle multiple extensions (e.g., `.ts` and `.tsx`).

### CapabilityRegistry

At startup, `CapabilityRegistry<TCapability>` builds an extension-to-implementation map from all registered capabilities. Resolution:

```csharp
var reducer = registry.TryResolve(".cs");  // returns CSharpReducer or null
```

Last registration wins for a duplicate extension, allowing a specialized plugin to override a default reducer.

Five registries exist in the core pipeline:

| Registry | Interface | Used by |
|----------|-----------|---------|
| Reducers | `IContentReducer` | `ContentReductionPipeline` |
| Skeleton | `ISkeletonExtractor` | `ContentReductionPipeline` |
| Markers | `ISemanticMarkerGenerator` | `ContentReductionPipeline` |
| Dependencies | `IDependencyExtractor` | `DependencyGraphBuilder` |
| Type names | `ITypeNameLocator` | `FocusSeedResolver`, `DependencyGraphBuilder` |

Optional map generators (`IRouteMapGenerator`, `IProjectGraphGenerator`) register as singletons and are resolved directly by the orchestrator, not through a registry.

---

## Adding a language plugin

### 1. Create the project

```
src/Fuse.Languages.MyLang/
  Fuse.Languages.MyLang.csproj   -> references Fuse.Languages.Abstractions
  Reducers/MyLangReducer.cs
  Dependencies/MyLangDependencyExtractor.cs
  Extensions/MyLangLanguageServiceCollectionExtensions.cs
```

Reference `Fuse.Languages.Abstractions` only. Do not reference `Fuse.Reduction` or `Fuse.Fusion` from the plugin assembly.

### 2. Implement IContentReducer

```csharp
using Fuse.Languages.Abstractions.Reducers;
using Fuse.Languages.Abstractions.Options;

namespace Fuse.Languages.MyLang.Reducers;

/// <summary>
///     Reduces MyLang source files.
/// </summary>
public sealed class MyLangReducer : IContentReducer
{
    public IReadOnlyCollection<string> SupportedExtensions => [".mylang"];

    public string Reduce(string content, ReductionOptions options)
    {
        // Extension-specific reduction only.
        // Do NOT trim lines or collapse blank lines;
        // ContentReductionPipeline handles whitespace normalization.
        return RemoveComments(content);
    }

    private static string RemoveComments(string content) => content;
}
```

Rules:

- Mark concrete classes `sealed`
- Do not instantiate token counters inside reducers
- `Reduce` receives already-normalized content when `TrimContent`/`UseCondensing` are enabled

### 3. Implement optional capabilities

Add only what your language needs:

| Capability | When to implement |
|------------|-------------------|
| `ISkeletonExtractor` | Agentic skeleton mode for your language |
| `ISemanticMarkerGenerator` | Type-level annotation comments |
| `IDependencyExtractor` | Focus/query/change dependency expansion |
| `ITypeNameLocator` | Resolve type names to defining files |
| `IPatternDetector` | Cross-codebase convention detection |

Each follows the same pattern: implement the interface, declare `SupportedExtensions`, register as singleton.

Example dependency extractor:

```csharp
public sealed class MyLangDependencyExtractor : IDependencyExtractor
{
    public IReadOnlyCollection<string> SupportedExtensions => [".mylang"];

    public IReadOnlyList<string> ExtractDependencies(string content, string filePath)
    {
        // Return referenced type or module names (best-effort).
        return [];
    }
}
```

### 4. Register via extension method

```csharp
public static class MyLangLanguageServiceCollectionExtensions
{
    public static IServiceCollection AddMyLangLanguage(this IServiceCollection services)
    {
        services.AddSingleton<IContentReducer, MyLangReducer>();
        services.AddSingleton<IDependencyExtractor, MyLangDependencyExtractor>();
        return services;
    }
}
```

### 5. Add tests

Create `tests/Fuse.Languages.MyLang.Tests/` with unit tests that pass content strings directly. No filesystem access required for reducer tests.

```bash
dotnet test tests/Fuse.Languages.MyLang.Tests
```

### 6. Verify end to end

```bash
fuse --directory ./samples --only-extensions .mylang
```

Or via MCP:

```
fuse_generic(path="./samples", template="Generic", onlyExtensions=[".mylang"])
```

---

## Adding format reducers

Non-language-specific format reducers (HTML, JSON, YAML) live in `Fuse.Formats`. They implement `IContentReducer` the same way language reducers do.

To add a format reducer:

1. Create `Fuse.Formats/Reducers/MyFormatReducer.cs` implementing `IContentReducer`
2. Register in `FormatReducersServiceCollectionExtensions.AddFormatReducers()`

Format reducers and language reducers share the same `CapabilityRegistry<IContentReducer>`. Register order determines override behavior for overlapping extensions.

---

## Adding a project template

Templates define default extensions and exclusions for a project type. They do not implement reduction logic.

### 1. Add an enum value

In `Fuse.Collection/Models/ProjectTemplate.cs`:

```csharp
/// <summary>
///     Template for MyLanguage projects.
/// </summary>
MyLanguage,
```

### 2. Create the template class

```csharp
public sealed class MyLanguageTemplate : ProjectTemplateBase
{
    public override string Name => nameof(ProjectTemplate.MyLanguage);

    public override IReadOnlyCollection<string> Extensions =>
        [".mylang", ".mylproj", ".yaml"];

    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["build", "dist", ".git", "vendor"];

    public override IReadOnlyCollection<string> ExcludePatterns =>
        ["*.generated.mylang"];
}
```

Rules:

- Extend `ProjectTemplateBase`, mark `sealed`
- `Name` must match the enum member name exactly
- Extensions include the leading dot

### 3. Register in DI

In `RegisterProjectTemplates()`:

```csharp
services.AddSingleton<IProjectTemplate, MyLanguageTemplate>();
```

`ProjectTemplateRegistry` discovers templates automatically.

### 4. Document and test

Update [templates.md](templates.md). Run:

```bash
dotnet build Fuse.sln
dotnet test Fuse.sln
```

---

## Templates and capability registries

Templates control which files enter the pipeline. Capability registries control how those files are processed.

A template can include extensions for which no capability is registered. Those files pass through with whitespace normalization only. The CLI emits a warning when skeleton mode is requested but no `ISkeletonExtractor` exists for the template's extensions:

```
Warning: Skeleton mode is requested but no skeleton extractor is registered
for this template's file types.
```

When adding a new language, register both a template (for discovery defaults) and a language plugin (for reduction and analysis).

---

## Adding a file filter

For collection-stage filtering:

1. Create a class implementing `IFileFilter` in `Fuse.Collection/Filters/`
2. Implement `bool Include(FileCandidate candidate, CollectionOptions options)`
3. Register in `RegisterFileFilters()` at the desired evaluation position
4. Add tests in `tests/Fuse.Collection.Tests/`

Filter registration order equals evaluation order.

Catch-and-swallow is permitted only when a file read failure should cause exclusion rather than abort. Include an inline comment explaining why.

---

## DI registration reference

All extension points register in `Fuse.Fusion/Extensions/ServiceCollectionExtensions.cs`:

| Extension point | Interface | Lifetime | Registration |
|-----------------|-----------|----------|--------------|
| Language capabilities | `IContentReducer`, etc. | Singleton | `Add{Language}Language()` |
| Format reducers | `IContentReducer` | Singleton | `AddFormatReducers()` |
| File filter | `IFileFilter` | Transient | `RegisterFileFilters()` |
| Project template | `IProjectTemplate` | Singleton | `RegisterProjectTemplates()` |

---

## Checklist

Before opening a PR:

- [ ] Capability classes implement `ILanguageCapability` with correct `SupportedExtensions`
- [ ] Registered in a dedicated `Add{Language}Language()` extension method
- [ ] Wired into `AddFuse()` in `ServiceCollectionExtensions`
- [ ] Unit tests added (reducer at minimum)
- [ ] Template enum and class added (if new project type)
- [ ] [templates.md](templates.md) updated (templates only)
- [ ] `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass

Coding standards: [contributing.md](contributing.md).
