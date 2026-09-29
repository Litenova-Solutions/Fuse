# Troubleshooting

This page is for someone whose Fuse setup does not behave as expected: it lists the symptoms with their causes and fixes, then where Fuse's logs are, how to read them, and how to stop the engine. [Commands](commands.md#messages-and-fixes) lists every message Fuse prints when it cannot answer.

Most problems show in one of two logs in the repository's state directory: `engine.log`, which the engine writes for every request it serves, and `hook.log`, which a hook writes when it fails. [Where the logs are](#where-the-logs-are) gives the path.

## A hook prints nothing

A hook prints something only when the edit introduced errors or a project needs a restore. A clean edit, an engine that is still loading, a timeout and an internal failure all print nothing, so that a hook never breaks the agent's session.

1. Run `fuse check` in the repository. If it reports errors that the hook did not, the hook did not run or did not reach the engine; see [The agent does not see the hooks](#the-agent-does-not-see-the-hooks).
2. Read `hook.log`. A payload that is not valid JSON and an exception inside the hook are written there with the harness and the event.
3. Read the end of `engine.log`. A hook's check appears as a `CheckFiles` request with the edited file names ([reading a phase line](#reading-a-phase-line)).

## The agent does not see the hooks

- **The harness's folder did not exist when `fuse init` ran.** `fuse init` writes settings only for the harnesses it detects ([Harnesses](harnesses.md#detection)). Create the folder, for example `.cursor/`, and run `fuse init` again.
- **The harness cannot find `fuse`.** Every hook runs the command `fuse`, so the tools directory (`~/.dotnet/tools`, or `%USERPROFILE%\.dotnet\tools` on Windows) has to be on the `PATH` of the process that starts the harness, not only in your terminal.
- **Cursor with only Claude Code settings.** Cursor also runs Claude Code's hooks, and Fuse ignores those in a Cursor session so that the Cursor hooks answer. Create `.cursor/` and run `fuse init` again.
- **Cursor or GitHub Copilot CLI and `dotnet test`.** These two harnesses have no pre-shell hook, so their `dotnet build` and `dotnet test` run unchanged. Have the agent run `fuse test` and `fuse build` instead.

## `dotnet build` and `dotnet test` are no longer rewritten after an update

Fuse 5.1.0 names the shell event `pre-shell`. Settings written by 5.0.0 call `fuse hook <harness> pre-bash`, which 5.1.0 answers with a usage line on standard error and exit code 0, so the harness runs the command unchanged. Run `fuse init` again in each repository after updating; it replaces Fuse's hook entries and the OpenCode plugin and keeps everything else ([Harnesses](harnesses.md#running-fuse-init-again)).

The engine needs no action after an update. The first request from the updated client reaches the running engine of the earlier build, which answers `Restart` and exits; the client then starts an engine of its own build and sends the request again. `engine.log` records it as `client build <build> differs; exiting so the client can start a matching engine`, with the client's build id in place of `<build>`. A 5.0.0 engine cannot read the build id of a 5.1.0 request and leaves the place empty; a 5.1.0 engine writes `(none)` there for a request that carries no build id, such as one from a 5.0.0 client.

## Restore needed

```text
fuse: restore needed: run `dotnet restore Lib/Lib.csproj` (Lib/Lib.csproj not restored)
```

A project the request needs has no `project.assets.json`, and Fuse never restores on its own. Run the command the message names, for each project it lists. The message names a project rather than the solution, because a solution can leave out the project that is missing its restore. Every hook reports this message too, so an agent sees it and can run the command.

## Not a repository, or no projects

`fuse: not inside a git repository` means the working directory, or for an MCP server the directory it was started in, is in no git repository. `fuse: no C# projects (.csproj) in this repository` means git knows no `.csproj` file there, tracked or untracked and not ignored. In such a repository every hook returns at once without starting an engine, by design. Fix: run Fuse inside a git repository with a C# project, and commit once.

## The first answer after the engine starts is slow

The engine loads projects when a request first needs them, and a declaration change needs every dependent project loaded. The first check after the engine starts, and the first declaration change in a large repository, wait for that load; later requests reuse it. `engine.log` shows each load as `loaded <project> in <ms> ms`.

Harnesses other than Claude Code run the post-edit hook inline, so that hook does not wait for an engine that is still loading and prints nothing until the load finishes. The stop hook, which waits, reports those edits before the agent finishes.

## A command times out

```text
fuse: the Fuse engine did not answer within 600 s
```

The request waited longer than the command allows: 600 s for `fuse check`, `fuse test` and the MCP tools, 300 s for the Claude Code post-edit hook and every stop hook, 50 s for the other post-edit hooks. Read the end of `engine.log`: `loaded <project>` lines mean the engine is still loading, and the `phases` line of the request, when it finishes, shows where the time went. Run the command again; the engine keeps its progress between requests.

## Internal errors

```text
fuse: internal error: <message> (details in <state directory>/engine.log)
```

Something failed inside Fuse. `engine.log` holds the exception with its stack trace, on a line with `failed:`. `could not connect to the Fuse engine` and `could not start the Fuse engine` mean the client could not reach or start the engine process; `engine.log` then shows whether an engine started at all. Run the command again; if it keeps failing, [open an issue](https://github.com/Litenova-Solutions/Fuse/issues) with the command, the message, the `engine.log` lines around the failure, and the output of `fuse --version`.

## A build waits

```text
fuse: waiting for another build or test in this repository
```

Another `fuse build` or `fuse test` in the same repository holds the build lock, and this one waits until it finishes, which is what the lock is for. If nothing else is running, a `fuse` process is still alive; the lock is released when that process exits. `fuse: running without the per-repository build lock: <reason>` means the state directory cannot be written, and the build runs unprotected.

## Where the logs are

Fuse keeps its files for a repository in a state directory in the user's local application data:

| Operating system | State directory |
| --- | --- |
| Windows | `%LOCALAPPDATA%\fuse\repos\<id>` |
| Linux | `~/.local/share/fuse/repos/<id>`, or `$XDG_DATA_HOME/fuse/repos/<id>` when that is set |
| macOS | `~/Library/Application Support/fuse/repos/<id>` |

`<id>` is the first 16 hexadecimal digits of the SHA-256 of the repository's root path. The first line of each `engine.log` names the repository its engine served, so on Windows this finds the directory of `C:\src\my-repo`:

```powershell
Select-String -Path "$env:LOCALAPPDATA\fuse\repos\*\engine.log" -SimpleMatch "started for C:\src\my-repo (" -List | Select-Object -ExpandProperty Path
```

On Linux and macOS, search the `engine.log` files under the directory in the table for `started for` followed by the repository's path.

The state directory holds:

- `engine.log`: one line per event the engine records, each with a timestamp. The engine moves a log larger than 4 MB to `engine.log.1` when it starts.
- `hook.log`: the failures of hooks run in this repository.
- `build.lock`: the [build lock](how-it-works.md#builds-and-the-build-lock).
- `results`, `shadow` and `analyzers`: test results while a run lasts, the copies used for test runs without MSBuild, and the copies of the repository's own analyzers.

The engine itself runs from a copy of the tool in `fuse/engine/<version>-<module id>` in the same local application data.

## Reading engine.log

These lines are from the engine of the [getting started](getting-started.md) sample, from its start to the test run, with some lines left out and the repository path shortened:

```text
2026-09-29 04:53:59.381 engine 5.1.0/1dc40ab422eb4433bf83d9de75ed354e started for C:\...\fuse-sample (pid 42416)
2026-09-29 04:54:00.054 evaluated 3 projects in 563 ms (0 failed)
2026-09-29 04:54:00.055 projects: App (1 sources, app), Lib.Tests (1 sources, tests), Lib (1 sources)
2026-09-29 04:54:09.561 loaded Lib in 1922 ms (1 projects open)
2026-09-29 04:54:14.614 loaded App in 2031 ms (2 projects open)
2026-09-29 04:54:16.273 loaded Lib.Tests in 1657 ms (3 projects open)
2026-09-29 04:54:17.605 check: binding 1120 ms, analyzers 526 ms (summed over files); 1 target(s) in 2901 ms, 1 with declaration changes, 3 file(s) bound, 7960 ms total
2026-09-29 04:54:17.607 CheckChanges took 10157 ms
2026-09-29 04:54:17.608 phases id=36420-1 kind=CheckChanges gate=0.0 sync=119.2 load=2067.6 bindTargets=2901.9 surfaceDiff=49.5 loadDependents=3684.6 referenceSearch=953.1 bindCandidates=369.0 total=10157.0
2026-09-29 04:54:22.943 check: binding 0 ms, analyzers 0 ms (summed over files); 1 target(s) in 0 ms, 1 with declaration changes, 3 file(s) bound, 5 ms total
2026-09-29 04:54:22.943 CheckFiles Calc.cs took 15 ms
2026-09-29 04:54:22.944 phases id=45888-1 kind=CheckFiles gate=0.0 sync=3.3 load=0.0 bindTargets=0.7 surfaceDiff=2.6 loadDependents=0.0 referenceSearch=1.9 bindCandidates=0.6 total=15.0
2026-09-29 04:54:37.837 test plan: selection in 71 ms: Lib.Tests 1 pattern(s)
2026-09-29 04:54:38.277 test plan: Lib.Tests [Lib.Tests] runs from C:\...\shadow\Lib.Tests\Lib.Tests.dll after 512 ms
2026-09-29 04:54:38.279 PlanAffectedTests took 516 ms
2026-09-29 04:54:38.280 phases id=36068-1 kind=PlanAffectedTests gate=0.0 sync=1.3 selection=71.5 mirror=439.8 total=516.0
```

The first check after the rename loaded all three projects and took 10,157 ms; the post-edit hook's check of the same edit took 15 ms. A check logs a `check:` line with its binding and analyzer time, and a test plan logs its selection per test project and whether each runs from the copy (`runs from ...`) or `builds with MSBuild`, with the reason when it does not run from the copy.

### Reading a phase line

Every request ends with a request line, `<request> took <ms> ms`, and a phase line:

```text
phases id=<client process id>-<n> kind=<request> <phase>=<ms> ... total=<ms>
```

- `id` names the client process and its request count, so a line can be matched to the command that sent it.
- `kind` is the request: `CheckChanges` (every change), `CheckFiles` (named files, which is what a post-edit hook sends), `PlanAffectedTests` (`fuse test`) or `PlanAllTests` (`fuse test --all`).
- The phases follow in the order they ran, in milliseconds:
    - `gate`: waiting for the requests ahead of it.
    - `sync`: folding the file changes since the previous request into both views.
    - `load`: loading the projects that own the checked files, or for a test plan the projects it needs.
    - `loadDependents`: loading the dependents of the projects with a declaration change.
    - `bindTargets`, `surfaceDiff`, `referenceSearch`, `bindCandidates`: the steps of a check, as [How it works](how-it-works.md#check) describes them.
    - `selection` and `mirror`: selecting the tests, and preparing the copies for runs without MSBuild.
    - `total`: the whole request from the moment it held the request lock. It is counted in system clock ticks, so on Windows a request that took less than about 16 ms shows `total=0.0`.

## Stopping or restarting the engine

The engine exits on its own after 30 minutes without a request, when its repository directory is deleted, and when a client of another build connects. Ending the engine's process is safe: it holds no lock and writes nothing into the repository, and the next command or hook starts a fresh engine.

The engine is a `dotnet` process whose command line holds `fuse.dll engine` and the repository's root path. On Windows, this lists the running engines:

```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object CommandLine -like '*fuse.dll engine *' | Select-Object ProcessId, CommandLine
```

and this stops the engine of `C:\src\my-repo`:

```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object CommandLine -like '*fuse.dll engine C:\src\my-repo' | ForEach-Object { Stop-Process -Id $_.ProcessId }
```

On Linux and macOS, end the process whose command line holds `fuse.dll engine` and the repository's path, for example with `pkill -f` and that text.
