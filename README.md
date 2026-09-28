# Fuse

Near-instant compiler feedback for AI coding agents on .NET.

Fuse keeps your solution compiled in memory with Roslyn and plugs it into coding agents through hooks. After every edit, the agent learns which compiler errors that edit introduced, including breaks in projects that depend on it, up to 10.0x faster than `dotnet build`. When the agent runs `dotnet test`, only the tests the change can reach run, and only failures are printed.

```bash
dotnet tool install -g Fuse
cd your-repo
fuse init
```

That is the setup. The hooks run on their own, so the agent needs no instructions and no tool to remember. Website: [fuse.codes](https://fuse.codes).

![Fuse's time as a share of the dotnet command it replaces](https://raw.githubusercontent.com/Litenova-Solutions/Fuse/main/site/benefits.svg)

## What the agent sees

After an edit that renames `Calc.Add` to `Calc.Plus` in a library, the post-edit hook wakes the agent with:

```text
App/Program.cs(2,28): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
  removed: public int Add(int a, int b)
Lib.Tests/UnitTest1.cs(4,65): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
  removed: public int Add(int a, int b)
fuse: 2 new error(s) in 2 file(s) (App, Lib.Tests); Lib declarations changed, 2 dependent project(s) checked
```

The indented line under an error in a file the agent did not edit names the changed declaration that broke it, as it was declared. An edit that introduces no error produces no output. Errors that exist at the last commit are never reported.

When the agent runs `dotnet test`, the command runs as `fuse test`:

```text
FAILED Lib.Tests.CalcTests.Multiplies
  Assert.Equal() Failure: Values differ
  Expected: 6
  Actual:   7
  at Lib.Tests.CalcTests.Multiplies() in Lib.Tests/UnitTest1.cs:line 5
fuse: 1 failed, 0 passed in 1.3 s; ran 1 test(s) affected by your changes out of 2; fuse test --all runs everything; fast path
```

When the agent tries to finish while its changes leave errors that are not at the last commit, the stop hook sends it back once with the list.

## Commands

| Command | What it does |
|---|---|
| `fuse init` | Registers the hooks with every agent harness the repository uses |
| `fuse check [files...]` | Errors the working tree has that HEAD does not, in the changed files and everything that depends on them |
| `fuse test` | Runs the tests affected by the working-tree changes and prints failures |
| `fuse test [dotnet test args]` | Runs the scope `dotnet test` would run with those arguments, printing failures |
| `fuse test --all` | Runs every test, printing failures |
| `fuse build [dotnet build args]` | The real `dotnet build`, printing its errors (or the end of its output when no error line parses) |
| `fuse mcp` | Stdio MCP server with `fuse_check`, `fuse_test` and `fuse_build` |

Exit codes: 0 clean, 1 errors or failed tests, 2 Fuse could not answer (the message says why and what to run).

There is no configuration file and there are no environment variables. Fuse finds every `.csproj` git knows about and needs the projects to be restored. In a repository without a `.csproj`, every command and hook returns at once without starting anything.

## Harnesses

`fuse init` looks for each harness's folder and writes its hooks there. With none found, it sets up Claude Code.

| Harness | Detected by | Hooks written |
|---|---|---|
| Claude Code | `.claude/` or `CLAUDE.md` | `.claude/settings.json`: post-edit check (in the background, wakes the agent only on new errors), `dotnet build` and `dotnet test` rewrite, stop check |
| Cursor | `.cursor/` | `.cursor/hooks.json`: post-edit check, stop follow-up |
| Gemini CLI | `.gemini/` or `GEMINI.md` | `.gemini/settings.json`: post-edit check, `dotnet` rewrite, stop check |
| Codex | `.codex/` | `.codex/hooks.json`: post-`apply_patch` check, `dotnet` rewrite, stop check |
| GitHub Copilot CLI | `.github/copilot-instructions.md` or `.github/hooks/` | `.github/hooks/fuse.json`: post-edit check, stop check |
| OpenCode | `.opencode/`, `opencode.json` or `opencode.jsonc` | `.opencode/plugins/fuse.js`: post-edit check, `dotnet` rewrite, stop check |
| VS Code agent mode | `.vscode/` | `.vscode/mcp.json`: the MCP server |

Running `fuse init` again replaces Fuse's entries in shared settings files and keeps the other entries (comments in those JSON files are not kept); `.github/hooks/fuse.json` and `.opencode/plugins/fuse.js` belong to Fuse and are written whole. OpenCode runs plugins rather than commands, so its plugin passes each tool event to `fuse hook opencode`; it supports OpenCode 1 and 2 plugin formats. If your Claude Code settings allow `dotnet build` or `dotnet test` without asking, `init` adds the same allowance for `fuse build` and `fuse test`. Any other MCP host can run `fuse mcp` from the repository directory.

The Claude Code integration is tested end to end with Claude Code 2.1.282, and the OpenCode plugin with OpenCode 2.0.15. The Cursor, Gemini CLI, Codex and Copilot CLI adapters, and the OpenCode 1 plugin format, follow each harness's documented hook format and are covered by payload tests.

## How it works

One `fuse engine` process runs per repository. The first command or hook starts it, and it exits after 30 minutes without a request. It evaluates every project with MSBuild, without building, and loads a project's compilation only when a change touches it.

- **Checking.** Fuse binds each changed file in the working tree and at HEAD and reports only the difference. When a file's declarations change, it finds the code that uses them with Roslyn's symbol search, in the owning project and every dependent project, and checks that code too. Analyzers run when the project configures one of their diagnostics as an error. Warnings are not reported.
- **Testing.** Fuse walks from the changed code to the test classes that can reach it, and refines that to test methods when the set is small. Application code (controllers, handlers, hosted services, top-level statements) runs behind a host, so reaching it selects every test project that depends on the application. When the test projects have build output and only C# sources changed since, Fuse emits the changed assemblies from memory into a copy of that output and runs the tests there without MSBuild.
- **Building.** `fuse build` and `fuse test` take a per-repository lock while they write build output, so agents building or testing through Fuse at the same time take turns instead of failing on each other's files. Analyzers and source generators the repository builds itself load from a copy, so the engine never holds a file a build writes.
- **Staying current.** A file watcher and a content comparison against HEAD track what changed. Project files, props, targets, `global.json` and `.editorconfig` re-evaluate the projects. A commit or branch switch moves the baseline.

[docs/design.md](docs/design.md) describes each part in detail.

## Measured

From the evals in `evals/Fuse.Evals`, run through the `fuse` executable on one Windows machine, on the Fuse 5.1.0 source at `14255ab` (the result files' build ids differ only by the commit embedded in the version). Measured at: fixture at `6c4e298`, NodaTime at `fcd80e1`, Jellyfin at `1d7c6af`, Community Toolkit at `b135626`.

| Eval | Small solution (fixture: 5 projects, 22 tests) | NodaTime (15 projects, 42,700 tests) | Jellyfin (40 projects, 2,535 tests) | .NET Community Toolkit (26 projects, 12,449 tests) |
|---|---|---|---|---|
| Correctness: generated edits compared with a real `dotnet build` | 30 cases: 0 missed, 0 contradicted | 30 cases: 1 missed, 0 contradicted | 30 cases: 0 missed, 0 contradicted | 30 cases: 0 missed, 0 contradicted |
| `fuse check` vs `dotnet build`, median | 0.18 s vs 1.14 s | 0.63 s vs 1.45 s | 0.70 s vs 4.26 s | 0.45 s vs 4.48 s |
| Warm check after a body edit, P50 / P95 | 168 / 252 ms | 778 / 933 ms | 239 / 393 ms | 1,266 / 1,667 ms |
| Warm check after a signature edit, P50 / P95 | 206 / 2,241 ms | 2,326 / 10,273 ms | 184 / 52,315 ms | 568 / 35,178 ms |
| Test selection: failing tests missed | 0 in 10 cases | 0 in 10 cases | 0 in 10 cases | 0 in 10 cases |
| Cases the suite could not check name by name | 0 | 2 | 0 | 0 |
| Tests run by `fuse test` | 15 percent | 80 percent | 22.3 percent | 13.7 percent |
| `fuse test` vs `dotnet test`, median | 1.27 s vs 4.17 s | 32.0 s vs 44.4 s | 165.3 s vs 226.6 s | 65.1 s vs 428.5 s |
| Three `fuse build` runs at once, five rounds: lock collisions | 0 | 0 | 0 | 0 |
| Engine memory | 271 MB | 865 MB | 1,019 MB | 2,029 MB |

Every number above comes from these files: `evals/results/correctness-fixture-20260929-0115.json`, `evals/results/selection-fixture-20260928-2052.json`, `evals/results/latency-fixture-20260928-2053.json`, `evals/results/correctness-NodaTime-20260929-0117.json`, `evals/results/selection-NodaTime-20260928-2109.json`, `evals/results/latency-NodaTime-20260928-2116.json`, `evals/results/correctness-Jellyfin-20260929-0124.json`, `evals/results/selection-Jellyfin-20260928-2237.json`, `evals/results/latency-Jellyfin-20260928-2251.json`, `evals/results/correctness-CommunityToolkit-20260929-0130.json`, `evals/results/selection-CommunityToolkit-20260929-0031.json` and `evals/results/latency-CommunityToolkit-20260929-0049.json`.

"Contradicted" means Fuse reported an error the build does not have. When a project has a declaration error, csc stops before it binds method bodies or runs analyzers, and after any compiler error it skips its documentation checks, so the build reports fewer errors than exist; Fuse reports them at once, and the suite counts those as deferred rather than contradicted. NodaTime's one missed error is a second-order difference: the build compiles dependents against reference assemblies, which omit private members, so it reports "no definition" and a follow-on conversion error where Fuse, compiling against source, reports "inaccessible". The signature-edit P95 includes the first such edit after the engine starts, which loads every dependent project; on Jellyfin and the Community Toolkit that first edit loads dozens of projects, which is why their P95 is in seconds. NodaTime builds with `TreatWarningsAsErrors`, so each check there also runs the analyzers that can report a warning. In two NodaTime cases hundreds of tests fail and `fuse test` lists at most 100 failing names, so the suite cannot match them one by one; `fuse test` ran 42,689 of 42,700 tests in both. Timings on this machine vary by about 150 ms between runs of the same build.

## Limits

- C# only. Fuse needs the .NET 10 SDK and restored projects.
- A check compiles what MSBuild's evaluation describes. Custom targets that change compilation inputs, source generators that read files outside the project's additional files, and IL weaving are invisible to it; `fuse build` runs the real build.
- Compilation-end analyzers do not run in a check.
- Test selection is static. Tests that reach code only through reflection are selected when the walk reaches an application's host, not otherwise.
- Test projects on Microsoft.Testing.Platform (opted in through `global.json`) run whole, without selection or the fast path.
- The fast test path takes resources and content files from the last real build; when any of them changed, Fuse builds with MSBuild instead.
- The build lock covers builds that go through Fuse. A `dotnet build` run directly, outside a harness whose `dotnet` commands Fuse rewrites, can still collide with one.
- An analyzer the repository builds itself is loaded as it was when its project loaded; a rebuilt analyzer takes effect when projects next reload.

## Troubleshooting

- **`restore needed`**: run the `dotnet restore` command the message names. Fuse never restores on its own.
- **Logs**: `engine.log` and `hook.log` in `%LOCALAPPDATA%\fuse\repos\<id>` on Windows, `~/.local/share/fuse/repos/<id>` on Linux, `~/Library/Application Support/fuse/repos/<id>` on macOS.
- **Stopping the engine**: it exits on its own after 30 idle minutes; ending the `fuse` process is safe.

## License

Apache-2.0. See [LICENSE](LICENSE).
