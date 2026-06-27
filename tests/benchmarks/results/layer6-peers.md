# Layer 6 results (peer scoper comparison)

On .NET: Fuse (Roslyn, C#-specialized) against an offline graph tool (CodeGraph, tree-sitter) and,
when configured, an offline .NET lexical tool (coa-codesearch-mcp, Lucene). Each tool gets the PR
title as the query and returns a file set, scored against the Layer 2A ground truth (changed_cs).
Recall is read together with tokens. Peer published numbers are ignored; only harness-measured
figures appear here. CodeGraph index build is setup, excluded from the reported token cost.

- Budget: 50000 tokens (fuse --max-tokens; codegraph --max-files 15).
- Arms: fuse, codegraph, coa, serena.
- PRs sampled (12): eShopOnWeb#949, eShopOnWeb#878, eShopOnWeb#876, NodaTime#621, NodaTime#620, NodaTime#571, Scrutor#6, Scrutor#4, Scrutor#2, Specification#502, Specification#196, Specification#188.
- Per-arm sample sizes (rows produced): fuse 12, codegraph 12, coa 4, serena 4.
- Model-driven arms (coa, serena) run one claude rollout per PR via claude-haiku-4-5-20251001; tool-dependent and not byte-reproducible. Bounded to 1 PR(s)/repo when ModelPerRepo is set, so their sample is smaller than the deterministic arms by design (a larger model-driven scale is a separate compute budget).

## Aggregate (mean over sampled PRs)

| Arm | Mean recall | Mean precision | Mean tokens |
|-----|------------:|---------------:|------------:|
| fuse | 19% | 19% | 10,717 |
| codegraph | 9% | 11% | 3,582 |
| coa | 9% | 1% | 3,382 |
| serena | 34% | 27% | 1,538 |

## Per repo (mean recall, mean tokens)

| Repo | fuse recall | codegraph recall | coa recall | serena recall |
|------|------:|------:|------:|------:|
| eShopOnWeb | 12% | 0% | 12% | 12% |
| NodaTime | 36% | 9% | 22% | 22% |
| Scrutor | 7% | 7% | 0% | 100% |
| Specification | 20% | 20% | 0% | 0% |

## How to read this

Recall (and precision) of the returned file set against the change set is the comparable axis across
all three arms. The token columns are NOT directly comparable: fuse returns the reduced source of the
scoped set (a payload the agent can read directly), codegraph explore also returns verbatim source,
but coa returns a ranked path/snippet list (pointers the agent would still have to open), so its token
count is far smaller by construction and does not represent delivered context. The coa arm is also
model-driven (a sonnet-4-6 driver issues one search and reports paths), so its recall blends the tool
with the driver. Fuse has a Roslyn home-field edge on C# structure; where a peer ties or beats it, that
is reported as-is. Sample is small and per-repo rows are shown so a single arm-vs-arm point is not over-read.
