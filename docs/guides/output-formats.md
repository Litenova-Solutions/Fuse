---
title: Choosing an Output Format
description: How to pick between XML, Markdown, and JSON output, and what each format is suited for.
---

Fuse writes a fusion in one of three formats: XML, Markdown, or JSON. The format controls how files are wrapped and how the manifest renders, but not which files are included or how aggressively they are reduced. This guide explains what each format looks like and which to reach for in a given situation.

This page is for engineers deciding how to consume a fusion, whether that is feeding an agent, reading the output by eye, or parsing it in a script.

## Configuration Context

The format is set with `--format`, which accepts `xml` (the default), `markdown`, or `json`. It can also be set in a configuration file under the `format` key, which the [Configuration Files](configuration.md) guide covers. Every format carries the same manifest and the same reduced file content; only the wrapping differs.

```bash
fuse dotnet --directory ./src --format markdown
```

## XML

XML is the default and the format to use for AI agents and for most chat interfaces. Each file is wrapped in a `<file>` element whose `path` attribute carries the relative path, which gives a model an unambiguous boundary between one file and the next.

```xml
<file path="src/Services/OrderService.cs">
public class OrderService
{
    public Receipt Charge(Order order) => _gateway.Process(order);
}
</file>
```

Use XML when the consumer is a language model, including pasting into a chat. The explicit tags survive truncation and reordering better than heading-based formats, so a model rarely confuses where one file ends.

## Markdown

Markdown is the format for human reading and for pasting into documentation or a pull request description. Each file becomes a heading carrying its path, followed by a fenced code block.

````markdown
## src/Services/OrderService.cs

```csharp
public class OrderService
{
    public Receipt Charge(Order order) => _gateway.Process(order);
}
```
````

Use Markdown when a person is the primary reader, or when the output will live inside another Markdown document such as a design note or a review comment, where fenced code blocks render with syntax highlighting.

## JSON

JSON is the format for programmatic consumption. The output is a single object containing the manifest and an array of file entries, each with its path and content as string fields, so a script can read the fusion without parsing wrapper syntax.

```json
{
  "manifest": { "files": [ { "path": "src/Services/OrderService.cs", "tokens": 142 } ] },
  "files": [
    {
      "path": "src/Services/OrderService.cs",
      "content": "public class OrderService\n{\n    public Receipt Charge(Order order) => _gateway.Process(order);\n}"
    }
  ]
}
```

Use JSON when another program consumes the fusion: a custom indexing step, a report generator, or any tool that needs structured fields rather than free text.

## Adding Metadata And Dropping The Manifest

Two options adjust what each format carries, and they apply regardless of which format is selected.

The `--include-metadata` flag adds each file's size and last-modified time alongside its path. This is useful when the consumer wants to reason about file age or weight, at the cost of a few extra tokens per entry.

The `--no-manifest` flag drops the manifest header that otherwise opens every fusion. Use it when the consumer does not need the orientation layer, for example a script that reads only the file entries.

```bash
fuse dotnet --directory ./src --format json --include-metadata
fuse dotnet --directory ./src --no-manifest
```

| Format | CLI value | Reach for it when |
|--------|-----------|-------------------|
| XML | `xml` (default) | Feeding an agent or pasting into a chat |
| Markdown | `markdown` | A person reads the output or it goes into a Markdown document |
| JSON | `json` | A program parses the output |

## What This Does Not Cover

This page does not document the byte-level structure of each format, the exact manifest fields, or how provenance and structural maps render within a format. The [Output Specification](../reference/output-specification.md) covers all of that. For the full option list, including how metadata fields are named, see the [Options reference](../reference/options.md).

## Next

Continue to [Configuration Files](configuration.md) to set a default format per project, or read [Redacting Secrets](secret-redaction.md) to understand what is removed from file content before it is written.
