# ![Fuse logo](site/assets/fuse-icon.svg) Fuse

[![NuGet](https://img.shields.io/nuget/v/Fuse)](https://www.nuget.org/packages/Fuse)
[![NuGet downloads](https://img.shields.io/nuget/dt/Fuse)](https://www.nuget.org/packages/Fuse)
[![CI](https://github.com/Litenova-Solutions/Fuse/actions/workflows/ci.yml/badge.svg)](https://github.com/Litenova-Solutions/Fuse/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/Litenova-Solutions/Fuse)](https://github.com/Litenova-Solutions/Fuse/blob/main/LICENSE)

Fuse gives AI coding agents a faster .NET build and test loop.

AI agents check their work by running `dotnet build` and `dotnet test`, often dozens of times in one task. Each run is slow, and it prints far more than the agent needs. When several subagents build, the builds use a lot of memory and fail on files that another build locked.

Fuse fixes that. It keeps your solution loaded in the background. After each edit, it tells the agent:

- which compiler errors the edit introduced, 8.1x to 27.7x faster than `dotnet build` at the median on 4 open-source repositories
- which affected tests fail, up to 2.9x faster than `dotnet test`

Run `fuse init` once. After that, the harness, the program your agent runs in (Claude Code, Cursor, Codex and others), runs Fuse by itself.

[Website](https://fuse.codes) | [Documentation](https://fuse.codes/docs/) | [Getting started](https://fuse.codes/docs/getting-started) | [Benchmarks](https://fuse.codes/docs/benchmarks) | [Changelog](https://github.com/Litenova-Solutions/Fuse/blob/main/CHANGELOG.md)

![Fuse's time as a share of the dotnet command it replaces](site/assets/benefits.svg)

## What you get

- **Only introduced errors.** Fuse leaves out every error that was already in HEAD, the commit you have checked out, so the agent does not chase problems it did not cause.
- **Breaks in other projects.** When you rename a method, Fuse lists every broken caller in every project, with the change that broke it.
- **Only affected tests.** Tests the change cannot reach do not run. When only `.cs` files changed, the tests run without a rebuild.
- **Short output.** A check prints at most 20 errors, and a test run prints at most 10 failures, plus one summary line. When the edit is clean, Fuse prints nothing at all.
- **Made for subagents.** All agents in a repository share one Fuse process, so Fuse loads the solution once. Builds through Fuse take turns, so parallel builds do not fail on locked files. Each git worktree gets its own process.
- **Made for judges and workers.** External writers register their paths as sessions. The judge checks one slice with `fuse check <folder>`. The `--json` errors split into briefs.
- **Safe.** If Fuse cannot answer, the hook stays silent and the agent carries on. Fuse never edits your code, runs locally and has no telemetry.
- **No configuration.** `fuse init` is the only setup.

## What it costs

- **Memory.** Fuse keeps the loaded projects in memory until it has been idle for 30 minutes. That costs 319 MB to 512 MB once idle in the [benchmarks](https://fuse.codes/docs/benchmarks), up to 1,091 MB while it checks, and more on a machine with more memory. Each git worktree uses its own.
- **A slow first check across projects.** The first edit that affects other projects has to load them. On Jellyfin (40 projects) that took up to 68.9 s. The median signature edit took 58 ms.
- **One check at a time.** Agents in the same repository wait for each other's checks.
- **Not a full build.** Fuse checks C# only and picks tests by reading the code, so a few cases still need `fuse build` or `fuse test --all`. [Limits](https://fuse.codes/docs/limits) lists them.

## Supported harnesses

| Harness | Integration |
| --- | --- |
| Claude Code | Hooks |
| Cursor | Hooks |
| Gemini CLI | Hooks |
| Codex | Hooks |
| GitHub Copilot CLI | Hooks |
| OpenCode | Plugin |
| Muse Code | Plugin bundle, which you install and approve once |
| VS Code agent mode | MCP server, since VS Code runs no hooks |

`fuse init` detects each harness by its folder or file in the repository root. When it finds none, it sets up Claude Code. [Connect your agent](https://fuse.codes/docs/harnesses) lists what it writes for each one.

## Getting started

### Requirements

- Install the .NET 10 SDK.
- Use a git repository with at least one commit.
- Restore your projects. Fuse never runs `dotnet restore` on its own.

### Install

```bash
dotnet tool install -g Fuse
```

This command installs `fuse` in `~/.dotnet/tools` (`%USERPROFILE%\.dotnet\tools` on Windows). That folder must be on your `PATH` and on your agent's.

### Set up a repository

In the root of your repository, run:

```bash
fuse init
```

```text
wrote .claude/settings.json
wrote .gitignore
fuse: hooks registered by Fuse 5.7.0; after each edit your agent gets the compiler errors the edit introduced, `dotnet test` runs the affected tests, and `dotnet build` prints only its errors
```

That is the whole setup. The agent now hears from Fuse only when an edit introduced an error.

### Try it from the command line

The commands print exactly what the agent receives, so you can try Fuse in a terminal. For example, after renaming `Calc.Add` in a library, `fuse check` lists the callers the rename broke in other projects:

```text
$ fuse check
removed: public int Add(int a, int b) (2 error(s))
App/Program.cs(3,30): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
Lib.Tests/CalcTests.cs(6,54): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
fuse: 2 error(s) introduced in 2 file(s) (App, Lib.Tests); Lib declarations changed, 2 dependent project(s) checked; tree 9f2c41aa07b3 over 1 changed file(s)
```

| Command | Action |
| --- | --- |
| `fuse init [--instructions]` | Registers Fuse's hooks with the harnesses this repository uses. `--instructions` also adds a Fuse section to `AGENTS.md` |
| `fuse uninit` | Removes what `fuse init` wrote, keeping every other entry, then stops this repository's engine and deletes its state |
| `fuse check [paths...]` | Reports the errors your changes introduced since HEAD, in every project. With directories or patterns, it checks one slice |
| `fuse test [args...]` | Runs the tests affected by your changes |
| `fuse test --all` | Runs every test |
| `fuse test --failed` | Reruns the tests that failed in the last run |
| `fuse build [args...] [--changed]` | Runs `dotnet build` and prints its errors. `--changed` builds only the changed projects and their dependents |
| `fuse status [--json]` | Shows the engine and the changed files |
| `fuse session begin <name> --paths <pattern>...` | Registers an external writer as a session for its paths, so a judge's stop leaves the writer's errors to the writer |
| `fuse session end <name>` | Ends the registration |
| `fuse stop [--all]` | Stops this repository's engine, or every engine. The next command starts a new one |
| `fuse mcp` | Serves `fuse_check`, `fuse_test` and `fuse_build` over stdio to an MCP host |

[Getting started](https://fuse.codes/docs/getting-started) shows each command's output and what the hooks send the agent.

## Documentation

- [Getting started](https://fuse.codes/docs/getting-started): set Fuse up in your repository and see what it reports.
- [Commands](https://fuse.codes/docs/commands): every command, its output and its exit codes.
- [Messages and fixes](https://fuse.codes/docs/messages): what each message means when Fuse cannot answer, and the fix.
- [Connect your agent](https://fuse.codes/docs/harnesses): what `fuse init` sets up for each harness, and how an MCP host runs Fuse.
- [Subagents and parallel agents](https://fuse.codes/docs/parallel-agents): several agents in one repository, and what each is told.
- [How it works](https://fuse.codes/docs/how-it-works): how Fuse checks an edit, selects tests and runs them.
- [Limits](https://fuse.codes/docs/limits): what a check cannot see, and what to do about it.
- [Troubleshooting](https://fuse.codes/docs/troubleshooting): why Fuse is silent, slow or failing, and where its logs are.
- [Benchmarks](https://fuse.codes/docs/benchmarks): benchmarked speed and accuracy on 4 repositories.
- [All documentation](https://fuse.codes/docs/)

## Contributing

Report bugs, request features and send pull requests on [GitHub](https://github.com/Litenova-Solutions/Fuse/issues), and ask questions in [Discussions](https://github.com/Litenova-Solutions/Fuse/discussions). [Contributing](https://github.com/Litenova-Solutions/Fuse/blob/main/CONTRIBUTING.md) describes how to build, test and submit a change. Every commit needs a Developer Certificate of Origin sign-off. AI-assisted contributions follow the [AI policy](https://fuse.codes/docs/ai-policy), and everyone follows the [code of conduct](https://github.com/Litenova-Solutions/Fuse/blob/main/.github/CODE_OF_CONDUCT.md).

To report a vulnerability, follow [Security](https://github.com/Litenova-Solutions/Fuse/blob/main/SECURITY.md) instead of opening a public issue.

## License

Fuse is licensed under the [Apache License 2.0](https://github.com/Litenova-Solutions/Fuse/blob/main/LICENSE).
