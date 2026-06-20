---
title: Adding a Format Reducer
description: How to add a content reducer for a markup or configuration file type that is not a programming language.
---

Not every file Fuse handles is source code. A solution carries JSON configuration, YAML pipelines, CSS, HTML, and similar formats, and these also cost tokens. A format reducer shrinks one of these file types the same way a language reducer shrinks code: it strips what does not carry meaning, such as insignificant whitespace, and returns the smaller result. The difference is only one of grouping. Format reducers handle file types that are not programming languages and are registered together rather than as part of a language plugin.

This page is written for an engineer adding reduction for a markup or configuration format Fuse does not yet handle.

## Implementation Context

A format reducer implements the same interface as a language reducer, `IContentReducer`, which extends `ILanguageCapability`. It declares the `SupportedExtensions` it handles, each with its leading dot, and implements a `Reduce` method that takes the file content and the run's reduction options and returns the reduced text. The existing CSS, JSON, and YAML reducers, among others, live in the `Fuse.Plugins.Formats.Web` project and are short enough to read in full as models.

Format reducers and language reducers share one registry, so the same last-registration-wins rule applies: whichever reducer for a given extension is registered last is the one the pipeline calls. The pipeline normalizes whitespace before invoking any reducer and runs reducers concurrently across files, so an implementation must be stateless and must not re-trim or re-collapse whitespace itself.

## Implement The Reducer

The following reducer collapses a hypothetical configuration format whose files end in `.myconf`. It declares its extension and returns the input reduced.

```csharp
using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Abstractions.Reducers;

namespace Fuse.Plugins.Formats.Web.Reducers;

/// <summary>
///     Reduces MyConf configuration files by removing insignificant whitespace.
/// </summary>
public sealed class MyConfReducer : IContentReducer
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".myconf"];

    /// <inheritdoc />
    public string Reduce(string content, ReductionOptions options)
    {
        // Remove only what carries no meaning for this format.
        return content.Trim();
    }
}
```

A reducer must preserve what the file means. Collapsing whitespace is safe between tokens but not inside a string literal, where a space is part of the value, nor in a format where indentation is significant, such as YAML, where the depth of a line determines its place in the structure. Treat string contents and significant whitespace as off limits, and prefer to leave content alone over risking a transformation that changes what the file says. A reducer that corrupts content costs more than the tokens it saves, because the reader can no longer trust the output.

## Registration

Format reducers are registered together through the `AddFormatReducers` extension method, where each is added as a singleton `IContentReducer`. Add the new reducer to that method alongside the existing ones.

```csharp
services.AddSingleton<IContentReducer, MyConfReducer>();
```

The method is called where the host composes its services, next to the language plugin registrations. Once registered, the reducer is resolved automatically for its extensions whenever a file of that type enters reduction.

## What This Does Not Cover

This page covers reducers for non-language formats. A reducer for a programming language belongs with its language plugin and may come with further capabilities such as skeleton extraction and dependency analysis; see [Adding a Language Plugin](language-plugin.md). Which files reach a reducer in the first place is governed by templates, covered in [Adding a Template](template.md).

## Next

The [Reducers reference](../reference/reducers.md) documents each built-in reducer and the transforms it applies. The [contributing guide](../project/contributing.md) covers coding standards and the pull request checklist.
