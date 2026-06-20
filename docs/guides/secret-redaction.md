---
title: Redacting Secrets
description: How Fuse removes detected secrets from file content before counting tokens, and the flags that control it.
---

Fuse scans file content for secrets and removes them before the content is counted or written. Redaction is on by default, so a fusion produced with no extra flags has already had detected secrets replaced. This guide explains what redaction does, what it detects, and the two flags that change its behavior.

This page is for engineers who share fusion output, whether with an agent, a colleague, or a public channel, and need to know what leaves their machine.

## When This Applies

Redaction runs during the reduction stage, before token counting. It reads each file's content, replaces any detected secret with a placeholder of the form `[REDACTED:<kind>]`, and leaves the surrounding code untouched. Because it runs before counting, the token figure Fuse reports reflects the redacted content, not the original.

A redacted file keeps its shape. Only the secret value is replaced, so a connection string becomes a line with `[REDACTED:ConnectionString]` in place of the credential while the assignment and the rest of the file remain readable.

```csharp
var connection = "[REDACTED:ConnectionString]";
```

## What It Detects

Redaction recognizes several categories of secret:

- AWS access keys
- JSON Web Tokens (JWTs)
- PEM private keys
- Connection strings
- API tokens
- High-entropy string literals

Detection is best-effort. It uses pattern and entropy heuristics rather than a definitive parse, so it can both flag a value that is not a secret and miss one that is. Treat redaction as a strong default that reduces exposure, not as a guarantee that output is free of credentials. Review output before sharing it where the cost of a leak is high.

## Disabling Redaction

The `--no-redact` flag turns redaction off, so file content is written exactly as read.

```bash
fuse dotnet --directory ./src --no-redact
```

Use this only when you are certain the source contains no live secrets, for example a sample project, and never on output you intend to share, paste into a chat, or commit. Disabling redaction also changes the cache key, which the [Watch Mode and Caching](watch-and-caching.md) guide explains.

## Reporting What Was Redacted

The `--redact-report` flag appends a summary to the output stating how many secrets of each kind were replaced. This is useful for confirming that redaction acted on a fusion and for auditing how much was removed.

```bash
fuse dotnet --directory ./src --redact-report
```

| Flag | Effect |
|------|--------|
| (default) | Redaction enabled; detected secrets replaced with `[REDACTED:<kind>]` |
| `--no-redact` | Disable redaction; content written as read |
| `--redact-report` | Append a count summary of what was redacted |

## What This Does Not Cover

This page does not list every redaction kind or the exact pattern behind each one. The [Secret Redaction Kinds reference](../reference/secret-redaction-kinds.md) documents the full set and what each placeholder label means. For the surrounding option set, see the [Options reference](../reference/options.md).

## Next

Continue to [Choosing an Output Format](output-formats.md) to control how the redacted content is written, or read [Watch Mode and Caching](watch-and-caching.md) to understand how redaction state affects the reduction cache.
