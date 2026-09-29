# Security policy

This page says which versions of Fuse get security fixes, what counts as a vulnerability in Fuse, and how to report one privately.

## Supported versions

Security fixes ship in patch releases of the latest 5.x minor version.

## Reporting a vulnerability

Do not open a public GitHub issue for a security report. Report it privately with **Report a vulnerability** on the repository's [Security tab](https://github.com/Litenova-Solutions/Fuse/security), or email info@fuse.codes, with a description of the issue and its impact, the steps to reproduce it, the output of `fuse --version`, and your operating system. We acknowledge a report within 3 business days and agree the timing of disclosure with you.

## Scope

In scope:

- The `fuse` command, its hooks, the per-repository engine process and its named pipe, and the MCP server (`fuse mcp`).
- Code execution, or a file written inside the repository or outside Fuse's own state directory, other than the hook settings `fuse init` writes, `fuse build` (which runs `dotnet build`) and `fuse test` (which runs `dotnet test`).
- Another local user reaching the engine's pipe, which accepts connections from the current user only.
- A command a hook approves on your behalf. Fuse never approves a command it did not rewrite whole. Codex applies a rewritten command only together with an allow decision, so for Codex the pre-shell hook rewrites and approves only a command that is one `dotnet build` or `dotnet test` with plain arguments, and gives no answer for any other command, which then goes through Codex's own approval. The other harnesses get the rewritten command without a decision, so it goes through their normal permission rules.

Out of scope: `fuse test` and `fuse build` run the repository's own build and test code, by design.
