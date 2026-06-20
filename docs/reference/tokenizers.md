---
title: Tokenizers
description: How Fuse counts tokens, the encodings it supports, and how the tokenizer flag resolves a model or encoding name.
---

Fuse reports the token count of every fusion so you can judge how much of a model's context window it will consume. It produces that figure with the Microsoft.ML.Tokenizers library using OpenAI-compatible encodings, counting the same tokens the target model counts rather than estimating from character length. The count Fuse reports therefore matches what the model sees when you paste the output into it.

This page is for engineers who want the reported count to match a specific model and maintainers who need the exact resolution rules.

## Purpose And Scope

Fuse counts tokens during the Emission stage, after reduction. The encoding it uses determines the count, because different encodings tokenize the same text differently. The default encoding is `o200k_base`, the encoding used by the current generation of OpenAI models. You select a different encoding with the `--tokenizer` flag, which accepts either a model name or an encoding name. See the [Options reference](options.md).

This page documents how a tokenizer value resolves to an encoding. It does not cover the manifest or the reported summary line, which the [Core Concepts](../getting-started/core-concepts.md) page describes.

## Resolution Rules

The `--tokenizer` value resolves to an encoding by the following rules, applied in order. Matching is case-insensitive and surrounding whitespace is trimmed.

| Input | Resolves to |
|-------|-------------|
| `o200k_base` | `o200k_base` |
| `gpt-4o` | `o200k_base` |
| `gpt-4o-mini` | `o200k_base` |
| `gpt-4.1` | `o200k_base` |
| `gpt-4.1-mini` | `o200k_base` |
| `cl100k_base` | `cl100k_base` |
| `gpt-4` | `cl100k_base` |
| `gpt-3.5-turbo` | `cl100k_base` |
| `gpt-3.5` | `cl100k_base` |
| Any value containing an underscore | Used as-is, treated as an encoding name |
| Anything else | `o200k_base` (the default) |

Two consequences follow from these rules. A value containing an underscore is passed through unchanged on the assumption that it names an encoding, so a misspelled encoding name is used verbatim rather than corrected. Any value that matches no known alias and contains no underscore falls back to the default `o200k_base` rather than raising an error.

## Choosing An Encoding

Choose the encoding that matches the model you will paste the fusion into. If you target a current OpenAI model, the default `o200k_base` is correct and no flag is needed. If you target an older model in the GPT-4 or GPT-3.5 family, pass `cl100k_base` or one of its model aliases so the reported count matches that model. Counts produced under one encoding are an approximation for a model that uses a different one.

## What This Does Not Cover

This page covers token counting and encoding resolution. It does not cover the token budget, the split threshold, or how the manifest reports per-file token costs; those are part of the Emission stage and are documented in the [Output Specification](output-specification.md).

## Next

See the [Options reference](options.md) for the `--tokenizer` flag and related budget options, or [Core Concepts](../getting-started/core-concepts.md) for where token counting sits in the pipeline.
