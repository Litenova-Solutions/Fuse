# Fuse

Fuse keeps a warm Roslyn compilation of your .NET repository and plugs it into coding agents through hooks. After every edit, the agent sees the compiler errors that edit introduced, including breaks in projects that depend on the edited one, usually well under a second after the first load. When the agent runs `dotnet test`, Fuse runs only the tests your changes can affect, and prints only what failed.

```bash
dotnet tool install -g Fuse
cd your-repo
fuse init
```

That is the whole setup. The agent does not need instructions, a skill file or a tool to remember: the hooks run on their own.

## What the agent sees

After an edit that renames `Calc.Add` to `Calc.Plus`, the post-edit hook wakes the agent with:

```text
App/Program.cs(2,28): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
Lib.Tests/UnitTest1.cs(4,65): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
fuse: 2 new error(s) in 2 file(s) (App, Lib.Tests); Lib declarations changed, 2 dependent project(s) checked
```

After an edit that introduces no error, it sees nothing.

When the agent runs `dotnet test`, the command is rewritten to `fuse test`:

```text
FAILED Lib.Tests.CalcTests.Multiplies
  Assert.Equal() Failure: Values differ
  Expected: 6
  Actual:   7
  at Lib.Tests.CalcTests.Multiplies() in Lib.Tests/UnitTest1.cs:line 5
fuse: 1 failed, 0 passed in 5.5 s; ran 1 test(s) affected by your changes out of 2; fuse test --all runs everything; fast path
```

When the agent tries to finish while its changes leave errors that were not there at the last commit, the stop hook sends it back once with the list.

## Commands

| Command | What it does |
|---|---|
| `fuse init` | Registers the hooks with every agent harness the repository uses |
| `fuse check [files...]` | Errors the working tree has that HEAD did not, in the changed files and everything that depends on them |
| `fuse test` | Runs the tests affected by the working-tree changes and prints failures |
| `fuse test [dotnet test args]` | Runs exactly what `dotnet test` would, printing failures |
| `fuse test --all` | Runs every test, printing failures |
| `fuse build [dotnet build args]` | The real `dotnet build`, printing only errors |
| `fuse mcp` | Stdio MCP server with `fuse_check`, `fuse_test` and `fuse_build` |

Exit codes: 0 clean, 1 errors or failed tests, 2 Fuse could not answer (the message says why and what to run).

There is no configuration file and there are no environment variables. Fuse finds every `.csproj` in the repository (skipping git-ignored ones) and needs the projects to be restored.

## Harnesses

`fuse init` looks for each harness's folder and writes its hooks there. With none found, it sets up Claude Code.

| Harness | Detected by | Hooks written |
|---|---|---|
| Claude Code | `.claude/` or `CLAUDE.md` | `.claude/settings.json`: post-edit check (in the background, wakes the agent only on new errors), `dotnet build`/`dotnet test` rewrite, stop gate |
| Cursor | `.cursor/` | `.cursor/hooks.json`: post-edit check, stop follow-up |
| Gemini CLI | `.gemini/` or `GEMINI.md` | `.gemini/settings.json`: post-edit check, `dotnet` rewrite, stop gate |
| Codex | `.codex/` | `.codex/hooks.json`: post-`apply_patch` check, `dotnet` rewrite, stop gate |
| GitHub Copilot CLI | `.github/copilot-instructions.md` or `.github/hooks/` | `.github/hooks/fuse.json`: post-edit check, stop gate |
| VS Code agent mode | `.vscode/` | `.vscode/mcp.json`: the MCP server (VS Code's own agent has no hooks) |

Re-running `fuse init` replaces Fuse's entries and leaves everything else in those files alone. If your Claude Code settings already allow `dotnet build` or `dotnet test` without asking, `init` adds the same allowance for `fuse build` and `fuse test`.

Any other MCP host can run `fuse mcp` from the repository directory.

## How it works

One `fuse engine` process runs per repository. The first command or hook starts it; it exits after 30 minutes without a request. It evaluates every project with MSBuild (no build), and loads a project's compilation through Roslyn's `MSBuildWorkspace` only when a change touches it, so a session that edits one project never pays for the rest of the repository.

**Checking.** Fuse compares each changed file's errors in the working tree with the same file at HEAD, so errors that were already there are never reported as yours. It then compares the file's declarations with HEAD. A body-only edit ends the check. A changed declaration re-checks the files in the owning project and its dependents that mention a changed name; a change that can reach code without naming it (a base type, an operator, a global using) re-checks every file there. Analyzers run only when the project configures one of their diagnostics as an error. Warnings are not reported.

**Testing.** Fuse walks references backwards from every changed member (through callers, overrides and interface implementations) until it reaches test methods. Code a framework calls (controller actions, request handlers, hosted services, top-level statements) has no caller in source, so reaching it selects every test project that depends on it. When the test projects already have build output and nothing but C# sources changed since, Fuse emits the changed assemblies from memory into a copy of that output and runs the tests there without MSBuild. Otherwise it runs `dotnet test` normally.

**Staying current.** A file watcher and a content comparison against HEAD track what changed. Project files, props, targets, `global.json` and `.editorconfig` changes trigger re-evaluation. A branch switch or commit resets the baseline.

## Measured

From the evals in `evals/Fuse.Evals`, run through the `fuse` executable on one Windows machine (results in `evals/results`):

| Eval | Fixture (5 projects) | NodaTime (17 projects) |
|---|---|---|
| Correctness: mutations compared with a real `dotnet build` | 30 cases, 0 missed errors, 0 extra errors | 20 cases, 0 missed errors, 0 extra errors |
| Warm check after a body edit, P50 / P95 | 157 / 204 ms | 532 / 704 ms |
| Warm check after a signature edit, P50 / P95 | 200 / 2,369 ms | 1,456 / 7,363 ms |
| Check on an unchanged tree (client and engine overhead), P50 | 140 ms | 139 ms |
| Engine memory | 218 MB | 801 MB |
| Test selection: failing tests missed | 0 of 10 cases | not measured yet |

The signature-edit P95 includes the first such edit, which loads every dependent project; later signature edits on NodaTime took 1.1 to 1.4 s. NodaTime builds with `TreatWarningsAsErrors`, so its body edits also run every analyzer that can report a warning. On the small fixture, `fuse test` ran 15 percent of the tests but took as long as a full `dotnet test` (median 4.0 s each), because test host startup dominates there.

## Limits

- Fuse compiles what MSBuild's design-time evaluation describes. Custom targets that change compilation inputs at build time, source generators that read files outside the project's additional files, and IL weaving are invisible to `fuse check`. `fuse build` is the ground truth.
- Compilation-end analyzers are not run by `fuse check`.
- Test selection is static. Tests that reach code only through reflection or assembly scanning are selected when the walk reaches framework-invoked code, not otherwise.
- Test projects on Microsoft.Testing.Platform (opted in through `global.json`) run whole, without selection or the fast path.
- The fast test path uses resources and content files from the last real build. When any of them changed, Fuse builds with MSBuild instead.
- C# only.

## Troubleshooting

- **`restore needed`**: run `dotnet restore`. Fuse never restores on its own.
- **The engine log** is `engine.log`, and hook failures go to `hook.log`, in `%LOCALAPPDATA%use
epos\<id>` (Windows) `~/.local/share/fuse/repos/<id>` (Linux) or `~/Library/Application Support/fuse/repos/<id>` (macOS). Fuse writes nothing inside the repository except the hook settings `fuse init` creates.
- **Stopping the engine**: it exits on its own after 30 idle minutes; killing the `fuse` process is safe.

## License

Apache-2.0. See [LICENSE](LICENSE).
