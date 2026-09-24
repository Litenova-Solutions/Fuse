# Security policy

## Supported versions

Security fixes ship in patch releases of the latest 5.x minor version.

## Reporting a vulnerability

Do not open a public GitHub issue for security reports. Email security@litenova.com with a description of the issue and its impact, steps to reproduce, and the output of `fuse --version` and your OS. We acknowledge reports within 3 business days and coordinate disclosure timing with you.

## Scope

In scope:

- The `fuse` CLI, its hooks, the per-repository engine process and its named pipe, and the MCP server (`fuse mcp`).
- Code execution, or file writes inside the repository or outside Fuse's own state directory, other than the hook settings `fuse init` writes, `fuse build` (which runs `dotnet build`) and `fuse test` (which runs `dotnet test`).
- Another local user reaching the engine's pipe (it is created current-user-only).

Out of scope: running `fuse test` or `fuse build` executes the repository's own build and test code, by design.
