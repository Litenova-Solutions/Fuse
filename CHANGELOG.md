# Changelog

## 5.0.0

Fuse 5 is a rewrite. Compared with 4.4.0 it does one job: it keeps a warm Roslyn compilation of the repository and gives coding agents the errors their edits introduced, affected-test runs and compact build output, through agent hooks and a three-tool MCP server. Nothing is compatible with 4.x; there is no migration path.

### Added

- `fuse init` registers hooks with Claude Code, Cursor, Gemini CLI, Codex and GitHub Copilot CLI, and the MCP server with VS Code, based on the folders the repository already has.
- `fuse hook <harness> <event>`: a post-edit check that reports only new errors, a rewrite of `dotnet build` and `dotnet test` to their Fuse equivalents, and a stop gate that sends the agent back once while its changes leave errors that were not at HEAD.
- `fuse check` reports the errors the working tree has that HEAD did not, including breaks in dependent projects, and runs analyzers only when one of their diagnostics is configured as an error.
- `fuse test` runs only the tests the changes can reach, emitting changed assemblies from memory into a copy of the last build output when that is safe; `fuse test --all` runs everything; with `dotnet test` arguments it runs exactly that scope. Output is failures only.
- `fuse build` runs `dotnet build` and prints only errors.
- `fuse mcp` serves `fuse_check`, `fuse_test` and `fuse_build`.

### Changed

- The engine is one process per repository, started on demand by any command or hook, running from a private copy of its binaries (so it never locks the installed tool) and exiting after 30 idle minutes. It is restarted automatically when a different build of Fuse connects.
- Projects are discovered from every `.csproj` git knows about; there is no solution selection. Compilations load only when a change touches their project.
- Errors are compared with HEAD. Errors that already existed at the last commit are never reported.

### Removed

- The SQLite index (`.fuse/fuse.db`, which can be deleted), retrieval and localization, context packing and reduction, wiring resolution, impact analysis, refactors, review packets, capture bundles, the `fuse host` daemon protocol, self-update, the warm OS service, `fuse up`, `fuse verify`, and every `FUSE_*` environment variable and `fuse.json` setting.
- The v4 MCP tools (`fuse_workspace`, `fuse_find`, `fuse_context`, `fuse_impact`, `fuse_check`, `fuse_test`, `fuse_refactor`, `fuse_review`, `fuse_reduce`), resources and prompts. `fuse_check` and `fuse_test` are new tools with new contracts. Replace `fuse mcp serve` registrations with `fuse mcp`, or run `fuse init`.
- The documentation site and the v4 benchmark suites.
