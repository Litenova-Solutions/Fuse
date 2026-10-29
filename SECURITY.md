# Security policy

Fuse ships security fixes for its supported versions. If you found a vulnerability, this page tells you which versions those are, what counts as a vulnerability in Fuse, and how to report it privately.

## Supported versions

Security fixes ship in patch releases of the latest 5.x minor version. Versions before 5.0.0 are deprecated and get no fixes. The [Changelog](https://fuse.codes/docs/changelog#supported-versions) lists the status of each version.

## Reporting a vulnerability

Do not open a public GitHub issue for a security report. Report it privately through **Report a vulnerability** on the repository's [Security tab](https://github.com/Litenova-Solutions/Fuse/security), or by email to info@fuse.codes. Include in your report:

- A description of the issue and its impact.
- The steps to reproduce it.
- The output of `fuse --version`.
- Your operating system.

We acknowledge your report within 3 business days. We agree the timing of disclosure with you.

## Scope

In scope:

- The `fuse` command, its hooks, the per-repository engine process and its named pipe, and the MCP server (`fuse mcp`).
- Code execution, or a file written inside the repository or outside Fuse's own state directory. This excludes the hook settings and the `.gitignore` entry `fuse init` writes, `fuse build` (which runs `dotnet build`), and `fuse test` (which runs `dotnet test`).
- A connection to the engine's pipe from another local user. The pipe accepts connections from the current user only.
- A command a hook approves on your behalf. Fuse never approves a command it did not rewrite whole. Codex applies a rewritten command only together with an allow decision. For Codex, the pre-shell hook therefore rewrites and approves only a command that is one `dotnet build` or `dotnet test` with plain arguments, and gives no answer for any other command. Such a command then goes through Codex's own approval. The other harnesses get the rewritten command without a decision, so it goes through your normal permission rules.

Out of scope: `fuse test` and `fuse build` run the repository's own build and test code, by design.
