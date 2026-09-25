# Changelog

## 5.0.0

Fuse keeps a warm Roslyn compilation of a .NET repository and gives coding agents the compiler errors their edits introduced, affected-test runs, and compact build output, through agent hooks and a three-tool MCP server.

- `fuse init` registers hooks with Claude Code, Cursor, Gemini CLI, Codex and GitHub Copilot CLI, a plugin with OpenCode, and the MCP server with VS Code, for the harnesses the repository uses.
- `fuse hook <harness> <event>` runs a post-edit check that reports only new errors, rewrites `dotnet build` and `dotnet test` to their Fuse equivalents, and runs a stop check that sends the agent back once while its changes leave errors that were not at HEAD.
- `fuse check` reports the errors the working tree has that HEAD did not, including breaks in dependent projects, with analyzers that can report an error.
- `fuse test` runs the tests the changes can reach, from a shadow copy of the last build with changed assemblies emitted in memory when that is safe; `fuse test --all` runs everything; with `dotnet test` arguments it runs exactly that scope. Output lists failures only.
- `fuse build` runs `dotnet build` and prints only errors.
- `fuse mcp` serves `fuse_check`, `fuse_test` and `fuse_build`.
- One engine runs per repository, starts on demand, runs from a private copy of its binaries, and exits after 30 idle minutes. Nothing is configured and nothing is written inside the repository except the hook settings `fuse init` creates.
