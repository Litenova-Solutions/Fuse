# Layer 6 results (peer scoper comparison)

On .NET: Fuse (Roslyn, C#-specialized) against an offline graph tool (CodeGraph, tree-sitter) and,
when configured, an offline .NET lexical tool (coa-codesearch-mcp, Lucene). Each tool gets the PR
title as the query and returns a file set, scored against the Layer 2A ground truth (changed_cs).
Recall is read together with tokens. Peer published numbers are ignored; only harness-measured
figures appear here. CodeGraph index build is setup, excluded from the reported token cost.

- Budget: 50000 tokens (fuse --max-tokens; codegraph --max-files 15).
- Arms: fuse, codegraph.
- PRs sampled (6): AutoMapper#4608, eShopOnWeb#949, FluentValidation#2158, MediatR#1171, NewtonsoftJson#1159, Serilog#1452.

## Aggregate (mean over sampled PRs)

| Arm | Mean recall | Mean precision | Mean tokens |
|-----|------------:|---------------:|------------:|
| fuse | 21% | 7% | 19,753 |
| codegraph | 12% | 10% | 4,456 |

## Per repo (mean recall, mean tokens)

| Repo | fuse recall | codegraph recall |
|------|------:|------:|
| AutoMapper | 0% | 0% |
| eShopOnWeb | 12% | 0% |
| FluentValidation | 11% | 22% |
| MediatR | 100% | 50% |
| NewtonsoftJson | 0% | 0% |
| Serilog | 0% | 0% |

## How to read this

Recall (and precision) of the returned file set against the change set is the comparable axis across
all three arms. The token columns are NOT directly comparable: fuse returns the reduced source of the
scoped set (a payload the agent can read directly), codegraph explore also returns verbatim source,
but coa returns a ranked path/snippet list (pointers the agent would still have to open), so its token
count is far smaller by construction and does not represent delivered context. The coa arm is also
model-driven (a sonnet-4-6 driver issues one search and reports paths), so its recall blends the tool
with the driver. Fuse has a Roslyn home-field edge on C# structure; where a peer ties or beats it, that
is reported as-is. Sample is small and per-repo rows are shown so a single arm-vs-arm point is not over-read.
