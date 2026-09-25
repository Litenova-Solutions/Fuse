# Design

Fuse answers two questions for a coding agent working in a .NET repository: which compiler errors did the agent's changes introduce, and which tests can those changes break. This document describes how it answers them. [README.md](../README.md) describes how to use it.

## Surfaces

Every surface calls the same three operations in `src/Fuse/Cli`: check, test and build.

- **Hooks** (`fuse hook <harness> <event>`) are the primary surface. A post-edit hook checks the edited files, a pre-shell hook rewrites `dotnet build` and `dotnet test` to `fuse build` and `fuse test`, and a stop hook checks all changes before the agent finishes. Hooks run without the agent deciding to call anything.
- **The CLI** (`fuse check`, `fuse test`, `fuse build`) serves people and agents that run shell commands.
- **The MCP server** (`fuse mcp`) serves `fuse_check`, `fuse_test` and `fuse_build` to hosts without hooks.

`fuse init` registers the hooks with each harness the repository uses and the MCP server with VS Code.

## Process model

One engine process runs per repository. It is `fuse engine <root>`, started detached by the first client that cannot connect, and it exits after 30 minutes without a request or when the repository disappears.

- **Identity.** The root is the nearest directory with `.git`, canonicalized through junctions, symlinks, `subst` drives and short names. A per-root mutex guarantees one engine; a per-root named pipe carries requests.
- **Protocol.** One JSON request and one JSON response per connection. Every request carries the client's build identity; an engine from another build answers `restart` and exits, and the client starts a matching one.
- **Isolation.** The engine runs from a private copy of the tool's binaries in local application data, so it never locks the installed tool. On Windows it is created without inheriting the caller's handles and outside the caller's job; on Linux and macOS it calls `setsid`.
- **State.** Logs, shadow test output and test results live in `<local application data>/fuse/repos/<id>`. Nothing is written inside the repository.
- **Guard.** A client starts an engine only in a repository that contains a `.csproj`, so hooks and commands cost one `git ls-files` call in other repositories.

## Projects and compilations

- **Evaluation.** At start the engine evaluates every `.csproj` git knows about (tracked or untracked, not ignored) with MSBuild, without running targets. For a multi-targeted project it evaluates each target framework and unions the results. The graph answers which projects own a file, which projects depend on a project, and which projects hold tests.
- **Lazy loading.** Nothing compiles at start. A check or test plan loads the projects it needs with `MSBuildWorkspace`, including their references. After a request, the engine loads the dependents of projects with uncommitted changes in the background, outside the request lock.
- **Two views.** The engine holds two immutable solutions: the working tree as it is on disk, and the same projects with every changed file restored to its HEAD content. The HEAD view changes only when HEAD moves or projects load, so its compilations and diagnostics stay cached across edits.
- **Staying current.** A file watcher marks paths dirty, and every request also checks the paths a hook names. A change is folded in when its content differs from what the engine holds. Project, props, targets, `global.json` and `.editorconfig` changes re-evaluate the graph; a branch switch or a burst of more than 300 files reloads.

## Check

1. Each target file (the edited files, or every file that differs from HEAD) is bound in both views, and the errors are diffed. A match is the same file, id and message, counted, ignoring line numbers.
2. The file's declarations are compared with HEAD from syntax alone. A body-only edit ends the check.
3. Otherwise each changed declaration is resolved to its symbol in the HEAD view, and Roslyn's `SymbolFinder` finds the files that reference it, implement or override it, or derive from its type, across the owning project and its dependents. An added member pulls in the same-named members of its type and its bases, and the implementations of an interface or abstract type. A changed type header, delegate, global using or assembly attribute re-checks every file in reach.
4. Candidate files bind in parallel. Past 500 candidates, whole projects are bound instead.
5. Errors are compiler errors, warnings the project treats as errors, and analyzer diagnostics whose configured severity is error. Only analyzers that can report an error run.

## Test selection

1. A type graph built from syntax (which types name which, with extension methods matched by method name) is walked backwards from the changed types to the test classes that can reach them. It takes milliseconds and is cached per file version.
2. When at most 40 test classes are reachable, a member-level walk refines the selection to test methods: Roslyn references backwards from each changed member, through callers, overridden members and implemented interface members, within an 8 second budget.
3. Application code (a project that is an executable or a web app) is reached through a host rather than by name, so reaching it selects every test project that depends on the application, as do top-level statements and Razor changes.

## Running tests

- **Fast path.** When every project a test assembly loads has build output, and nothing but C# sources changed since that build, Fuse copies the test project's output into a shadow directory, emits the changed assemblies (and their dependents) from the warm compilations into it, and runs `dotnet test` on the shadow assembly. Shadow runs execute in parallel, one per target framework.
- **Otherwise** it runs `dotnet test` on the project with the selection's filter, one project at a time.
- Results are read from TRX files. Output lists every failing test, with details for the first ten, and a line stating how many tests ran out of how many.

## Failure handling

Every failure maps to one error code with a message that names the fix: not a repository, no projects, loading, restore needed, load failed, timeout, internal. Hooks never break an agent's session: on any internal failure they exit 0 with no output and log to `hook.log`.

## Measurement

`evals/Fuse.Evals` measures the product through the `fuse` executable against real `dotnet build` and `dotnet test` runs: correctness over generated API mutations, test selection over generated behavior mutations, and latency. Results are in `evals/results`, and `dotnet run --project evals/Fuse.Evals -c Release -- chart` renders the site's chart from them.
