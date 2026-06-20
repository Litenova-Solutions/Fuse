---
title: Watch Mode and Caching
description: How watch mode re-runs fusion on file changes and how the reduction cache avoids repeated work.
---

Two features cut the cost of running Fuse repeatedly against the same project. Watch mode re-runs a fusion automatically when files change, and the reduction cache stores per-file results so unchanged files are not reduced twice. They are independent but complement each other: watch mode triggers runs, and the cache makes each run cheaper. This guide explains both and the flags that control them.

This page is for engineers iterating on a codebase who want fusion output kept current without rerunning by hand, and who want repeat runs to stay fast.

## When This Applies

Watch mode applies to interactive terminal sessions where you edit files and want fresh output after each change. The reduction cache applies to every fusion by default and matters most on large projects where reducing every file from scratch is the bulk of the work.

## Watch Mode

The `--watch` flag keeps Fuse running and re-runs the fusion after file changes settle.

```bash
fuse dotnet --directory ./src --watch
```

Changes are debounced: Fuse waits for a quiet interval of 500 milliseconds with no further filesystem events before it re-runs, so a burst of saves across several files produces one run rather than one per file. Each new event restarts the interval.

Changes inside any `.fuse` directory are ignored, so writing the fusion output and updating the cache does not itself trigger another run.

Watch mode is disabled automatically when standard input or standard output is redirected, which is the case when Fuse runs as an MCP server over stdio. In that mode it runs once and exits rather than watching.

## The Reduction Cache

Reduction is the most expensive stage for most fusions, so Fuse caches the reduced result of each file. The cache is on by default and stores entries under the project's `.fuse/cache` directory.

Each entry is keyed by two hashes: an XXHash64 of the file's raw content and a hash of the reduction options in effect. On a later run, a file whose content and options both match an existing entry is served from the cache instead of being reduced again. The summary line reports how the run split, in the form `cache: N hit / M miss`.

Two flags adjust the cache:

```bash
fuse dotnet --directory ./src --no-cache      # do not read or write the cache
fuse dotnet --directory ./src --clear-cache    # empty the cache, then run
```

The `--no-cache` flag skips the cache entirely for the run. The `--clear-cache` flag deletes existing entries before the run begins, which forces every file to be reduced fresh and the cache to be rebuilt.

## Why The Options Hash Matters

Because the reduction options are part of the cache key, changing how Fuse reduces a file produces a distinct cache entry rather than reusing a stale one. The redaction state and the reduction flags both feed this hash, so a run with `--no-redact`, a run with `--skeleton`, and a default run each cache separately for the same file. This means switching options does not return content reduced under the wrong settings, at the cost of a fresh miss the first time a given combination runs.

| Flag | Effect |
|------|--------|
| (default) | Cache reads and writes under `.fuse/cache` |
| `--no-cache` | Neither read nor write the cache |
| `--clear-cache` | Empty the cache before running |
| `--watch` | Re-run after changes settle; ignored under MCP stdio |

## What This Does Not Cover

This page does not document the on-disk cache layout, the hashing implementation, or the eviction behavior. The [Caching Internals](../architecture/caching-internals.md) page covers the design. For the surrounding flags, see the [Options reference](../reference/options.md).

## Next

Continue to [Redacting Secrets](secret-redaction.md) to understand the redaction state that feeds the cache key, or read [Reducing Tokens](reducing-tokens.md) to see how the reduction options that key the cache change the output.
