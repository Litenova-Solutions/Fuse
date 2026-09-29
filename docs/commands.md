# Commands

This page is the reference for every `fuse` command: its arguments, what it prints, its exit codes, and every message Fuse gives when it cannot answer, with the fix. [Getting started](getting-started.md) walks through the common commands on a sample repository.

`fuse` is one executable. `check`, `test` and `build` are its three operations; `hook` runs them for a harness and `mcp` serves them to an MCP host, and both print the same text the command line does. Each command works on the git repository that contains the current directory (a hook, on the repository of the files its payload names), compares the working tree (the files on disk) with HEAD (the commit you have checked out), and reads no configuration.

```text
$ fuse help
fuse - instant C# compiler feedback and affected-test runs for coding agents

  fuse init                 register Fuse's hooks with the agent harnesses this repository uses
  fuse check [files...]     errors the working tree has that HEAD does not, across dependent projects
  fuse test [args...]       run the tests affected by your changes (with args: the tests those dotnet test arguments name)
  fuse test --all           run every test
  fuse build [args...]      dotnet build, printing its errors
  fuse mcp                  stdio MCP server (fuse_check, fuse_test, fuse_build) for MCP hosts that run no hooks
  fuse hook <harness> <event>   the command registered hooks run
```

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Fuse answered and found nothing to fix: no errors introduced, the tests passed, or the build succeeded. |
| 1 | Fuse answered and found something to fix: errors introduced, a test failed, or a build failed. |
| 2 | Fuse could not answer. The output says why and what to run, as [Messages and fixes](#messages-and-fixes) lists. An unknown command also exits with 2 and prints the usage on standard error, and `fuse test --all` with other arguments exits with 2 and says why there ([fuse test](#fuse-test)). |
| 130 | Ctrl+C stopped the command. |

`fuse hook` exits with 0 in every case except one: the Claude Code post-edit hook exits with 2 to wake the agent, as [fuse hook](#fuse-hook) describes.

## `fuse init`

Registers Fuse's hooks with every harness the repository uses, and the MCP server with VS Code. It detects a harness by its folder or file in the repository root and sets up Claude Code when it finds none. [Harnesses](harnesses.md) lists what it detects and writes, and how running it again behaves.

```text
$ fuse init
wrote .claude/settings.json
fuse: hooks registered; after each edit your agent gets the compiler errors the edit introduced, `dotnet test` runs the affected tests, and `dotnet build` prints only its errors
```

It exits with 2 outside a git repository and in a repository where git knows no `.csproj` file. It also exits with 2, after the `wrote` lines of the files it wrote before, when a settings file it has to change is not valid JSON (`fuse: <file> is not valid JSON (<reason>); fix or remove it`, leaving the file as it was) or cannot be written (`fuse: could not write <file> (<reason>)`, leaving no temporary file). An empty settings file, or one that holds only comments, counts as an empty object.

## `fuse check`

```text
fuse check [files...]
```

Reports the compiler errors the working tree has that HEAD does not, in the checked files and in the files, in the same project or a dependent one, that use a declaration the checked files changed.

- **Without files**, it checks every source file (`.cs`, `.razor`, `.cshtml`) that differs from HEAD: modified, added (untracked and not ignored by git) or deleted. Files under `bin`, `obj`, `.git` and `node_modules` are skipped.
- **With files**, it checks those files. A path is relative to the current directory or absolute. A named file is checked even when it matches HEAD, and a file that is not a source file or that no project owns is skipped.

It waits for the engine to finish loading the repository, for up to 10 minutes. [How it works](how-it-works.md#check) describes what the check does.

```text
$ fuse check
App/Program.cs(3,30): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
  removed: public int Add(int a, int b)
Lib.Tests/CalcTests.cs(6,54): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
  removed: public int Add(int a, int b)
fuse: 2 error(s) introduced in 2 file(s) (App, Lib.Tests); Lib declarations changed, 2 dependent project(s) checked
```

The output has three kinds of line.

- **Error lines**, one per introduced error, as `path(line,column): error ID: message`, with the path relative to the repository root and forward slashes. The engine returns at most 200 errors, ordered by file and position, and the command prints the first 20.
- **Cause lines**, indented by two spaces under an error in a file the check reached because of a declaration change: `changed:` and the changed declaration as the working tree has it, or `removed:` and the removed declaration as HEAD had it, on one line without its body. A field is quoted as its modifiers, type and name, as in `changed: public long Limit`, and a constant with its value. An error in a checked file itself, an analyzer error, and an error reached by a change that can break code without naming it (a type header, a delegate, a global using or an assembly attribute) get no cause line. An answer holds at most ten.
- **The summary line**, last, in one of two forms:
    - `fuse: N error(s) introduced in M file(s)` followed by `, first 20 shown` when N is over 20, and the projects the errors are in, in parentheses.
    - `fuse: no errors introduced (F file(s) checked)`, where F counts the checked files and the files the check reached.

The summary ends with any of these parts, separated by semicolons:

- `P declarations changed, K dependent project(s) checked`: the projects in which a checked file changed a declaration, and how many projects that depend on them the check searched.
- `checked whole projects`: more than 500 files were in reach, so the check compiled the reached projects whole.
- `C cause(s) left out`: more than ten errors had a cause, and C of them are printed without one.

An error counts as introduced when HEAD has no error with the same file, id and message; line numbers are ignored, so an error that only moved is not reported. Errors are compiler errors, warnings the project treats as errors, and analyzer diagnostics at error severity. Warnings are not reported.

The exit code is 1 when an error was introduced and 0 when none was.

## `fuse test`

```text
fuse test
fuse test --all
fuse test [dotnet test arguments]
```

Runs tests and prints only failures, in one of three forms.

- **`fuse test`** runs the tests affected by the working-tree changes: the tests the changed code can reach, as [How it works](how-it-works.md#test-selection) describes. When a test project has build output and only C# sources changed since, it runs without MSBuild.
- **`fuse test --all`** runs every test project whole, each built with MSBuild.
- **`fuse test` with `dotnet test` arguments** runs `dotnet test` with those arguments in the current directory and adds result reporting. `--all` cannot be combined with them: `fuse test --all --filter Name=A` prints `fuse: --all runs every test and cannot be combined with other arguments; pass the arguments without --all to choose the scope` on standard error, runs nothing and exits with 2.

Every form takes the [build lock](how-it-works.md#builds-and-the-build-lock) until its last `dotnet test` process exits. The first form waits for the engine's plan for up to 10 minutes.

```text
$ fuse test
FAILED Lib.Tests.CalcTests.Multiplies
  Assert.Equal() Failure: Values differ
  Expected: 6
  Actual:   7
  at Lib.Tests.CalcTests.Multiplies() in Lib.Tests/CalcTests.cs:line 9
fuse: 1 failed, 0 passed in 2.9 s; ran 1 test(s) affected by your changes out of 2; fuse test --all runs everything; without MSBuild
```

Each failed test prints `FAILED` and its name, then its message (up to 12 lines) and up to five stack frames inside the repository, indented by two spaces. The first 10 failures print in detail. The next 100 print by name only, under `also failed (N):`, and a line `... and N more` counts the rest.

The summary line is `fuse: F failed, P passed in X.X s` (with `, S skipped` when tests were skipped), then what ran, then how it ran:

| What ran | When |
| --- | --- |
| `ran N test(s) affected by your changes out of T; fuse test --all runs everything` | `fuse test` |
| `ran every test in N test project(s)` | `fuse test --all` |
| `ran the tests your dotnet test arguments name` | `fuse test` with arguments |

N and T count test methods from source, by test attribute, without running test discovery; the failed and passed counts come from the test run. When a test project runs whole, the first form adds the reason in parentheses, for example `(whole projects where App uses the changed code and runs behind an application host)`. The reasons are `<application> uses the changed code and runs behind an application host`, `<application>'s entry point calls the changed code`, `<member> is called by an application host or a framework`, `top-level statements changed`, `<file> changed` for a Razor file, `a test file without classes changed` and `a test project changed outside any class`.

How it ran is `without MSBuild`, `without MSBuild for N of M project(s)` or `built with MSBuild`; the form with arguments leaves it out.

When no test runs, the command prints one line and exits with 0:

```text
fuse: no C# changes since HEAD, so no test is affected (fuse test --all runs everything)
fuse: no test is affected by the changes (out of T); fuse test --all runs everything
fuse: no test projects in this repository
```

When a test project does not build, the command prints the build's errors and `fuse: test build failed with N error(s) in T s` in the format of [fuse build](#fuse-build), and exits with 1. When other test projects ran, their results come first and each failed build follows them, so the last line is the failed build even when every test that ran passed.

A test project on Microsoft.Testing.Platform writes no TRX file. A passing run prints `fuse: tests passed in T s;` and the summary. A failing run prints the last 30 lines of its output and `fuse: the test run of <project> exited with code E and produced no results in T s`, and exits with 1. Any test run that exits with a code other than 0, writes no results and prints no error line ends the same way; for `fuse test` with arguments the line names the run `dotnet test`.

The exit code is 1 when a test failed or a test project did not build, and 0 otherwise.

## `fuse build`

```text
fuse build [dotnet build arguments]
```

Runs the real `dotnet build` in the current directory with the arguments unchanged, implicit restore included, and adds `-nologo -tl:off -v:q -clp:ErrorsOnly;NoSummary`. Inside a repository it takes the [build lock](how-it-works.md#builds-and-the-build-lock); outside one it runs without it.

```text
$ fuse build
Lib.Tests/CalcTests.cs(6,54): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
App/Program.cs(3,30): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
fuse: build failed with 2 error(s) in 1.2 s
```

It prints each error once, with its path relative to the repository root and without MSBuild's trailing project tag, up to 20 of them. The last line is one of:

- `fuse: build succeeded in T s`
- `fuse: build failed with N error(s) in T s`, with `, first 20 shown` when N is over 20
- `fuse: build failed (exit code E) in T s`, after the last 30 lines of the build's output, when the build failed and no line of its output parses as an error

Warnings are not printed. The exit code is 0 when the build succeeded and 1 when it failed.

## `fuse mcp`

Runs an MCP server over standard input and output for an MCP host without hooks. The server finds the repository from its working directory on every call, so the host has to start it inside the repository. [Harnesses](harnesses.md#other-mcp-hosts) shows how to register it.

| Tool | Arguments | Runs |
| --- | --- | --- |
| `fuse_check` | `files`: array of paths, relative to the repository root or absolute; omit to check every change | `fuse check` |
| `fuse_test` | `all`: `true` to run every test | `fuse test` or `fuse test --all` |
| `fuse_build` | `target`: project or solution; omit to build what `dotnet build` picks in the repository root | `fuse build` |

Each tool returns one text block with the same text the command prints. The result's `isError` is `true` only when Fuse could not answer, including outside a repository and for an unknown tool name. Errors introduced, failed tests and a failed build are answers, so their `isError` is `false`. `fuse_check` is marked read-only and idempotent; `fuse_test` and `fuse_build` are not read-only.

This transcript sends an `initialize` request, the `initialized` notification, `tools/list`, one `fuse_check` call and one call to a tool that does not exist, one JSON message per line, and shows the server's responses:

```text
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"example","version":"1.0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"fuse_check","arguments":{"files":["Lib/Calc.cs"]}}}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"fuse_lint","arguments":{}}}
```

```json
{"result":{"protocolVersion":"2025-06-18","capabilities":{"logging":{},"tools":{}},"serverInfo":{"name":"fuse","version":"5.1.0"}},"id":1,"jsonrpc":"2.0"}
{"result":{"tools":[{"name":"fuse_check","title":"Check C# changes","description":"Compiler and analyzer errors the working tree has and HEAD does not: in the named files, or in every changed file when none are named, and in the files of dependent projects that use a changed declaration. Run after editing C# files. Once Fuse has loaded the repository it answers much faster than dotnet build.","inputSchema":{"type":"object","properties":{"files":{"type":"array","items":{"type":"string"},"description":"Files to check, absolute or relative to the repository root. Omit to check every file that differs from HEAD."}}},"annotations":{"idempotentHint":true,"openWorldHint":false,"readOnlyHint":true}},{"name":"fuse_test","title":"Run affected tests","description":"Runs the tests affected by the working-tree changes and reports only failures. Set all=true to run every test.","inputSchema":{"type":"object","properties":{"all":{"type":"boolean","description":"Run every test instead of the affected ones."}}},"annotations":{"destructiveHint":false,"openWorldHint":false,"readOnlyHint":false}},{"name":"fuse_build","title":"Build","description":"Runs dotnet build and reports its errors.","inputSchema":{"type":"object","properties":{"target":{"type":"string","description":"Project or solution to build. Omit to build what dotnet build picks in the repository root."}}},"annotations":{"destructiveHint":false,"openWorldHint":false,"readOnlyHint":false}}]},"id":2,"jsonrpc":"2.0"}
{"result":{"content":[{"type":"text","text":"App/Program.cs(3,30): error CS1061: \u0027Calc\u0027 does not contain a definition for \u0027Add\u0027 and no accessible extension method \u0027Add\u0027 accepting a first argument of type \u0027Calc\u0027 could be found (are you missing a using directive or an assembly reference?)\n  removed: public int Add(int a, int b)\nLib.Tests/CalcTests.cs(6,54): error CS1061: \u0027Calc\u0027 does not contain a definition for \u0027Add\u0027 and no accessible extension method \u0027Add\u0027 accepting a first argument of type \u0027Calc\u0027 could be found (are you missing a using directive or an assembly reference?)\n  removed: public int Add(int a, int b)\nfuse: 2 error(s) introduced in 2 file(s) (App, Lib.Tests); Lib declarations changed, 2 dependent project(s) checked"}],"isError":false},"id":3,"jsonrpc":"2.0"}
{"result":{"content":[{"type":"text","text":"fuse: unknown tool fuse_lint; the tools are fuse_check, fuse_test and fuse_build"}],"isError":true},"id":4,"jsonrpc":"2.0"}
```

The server reads its input as UTF-8 whatever the console's code page, and exits when the host closes its standard input.

## `fuse hook`

```text
fuse hook <harness> <event>
```

The command every installed hook runs. It reads the harness's JSON payload from standard input as UTF-8 and answers in that harness's format, which [Harnesses](harnesses.md) shows for each one. The harness is `claude`, `cursor`, `gemini`, `codex`, `copilot` or `opencode`. Any other harness or event prints a usage line on standard error and exits with 0.

| Event | What it does |
| --- | --- |
| `post-edit` | Checks the source files the edit wrote and reports introduced errors. |
| `pre-shell` | Rewrites `dotnet build` and `dotnet test` in a shell command to `fuse build` and `fuse test`. |
| `stop` | Checks every change when the agent tries to finish, and sends it back once while errors are introduced. |

**`post-edit`** reads the edited paths from the payload's tool input (`file_path`, `filePath`, `path` or `notebook_path`, and the files an `apply_patch` patch adds, updates, deletes or moves to), keeps the `.cs`, `.razor` and `.cshtml` files, and runs `fuse check` on them. Relative paths resolve against the payload's `cwd` (or the first of its `workspace_roots`), or the hook's own working directory when the payload names neither. For Claude Code, which runs the hook in the background, it waits for the engine to finish loading for up to 5 minutes, writes the check's output to standard error, and exits with 2, which wakes the agent. Every other harness runs the hook inline, so it answers only once the engine has loaded, waits up to 50 seconds, and writes its answer as JSON to standard output.

**`pre-shell`** reads the command from the tool input's `command`, and rewrites `dotnet build` and `dotnet test` (also `dotnet.exe`) where a command segment starts: at the beginning, or after `&&`, `||`, `;`, `|` or a line break. `cd Lib && dotnet build -c Release` becomes `cd Lib && fuse build -c Release`, and a command without either verb is left alone. Claude Code, Gemini CLI and OpenCode get the rewritten command. Codex gets it only when the whole command is one `dotnet build` or `dotnet test` with plain arguments, because its answer also approves the command ([Harnesses](harnesses.md#codex)). Cursor and GitHub Copilot CLI have no pre-shell hook.

**`stop`** checks every change, waiting for the engine for up to 5 minutes. It sends the agent back with the check's output and `Fix these errors before finishing; HEAD does not have them, so your changes introduced them.` When the payload says the agent is already continuing because of an earlier stop hook (`stop_hook_active` is `true`, or `loop_count` is above 0), it lets the agent finish, so it sends an agent back at most once in a row.

A hook reports only introduced errors and a missing restore. Every other outcome, including a loading engine, a timeout and an internal failure, exits with 0 and prints nothing (or `{}` where the harness expects JSON), so a hook never breaks the agent's session. A payload that is not valid JSON or not a JSON object, and an exception, are written to `hook.log` in the [state directory](troubleshooting.md#where-the-logs-are). Claude Code hooks whose payload comes from Cursor, which also runs Claude Code's settings, do nothing, so the Cursor hook answers there.

## `fuse --version` and `fuse help`

`fuse --version` (or `fuse version`) prints the product version:

```text
$ fuse --version
5.1.0
```

`fuse help` (or `--help`, `-h`, or no arguments) prints the usage shown at the top of this page to standard output and exits with 0.

`fuse engine <root>` is the engine process itself. Clients start it; there is no reason to run it by hand.

## Environment

Fuse has no configuration file and no environment variables of its own. When a client starts the engine, it looks for the `dotnet` host in `DOTNET_HOST_PATH` and `DOTNET_ROOT`, the variables the .NET SDK sets, before the runtime's own directory. Every child process Fuse starts gets `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_NOLOGO=1` and `DOTNET_CLI_UI_LANGUAGE=en`, so its output is in English and parses.

Fuse needs the .NET 10 SDK, git on the `PATH`, and restored projects. It writes the harness settings `fuse init` creates and the state directory in the user's local application data, and nothing else.

## Messages and fixes

When Fuse cannot answer, it prints `fuse:` and one of these messages and exits with 2. Each has a code, which the MCP server and the hooks act on: a hook reports `RestoreNeeded` and stays silent on the rest. Text in angle brackets varies.

### `NotARepository`

```text
fuse: not inside a git repository; Fuse compares your changes with HEAD, so it needs one
```

`fuse check`, `fuse test` and `fuse init` print it on standard error, and `fuse mcp` returns it, when the working directory is in no git repository. Fix: run the command inside the repository, or run `git init` and commit.

### `NoProjects`

```text
fuse: no C# projects (.csproj) in this repository, so Fuse has nothing to check
fuse: no C# project could be evaluated: <path>: <MSBuild message>
```

Git knows no `.csproj` file in the repository (the second line is `fuse init`'s), the project files git knows are missing from disk, or MSBuild could evaluate none of them. Fix: add or commit a project; for the last line, fix the project file the message names, which `dotnet build` also fails on.

### `Loading`

```text
fuse: Fuse is still loading this repository; the next check will include these changes
```

The engine answers a check with this while it is still evaluating the repository, when the check asked not to wait. Only the post-edit hook of a harness other than Claude Code asks that, and it stays silent on this answer. Fix: none; the stop hook and the next check cover the changes.

### `RestoreNeeded`

```text
fuse: restore needed: run `dotnet restore Lib/Lib.csproj` (Lib/Lib.csproj not restored)
```

A project the request needs, or a project it references, has no `project.assets.json`. The message names the first such project, adds `and the same for each project listed` when there are more, and lists up to three. Fix: run the command it names for each project listed. Fuse never restores on its own.

### `LoadFailed`

```text
fuse: could not load <project>: <message>
fuse: Fuse could not evaluate the repository's projects: <message>
fuse: git failed <action> in <root> (<git's message>); the repository may be damaged, see `git status` and `git fsck`
fuse: git cannot read <path> at HEAD; the repository may be damaged (run `git fsck`)
fuse: git cannot list the repository's files (<git's message>); see `git status`
```

A project could not be loaded (a missing project reference or an SDK that does not resolve, which `dotnet build` also fails on), the evaluation failed, or git could not read the repository. A NuGet restore warning, such as a package advisory, does not count as a failure. Fix: run `dotnet build` on the project to see the same failure, or `git status` and `git fsck` for the git messages.

### `Timeout`

```text
fuse: the Fuse engine did not answer within <seconds> s
fuse: the request is cancelled
```

The engine did not answer within the command's limit: 600 s for `fuse check`, `fuse test` and the MCP tools, 300 s for the Claude Code post-edit hook and every stop hook, 50 s for the other post-edit hooks. A request that reaches projects the engine has not loaded waits for them to load; in the [measurements](results.md) that took up to 25,166 ms for Jellyfin's dependent projects. The second line is the engine's answer when a request is cancelled while it runs. Fix: run the command again; the engine keeps loading in between. [Troubleshooting](troubleshooting.md#a-command-times-out) shows how to see what it is doing.

### `InvalidPath`

```text
fuse: a file named in the check is empty; name each file by its path
fuse: "<file>" named in the check is not a valid path
```

`fuse check` and the `fuse_check` tool print it when a file argument is empty or blank, is not a string, or is not a path (for example because it holds a NUL character), and check nothing. Fix: name each file by its path, relative or absolute.

### `Internal`

```text
fuse: internal error: <message> (details in <state directory>/engine.log)
fuse: the Fuse engine closed the connection (see <state directory>/engine.log)
fuse: the Fuse engine sent an answer this client cannot read (<reason>; see <state directory>/engine.log)
fuse: could not connect to the Fuse engine: <message>
fuse: could not start the Fuse engine: <message>
fuse: the Fuse engine sent no check result (<response>); run the command again
fuse: the Fuse engine sent no test plan (<response>); run the command again
```

Something failed inside Fuse. `could not connect to the Fuse engine: it did not start within 15 s` means the engine process did not open its pipe. Fix: read the lines of `engine.log` the message names, then run the command again; [Troubleshooting](troubleshooting.md#internal-errors) has the details to collect for a bug report.

### Other lines on standard error

```text
fuse: waiting for another build or test in this repository
fuse: running without the per-repository build lock: <reason>
```

`fuse build` and `fuse test` print the first once when another client holds the [build lock](how-it-works.md#builds-and-the-build-lock), and keep waiting. They print the second when the state directory cannot be written, and build without the lock.
