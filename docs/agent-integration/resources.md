---
title: Resources Reference
description: The fuse:// URI templates an AI client can read for codebase context using default options.
---

The Fuse MCP server exposes its fusion output as resources in addition to tools. MCP (Model Context Protocol) is the open protocol that lets an AI client call external tools; a resource is the passive counterpart, a read addressed by a URI rather than a call with arguments. A client reads a `fuse://` URI and receives the fused content produced with default options. This page documents the five URI templates and what each returns.

This page is for engineers wiring resource reads into a client and for anyone deciding between a resource and the equivalent tool.

## Purpose and Scope

Resources run their fusion in memory and return the result as the resource content. They use default options: no token limit and no reduction flags beyond the defaults each scope applies. When a task needs a token limit, reduction control, or exclusions, prefer the equivalent tool in [Tools Reference](tools.md), which accepts those parameters. Use a resource when the default behavior is sufficient and a URI is more convenient than a tool call.

In the templates below, `{path}` is the source directory to fuse and the remaining segments supply the scope.

## URI Templates

| URI template | Returns |
|--------------|---------|
| `fuse://skeleton/{path}` | A structural skeleton overview, signatures without bodies |
| `fuse://focus/{path}/{seed}` | Content scoped to the seed type, file, or path plus its dependencies |
| `fuse://search/{path}/{query}` | Content scoped to the files a query ranks highest plus dependencies |
| `fuse://changes/{path}/{since}` | Content for files changed since the given git ref plus dependents |
| `fuse://{template}/{path}` | Template-based fusion using the named template |

For the template URI, `{template}` is a template name such as `dotnet`, `python`, `generic`, or `azuredevopswiki`. All five resources return content with the MIME type `text/plain`.

## What This Does Not Cover

This page documents the resource URIs and their default behavior. It does not cover the parameters that let you set token limits or reduction flags, which are available only on the tools. See [Tools Reference](tools.md) for those.

## Next

Continue to [Tools Reference](tools.md) when you need token limits, reduction flags, or exclusion control on a read.
