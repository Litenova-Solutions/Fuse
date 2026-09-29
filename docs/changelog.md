# Changelog

## 5.1.0

Compared with 5.0.0. After updating, run `fuse init` again in each repository: the shell hook event is `pre-shell`, and 5.1.0 does not accept the `pre-bash` event that 5.0.0 settings call, so until then `dotnet build` and `dotnet test` are not rewritten.

- The hook event that rewrites `dotnet build` and `dotnet test` is `pre-shell`, and `fuse init` writes it for Claude Code, Gemini CLI, Codex and the OpenCode plugin. `fuse hook` answers `pre-bash` with a usage line on standard error and exit code 0.
- For Codex, whose answer to the pre-shell hook also approves the command, the hook rewrites only a command that is one `dotnet build` or `dotnet test` with plain arguments, and gives any other command no answer, so Codex asks you about it as usual. 5.0.0 rewrote `dotnet build` and `dotnet test` after any separator and approved the whole command line, so a command such as `dotnet build && curl x | sh` ran without your approval.
- `fuse check` says "introduced": `fuse: N error(s) introduced in M file(s)` and `fuse: no errors introduced`, where 5.0.0 printed `new error(s)` and `no new errors`. `checked whole projects` replaces `whole projects bound`.
- An error in a file the agent did not edit is followed by a cause line: `changed:` or `removed:` and the header, on one line, of the declaration change that put the file in the check, as the working tree has it or as HEAD had it. An answer holds at most ten, and the summary ends with `N cause(s) left out` for the rest.
- `fuse test` says `without MSBuild` and `without MSBuild for N of M project(s)` where 5.0.0 said `fast path`, `no test is affected by the changes` where it said `no test reaches the changed code`, and `ran the tests your dotnet test arguments name` where it said `ran the tests you selected`.
- `fuse build` and `fuse test` take a per-repository lock for as long as they write build output, so two agents building or testing through Fuse run one after the other instead of making MSBuild fail with MSB3021, MSB3027 or CS2012 on a file the other holds. A client that has to wait says so once on standard error, and a client that cannot take the lock says why and runs anyway. The arguments `fuse build` passes to `dotnet build`, the implicit restore included, are unchanged.
- An analyzer or source generator the repository builds itself is loaded from a copy in the state directory, so a running engine no longer holds its file open and a real build that rebuilds it no longer fails with MSB3021 or MSB3027.
- Removing a `using` directive checks the files that use the edited file's types, because the declarations' text is unchanged while what their type names resolve to is not. An added `using` still checks only the edited file.
- A check finds a change to a type's parameterless constructor, such as making `public C()` internal, when a static constructor follows it in the file, which 5.0.0 missed. It no longer counts a formatting-only edit inside an explicit interface name as a declaration change.
- Test selection selects the tests that reach an edit it missed in 5.0.0: a body edit to an explicit interface implementation that shares its name and parameter types with another member, a changed parameter modifier (`ref`, `out`, `in`, `params` or `this`), which also selects the tests that reach the member's type, a change from `class` to `struct` or from `record` to `record struct`, and an edit to a delegate declared at namespace level.
- Test selection selects fewer extra tests: splitting a field declaration into two, or adding a variable to one, selects the tests that reach those fields and no longer every test that reaches the type. A formatting-only or comment-only edit inside a type header, a parameter type or a conversion, and an edit above a nested delegate, no longer count as a change to that declaration.
- A run without MSBuild emits a project the tests load when the test project's build output holds another build of it, as after `dotnet build Lib` when `Lib.Tests` references `Lib`. 5.0.0 ran the test project's older copy, so a test the working tree fails could pass.
- `fuse test` prints the errors of a test project that does not build, and exits with 1, when other test projects ran too; their results come first and the failed build is the last line. 5.0.0 printed only the other projects' results, so a passing project hid the failed build.
- A test run that exits with a code other than 0 and writes no results, as a failing Microsoft.Testing.Platform run does, ends with `fuse: the test run of <project> exited with code E and produced no results`, where 5.0.0 printed `test build failed (exit code E)`.
- The engine's pipe requests and responses have a different shape. An engine of another build, a running 5.0.0 engine included, answers the first request with a restart and exits, and the client starts an engine of its own build, so the first command after updating starts a fresh engine and no request fails.
- `engine.log` names every request by its kind, such as `CheckFiles Calc.cs took 15 ms`, and writes one line of per-phase times for it, so a slow check or test run can be traced to its phases.
- A check that arrives just as the engine finishes loading a dependent project in the background checks that project too. In 5.0.0 such a check could find the project loaded but leave it out, and miss the errors in it.
- A project with a NuGet restore warning, such as a package advisory, is no longer reported as a project Fuse could not load, so Fuse answers in a repository whose restore logs a warning.
- A `restore needed` answer names the project to restore in its command, so it also works for a project the solution leaves out.
- A hook and the MCP server read their input as UTF-8, so a repository whose path or file names are not ASCII is checked from a harness whose console uses another code page, where 5.0.0 failed to parse the payload and reported nothing.
- Git's file list and status are read NUL-separated, so a file whose name holds a quote, a backslash or a control character is found and checked under its own name.
- When `git status` fails while the engine reads the changes again, after HEAD moved, a watcher error or more than 300 changed files, the engine keeps HEAD and the changed files it knew, and the next request asks git again. 5.0.0 had already taken the new HEAD and emptied its list of changed files, so later checks left out the files changed before the failure.
- A hook whose payload names an empty working directory exits 0 with no output, as every other internal failure does, instead of crashing.
- `fuse init` and `fuse mcp` outside a git repository say `fuse: not inside a git repository; Fuse compares your changes with HEAD, so it needs one`, the message `fuse check` gives.

## 5.0.0

Fuse keeps a warm Roslyn compilation of a .NET repository and gives coding agents the compiler errors their edits introduced, affected-test runs, and compact build output, through agent hooks and a three-tool MCP server.

- `fuse init` registers hooks with Claude Code, Cursor, Gemini CLI, Codex and GitHub Copilot CLI, a plugin with OpenCode, and the MCP server with VS Code, for the harnesses the repository uses.
- `fuse hook <harness> <event>` runs a post-edit check that reports only new errors, rewrites `dotnet build` and `dotnet test` to their Fuse equivalents, and runs a stop check that sends the agent back once while its changes leave errors that were not at HEAD.
- `fuse check` reports the errors the working tree has that HEAD did not, including breaks in dependent projects, with analyzers that can report an error.
- `fuse test` runs the tests the changes can reach, from a shadow copy of the last build with changed assemblies emitted in memory when that is safe; `fuse test --all` runs everything; with `dotnet test` arguments it runs exactly that scope. Output lists failures only.
- `fuse build` runs `dotnet build` and prints only errors.
- `fuse mcp` serves `fuse_check`, `fuse_test` and `fuse_build`.
- One engine runs per repository, starts on demand, runs from a private copy of its binaries, and exits after 30 idle minutes. Nothing is configured and nothing is written inside the repository except the hook settings `fuse init` creates.
