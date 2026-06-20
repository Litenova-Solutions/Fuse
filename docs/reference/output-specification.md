---
title: Output Specification
description: The exact structure of the three Fuse output formats and the manifest, including how metadata, provenance, and git statistics appear.
---

A fusion produces one of three output formats, each carrying the same content with a different structure: XML, Markdown, or JSON. Every format begins with a manifest, followed by one entry per included file. This page documents the precise shape of each format and the manifest so that a tool, an agent, or a person can parse the output without inspecting the source.

This page is for engineers writing parsers against Fuse output, agents consuming a fusion, and anyone who needs to know exactly what a flag adds to the result.

## Purpose and Scope

This page specifies the on-disk and in-memory structure of a fusion: the per-file entry for each format, the manifest block, and the additions that the metadata, provenance, git statistics, and pattern summary options introduce. It states the exact text and field names Fuse emits. It does not cover which flag to choose for a given task; the [Output Formats](../guides/output-formats.md) guide covers that, and [Options](options.md) lists every flag.

## File Entries

The body of a fusion is a sequence of file entries. Files are emitted largest first by token count, so the most expensive content appears at the top of the body.

### XML (Default)

Each file is wrapped in a `file` element whose `path` attribute holds the normalized path. The reduced content sits between the tags:

```xml
<file path="src/Services/OrderService.cs">
public class OrderService { }
</file>
```

With `--include-metadata`, the opening tag gains two attributes: `size` in bytes and `modified` as an ISO 8601 UTC timestamp:

```xml
<file path="src/Services/OrderService.cs" size="1843" modified="2026-06-19T01:30:00.0000000Z">
```

With `--provenance`, a comment precedes the entry, listing the inclusion chain from seed to the file:

```xml
<!-- included via: OrderService -> IPaymentGateway -> StripeGateway -->
```

The provenance comment is emitted only when the inclusion chain has more than one entry. A file that is itself a seed has no chain to show and receives no comment.

### Markdown

With `--format markdown`, each file is a level-three heading holding the path, followed by a fenced code block with the content:

````markdown
### src/Services/OrderService.cs
```
public class OrderService { }
```
````

With `--include-metadata`, an italic line follows the heading:

```markdown
*size: 1843, modified: 2026-06-19T01:30:00.0000000Z*
```

Provenance is the same HTML comment as in XML, emitted before the heading under the same more-than-one-entry rule.

### JSON

With `--format json`, each file is a JSON object on its own line with these fields:

| Field | Type | Present when |
|-------|------|--------------|
| type | string, always `"file"` | Always |
| path | string | Always |
| content | string | Always |
| tokens | number | Always |
| size | number | `--include-metadata` |
| modified | string, ISO 8601 UTC | `--include-metadata` |
| provenance | array of strings | `--provenance` and chain length greater than one |

```json
{"type":"file","path":"src/Services/OrderService.cs","content":"public class OrderService { }","tokens":7}
```

## The Manifest

The manifest is on by default and prepended before the file entries. It lists every included file with its token cost so that a reader can judge the shape and size of a fusion before reading any file body. Disable it with `--no-manifest`.

Token counts in the manifest are abbreviated: a count of 1000 or more is shown as `Nk` (for example `1.5k`), and smaller counts are shown in full.

### XML and Markdown Manifest

In both formats the manifest is an HTML comment block. It opens with `<!-- fuse:manifest`, lists the file count, then one line per file, and closes with `-->`:

```
<!-- fuse:manifest
files: 3
  src/Program.cs (~420 tokens)
  src/Services/OrderService.cs (~1.2k tokens)
  src/Services/PaymentService.cs (~890 tokens)
-->
```

With `--git-stats`, each file line gains a trailing churn summary in the form `[commits:N last:YYYY-MM-DD]`:

```
  src/Services/OrderService.cs (~1.2k tokens) [commits:14 last:2026-05-30]
```

With `--pattern-summary`, one summary line per detected pattern is appended after the file list. The [Pattern Detectors](pattern-detectors.md) reference documents what each line reports.

### JSON Manifest

In JSON the manifest is an object with type `"manifest"`. Its `files` array holds one object per file with `path` and `tokens`, plus `commits` and `lastModified` when git statistics are available. A `patterns` array carries detected patterns, and a git availability field reports whether git statistics could be produced:

```json
{
  "type": "manifest",
  "files": [
    {"path": "src/Services/OrderService.cs", "tokens": 1200, "commits": 14, "lastModified": "2026-05-30"}
  ],
  "patterns": [],
  "git": "unavailable"
}
```

## What This Does Not Cover

This page does not explain the reduction that produces the content inside each entry, nor the token budget and part-splitting that govern how much is written. It documents structure only. For when to choose each format, see the [Output Formats](../guides/output-formats.md) guide. For the flags named here, see [Options](options.md).

## Next

Read [Pattern Detectors](pattern-detectors.md) for the content of pattern summary lines, or the [Output Formats](../guides/output-formats.md) guide to choose a format for a task.
