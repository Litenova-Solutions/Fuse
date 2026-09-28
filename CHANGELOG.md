# Changelog

## 5.1.0

- A project with a restore warning, such as a NuGet advisory, is no longer reported as a project Fuse could not load, so Fuse answers in repositories whose build logs a warning.
- Every request the engine serves is named in `engine.log` and carries a line of per-phase times, so a check or test round can be attributed to the call that caused it.
- A hook and the MCP server read their input as UTF-8, so a repository whose path or file names are not ASCII is checked from a harness whose console uses another code page. Before this, a post-edit hook attached to a Windows console read the payload as that code page, failed to parse it, and told the agent its edit was clean.
- Git's file list and status are read NUL-separated, so a file whose name holds a quote, a backslash or a control character is found and checked under the name it has.
- `fuse build` and `fuse test` take a per-repository lock for as long as they write build output, so two agents building or testing at once no longer make MSBuild fail on a file the other is holding. A client that has to wait says so once, on standard error. The arguments `fuse build` passes to `dotnet build`, including an implicit restore, are unchanged.
- An error in a file the agent did not edit is followed by one line naming the changed declaration that put that file in scope, so a break in a dependent project no longer has to be traced back by hand. At most ten such lines per answer, and the summary says how many were left out.
- Checks that arrive together are answered together, so a second agent's check no longer waits for the first agent's whole check, dependent projects and all. Each agent still gets the answer a check on its own would have given it: its own files, the breaks its own change reaches, and its own scope line. A test plan is never batched.

## 5.0.0

Fuse keeps a warm Roslyn compilation of a .NET repository and gives coding agents the compiler errors their edits introduced, affected-test runs, and compact build output, through agent hooks and a three-tool MCP server.

- `fuse init` registers hooks with Claude Code, Cursor, Gemini CLI, Codex and GitHub Copilot CLI, a plugin with OpenCode, and the MCP server with VS Code, for the harnesses the repository uses.
- `fuse hook <harness> <event>` runs a post-edit check that reports only new errors, rewrites `dotnet build` and `dotnet test` to their Fuse equivalents, and runs a stop check that sends the agent back once while its changes leave errors that were not at HEAD.
- `fuse check` reports the errors the working tree has that HEAD did not, including breaks in dependent projects, with analyzers that can report an error.
- `fuse test` runs the tests the changes can reach, from a shadow copy of the last build with changed assemblies emitted in memory when that is safe; `fuse test --all` runs everything; with `dotnet test` arguments it runs exactly that scope. Output lists failures only.
- `fuse build` runs `dotnet build` and prints only errors.
- `fuse mcp` serves `fuse_check`, `fuse_test` and `fuse_build`.
- One engine runs per repository, starts on demand, runs from a private copy of its binaries, and exits after 30 idle minutes. Nothing is configured and nothing is written inside the repository except the hook settings `fuse init` creates.
