---
title: Reducers
description: The per-type content reducers that shrink each file by its format, resolved by file extension.
---

A reducer is a capability that shrinks a file's content according to its type. During the Reduction stage, Fuse resolves a reducer for each file by its extension and applies that reducer's transforms to the file's text. A reducer changes how content is written, not what it means: the output represents the same source in fewer tokens. Reducers fall into two groups, the format reducers for web and configuration files and the C# reducer for `.cs` source.

This page is for engineers who want to know exactly which transforms run on a given file type and maintainers reasoning about token output.

## Purpose And Scope

Each reducer declares the extensions it handles. When the Reduction stage processes a file, it looks up the reducer registered for that file's extension and applies it. A file whose extension has no registered reducer passes through with whitespace normalization only.

This page documents the transforms each reducer performs and the options that turn them on or off. It does not cover skeleton extraction or secret redaction, which are separate Reduction-stage steps described in [Core Concepts](../getting-started/core-concepts.md).

## Format Reducers

The format reducers handle web and configuration file types. Each strips comments and collapses whitespace in a way appropriate to its syntax. They are controlled by two options: `--minify-xml-files` governs the XML-family reducers and `--minify-html-and-razor` governs the HTML and Razor reducers; both default to true. See the [Options reference](options.md).

| Reducer | Extensions | Transforms |
|---------|------------|------------|
| CssReducer | `.css` | Removes block comments; collapses newlines; removes spaces around `{ } : ; ,`; collapses multiple spaces. |
| HtmlReducer | `.html` `.htm` | Removes HTML comments; collapses whitespace between tags; removes quotes from safe attribute values; collapses spaces. |
| JavaScriptReducer | `.js` `.ts` `.tsx` `.jsx` `.mjs` `.cjs` `.mts` `.cts` | Removes line and block comments; collapses double newlines; trims line whitespace; collapses spaces around delimiters. Covers TypeScript and JSX/TSX and ESM variants alongside plain JavaScript. |
| JsonReducer | `.json` | Removes newlines; collapses whitespace around `:` and `,`; removes spaces after `[` and `{` and before `]` and `}`. |
| MarkdownReducer | `.md` | Removes HTML comments; converts underline headings to ATX form; removes horizontal rules; normalizes pipe spacing and link titles; collapses excess newlines. |
| RazorReducer | `.razor` `.cshtml` | Removes HTML, block, line, and Razor comments; collapses tag whitespace; normalizes `@()` syntax; collapses spaces. |
| ScssReducer | `.scss` | Removes line and block comments; collapses newlines; removes spaces around `{ } : ; ,`; collapses spaces. |
| SqlReducer | `.sql` | Removes line and block comments; collapses blank lines. |
| XmlReducer | `.xml` `.csproj` `.targets` `.props` | Removes XML comments; collapses tag whitespace; normalizes the declaration; removes interior newlines; collapses spaces. |
| YamlReducer | `.yaml` `.yml` | Removes comment lines; removes trailing whitespace; collapses three or more newlines to two. |

## C# Reducer

The C# reducer handles `.cs` files. Unlike the format reducers, its transforms are individually configurable through reduction options, so the same reducer produces output ranging from light cleanup to compressed source. Its steps fall into two tiers.

The standard removals each have their own flag and run when enabled:

| Removal | Removes | Flag |
|---------|---------|------|
| Comments | Line and block comments, preserving string-literal contents | `--remove-csharp-comments` |
| Preprocessor and region directives | `#region` and `#endregion` markers, and other preprocessor directives | `--remove-csharp-regions` |
| Using statements | `using` directives and `using` aliases | `--remove-csharp-usings` |
| Namespace wrappers | File-scoped and block namespace declarations, and the indentation a block namespace adds | `--remove-csharp-namespaces` |

Aggressive mode, enabled with `--aggressive`, runs the standard removals and then applies a second tier:

- Removes noise attributes such as `DebuggerDisplay`, `MethodImpl`, `ExcludeFromCodeCoverage`, and the assembly-info attributes.
- Removes assembly-level `SuppressMessage` attributes.
- Removes the `this.` prefix on member access.
- Rewrites auto-properties to compact form, for example `{ get; set; }` becomes `{get;set;}`.
- Collapses whitespace around delimiters such as `{ } ; , : ( ) = [ ]`, while preserving the contents of string literals through placeholder substitution so literal text is never altered.

Aggressive mode maximizes token savings but produces output that is no longer guaranteed to compile. The `--all` flag enables the full standard removal set together with aggressive mode in one switch.

## What This Does Not Cover

This page documents what each reducer does, not how to write a new one. The [Adding A Format Reducer](../extending/format-reducer.md) page covers registering a reducer for a new extension. For guidance on which combination of flags to use for a given goal, see the [Reducing Tokens](../guides/reducing-tokens.md) guide.

## Next

See the [Options reference](options.md) for the full flag set, or the [Reducing Tokens](../guides/reducing-tokens.md) guide for the reduction levels in practice.
