---
title: Secret Redaction Kinds
description: The seven secret kinds Fuse detects and redacts, how each is matched, and how redaction interacts with token counting and the cache.
---

Secret redaction is on by default and runs before token counting. Fuse scans reduced content for known secret shapes and high-entropy literals, then replaces each match in place with the marker `[REDACTED:<kind>]`, leaving the surrounding code intact. Because redaction precedes counting, the token figure a fusion reports reflects the redacted output, not the original. Detection is best-effort: secrets in unrecognized shapes can slip through, and high-entropy values that are not secrets, such as hashes or base64 blobs, can be redacted as false positives.

This page is for engineers who need to know what Fuse removes before output leaves the machine, agents reading redacted content, and maintainers verifying the detection rules.

## Purpose and Scope

This page documents the seven secret kinds, how each is matched, and the controls and side effects of redaction. It does not cover the threat model or operational guidance for handling secrets, which the [Secret Redaction](../guides/secret-redaction.md) guide covers, nor the full option set, which [Options](options.md) lists.

## Controls

Two flags govern redaction:

- `--no-redact` disables redaction entirely. Content passes through unchanged.
- `--redact-report` appends a count summary to the output, reporting how many matches of each kind were redacted.

Redaction state is part of the reduction cache key. Toggling `--no-redact` therefore produces distinct cache entries: a redacted run and an unredacted run of the same source do not share cached results. The [Watch and Caching](../guides/watch-and-caching.md) guide explains the cache.

## The Kinds

Each kind below is detected by a dedicated rule, except `high-entropy`, which is a heuristic that catches secrets the named rules miss.

| Kind | Detected by |
|------|-------------|
| aws-access-key | An `AKIA`-prefixed key followed by 16 uppercase or digit characters. |
| aws-secret-key | An `aws_secret_access_key` assignment whose value is 40 characters from the base64 alphabet. |
| jwt | Three base64url segments separated by dots, the first beginning `eyJ`. |
| pem-private-key | A PEM private key header line, covering the RSA, EC, OPENSSH, and plain private key variants. |
| connection-string | A `Server`, `Data Source`, `Host`, `User ID`, `Password`, or `Pwd` assignment. |
| api-token | An `api_key`, `api_token`, `secret_key`, or `access_token` assignment whose value is 16 or more characters. |
| high-entropy | A quoted literal of at least 32 characters whose Shannon entropy is at least 4.5 and which contains mixed case, a digit, and a symbol. |

The high-entropy rule is the one most likely to produce false positives, because it matches on statistical shape rather than a known format. It is also the rule that catches credentials the named patterns do not recognize.

## The Marker

A redacted match is replaced with `[REDACTED:<kind>]`, where `<kind>` is the value from the table above. For example, a detected JWT becomes `[REDACTED:jwt]`. For the high-entropy kind, the surrounding quotes are preserved and only the literal content is replaced, so `"a1B2c3...!"` becomes `"[REDACTED:high-entropy]"`. The marker keeps the structure of the code readable while removing the value, so a reader can see that a secret was present and what kind it was.

## What This Does Not Cover

This page does not give guidance on responding to a detected secret, rotating credentials, or treating redaction as a security boundary; the [Secret Redaction](../guides/secret-redaction.md) guide covers operational use. It does not document the report format beyond stating that it counts matches per kind. For the flags named here, see [Options](options.md).

## Next

Read the [Secret Redaction](../guides/secret-redaction.md) guide for operational guidance, or [Options](options.md) for the flags that control redaction.
