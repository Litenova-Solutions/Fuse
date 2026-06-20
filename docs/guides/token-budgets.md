---
title: Token Budgets and Splitting
description: Cap a fusion with a hard token limit or split it into parts, and choose a budget that fits the workflow.
---

A fusion has to fit the context window it is destined for. Fuse gives two controls over size: a hard limit that stops emission when a count is reached, and a split threshold that breaks a large fusion into parts. This guide explains the difference, shows how to size a budget for a given workflow, and covers the reporting that tells you where the tokens went.

This page is for engineers planning a fusion's size against a model's context window, and for anyone budgeting token spend across runs.

## When This Applies

Set a budget whenever a fusion is destined for a model with a fixed window, or when you want to cap the cost of a run. The two controls serve different goals: `--max-tokens` enforces a ceiling by dropping content, while `--split-tokens` keeps all content by dividing it into parts. Both are in the [Options reference](../reference/options.md).

## Hard Limit With Max Tokens

The `--max-tokens` flag is a hard stop: emission halts when the running total reaches the limit, and any remaining files are left out. Because files emit largest first, the largest content lands inside the budget and the smallest is what gets dropped. Use this when a fusion must fit a window no matter what, and partial content is acceptable.

```bash
fuse dotnet --directory ./src --skeleton --max-tokens 100000
```

## Split Into Parts

The `--split-tokens` flag divides output into multiple parts when a part would exceed the threshold, rather than dropping anything. It defaults to 800000. Use it when you want the whole fusion preserved but delivered in pieces a model can read in sequence.

```bash
fuse dotnet --directory ./src --all --split-tokens 200000
```

## See Where The Tokens Went

Files emit largest first, so the most expensive content is at the top of the output and visible before you scroll. To get an explicit accounting, add `--track-top-token-files`, which reports the largest files after the fusion completes. The report tells you which files to scope out or reduce further when a fusion runs over budget.

```bash
fuse dotnet --directory ./src --all --track-top-token-files
```

## Suggested Budgets

A budget depends on the workflow, because each produces a different density of output. These ranges are starting points:

| Workflow | Command shape | Suggested budget |
|----------|---------------|------------------|
| Architecture skeleton | `--skeleton` | 50k to 100k |
| Focus or query scope | `--focus` or `--query` | 100k to 200k |
| Change review | `--changed-since` | 50k to 150k |
| Full reduction | `--all` | 200k to 800k |

## Match The Tokenizer To The Model

A token count is only accurate against the tokenizer that produced it. The `--tokenizer` flag selects the model or encoding Fuse counts with, and it should match the model the fusion is destined for, or the budget you plan against will not match what the model sees. The supported names are in the [Tokenizers reference](../reference/tokenizers.md).

```bash
fuse dotnet --directory ./src --all --tokenizer o200k_base --max-tokens 150000
```

## What This Does Not Cover

This page covers sizing and splitting a fusion. It does not cover reducing the content itself, which is the more direct way to cut tokens; see [Reducing Tokens](reducing-tokens.md). It does not cover narrowing a fusion to fewer files; see [Scoping to What Matters](scoping.md). The output format and how parts are written are in the [Output Specification](../reference/output-specification.md).

## Next

Continue to [Reducing Tokens](reducing-tokens.md) to shrink a fusion that runs over budget, or to [Scoping to What Matters](scoping.md) to include fewer files in the first place.
