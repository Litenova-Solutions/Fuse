# Limits

This page lists what Fuse cannot check and where it behaves differently from `dotnet build` and `dotnet test`, each with what to do about it. Read it before you rely on a clean answer from Fuse, especially in a repository with custom build logic.

Fuse answers from a Roslyn compilation of what MSBuild's evaluation describes, and compares the working tree (the files on disk) with HEAD (the commit you have checked out). It does not see what lies outside that model. `fuse build` runs the real build and `fuse test --all` runs every test when you need the complete answer.

## Scope and requirements

- **C# only.** Fuse evaluates `.csproj` projects and checks `.cs`, `.razor` and `.cshtml` files. Visual Basic and F# projects are not evaluated. Build those with `dotnet build`.
- **The .NET 10 SDK, git and restored projects.** Fuse runs `git` from the `PATH` and never runs `dotnet restore`. When a project is not restored, it answers with the command to run ([Commands](commands.md#restoreneeded)).
- **Only changes against HEAD.** An error that is already committed is part of HEAD and is never reported, and neither is an error that exists at HEAD and in the working tree alike. Run `fuse build` to see every error the solution has.
- **A repository needs a commit.** With no commit, every file counts as added, so every error counts as introduced. Commit once before relying on a check.
- **Platforms.** CI builds and tests Fuse on Windows, Linux and macOS (`.github/workflows/ci.yml`); the [measurements](results.md) come from one Windows machine.
- **Letter case in paths on macOS.** Fuse compares paths ignoring case on Windows and exactly on Linux and macOS. The default macOS volume ignores case, so a file named to `fuse check` in another letter case than it has on disk is not matched to the file git knows. Name files as they are spelled on disk; hooks already do.

## What a check does not see

- **Compile inputs outside evaluation.** A check compiles the sources, references, analyzers and options that MSBuild's evaluation lists, without running targets. Custom targets that change what the compiler receives, source generators that read files outside the project's additional files, and IL weaving (rewriting assemblies after the compiler runs) are invisible to it. Run `fuse build` in such a repository before finishing a change.
- **Changes to build configuration.** Both of Fuse's views, the working tree and HEAD, use the project files as they are on disk. A change to a `.csproj`, `.props`, `.targets`, `.editorconfig`, `.globalconfig` or `global.json` file on its own is not compared with HEAD, and the files it affects are checked only when they changed too. Run `fuse build` after changing build configuration.
- **Compilation-end analyzers.** Analyzers that report only after seeing the whole project run only when a check compiles whole projects, which happens past 500 files in reach. Otherwise `fuse build` reports them.
- **Warnings.** A check reports errors only: compiler errors, warnings the project treats as errors, and analyzer diagnostics configured as errors.
- **Matching.** An error counts as introduced when HEAD has no error in the same file with the same id and message. An edit that removes one error and causes an identical one in the same file reports nothing.
- **Differences from the build's wording.** The build compiles a dependent project against reference assemblies, which leave out private members, and Fuse compiles against the source. After a member is made private, the build says "does not contain a definition" and Fuse says "is inaccessible due to its protection level", at the same position.
- **Output limits.** An answer holds at most 200 errors, of which the command prints 20, and at most ten cause lines. Fix what is printed and check again, or run `fuse build` for the full list.

## Test selection

- **Static selection.** Fuse selects tests from the code as written. A test that reaches the changed code only through reflection, a type name in a string or configuration, or an assembly loaded at run time is selected only when the change reaches an application host, which selects every test project that depends on the application. Run `fuse test --all` before finishing a change that such tests cover.
- **Counts from source.** The summary counts test methods from source, so a theory with several cases counts once, while the failed and passed counts come from the test run.
- **Microsoft.Testing.Platform.** A test project on Microsoft.Testing.Platform (opted in through `global.json`) runs whole and builds with MSBuild. It writes no TRX file, so Fuse cannot list its failures: a failing run is reported as a test run that exited with its exit code and produced no results, after the last 30 lines of the run's output. Read those lines, or run that project with `dotnet test` for its full report.

## Test runs without MSBuild

- **Resources and content files.** A run without MSBuild takes resources and content files from the last real build. When any file other than a source file changed in a project directory since that build, Fuse builds with MSBuild instead, which is slower but complete.
- **Timestamps.** Freshness is judged by timestamps, not content, so saving a resource file unchanged also sends the run through MSBuild.

## Builds and the build lock

- **Only builds through Fuse take the lock.** A `dotnet build` run directly, by a person, a script, an IDE, or an agent in a harness without a pre-shell hook (Cursor and GitHub Copilot CLI), can still collide with a build Fuse runs. Run builds and tests through `fuse build` and `fuse test` in a repository that several agents work in.
- **What the pre-shell hook rewrites.** The hook rewrites `dotnet build` and `dotnet test` where a command segment starts. `dotnet msbuild`, `dotnet run`, `dotnet publish` and scripts that call `dotnet` themselves run as they are. In Codex it rewrites only a command that is one `dotnet build` or `dotnet test` with plain arguments, so `cd Lib && dotnet build` runs there as it is ([Harnesses](harnesses.md#codex)).

## The engine

- **Analyzers the repository builds.** The engine loads an analyzer the repository builds itself as it was when its project loaded. A rebuilt analyzer takes effect when projects next reload: after a project file change, a commit or branch switch, more than 300 changed files at once, or an engine restart. After changing an analyzer, [restart the engine](troubleshooting.md#stopping-or-restarting-the-engine).
- **Loading.** The first request that reaches a project loads it, and the engine keeps every loaded project in memory; [Results](results.md) gives measured load times and memory. A post-edit hook in a harness other than Claude Code does not wait for loading, so the stop hook reports the first edits after the engine starts.
