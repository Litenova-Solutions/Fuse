# Architecture

This page describes how Fuse's code is organized: its layers, which namespace may use which, the order to read it in, the types each feature is built from, and the words those types are named with. It is for contributors and coding agents changing Fuse. Read it before adding a namespace, a type that other namespaces use, a step to the check or test pipeline, or a word to the output. [How it works](how-it-works.md) describes what Fuse does; this page describes where that behavior lives and what it is called.

Two tests hold the code to this page: `tests/Fuse.Tests/Architecture/NamespaceDependencyTests.cs` for the layers and `PathComparisonTests.cs` for how paths compare. A change that contradicts a rule here changes the page, the tests and the code together, and records a decision in [Decisions](#decisions).

## Principles

1. **Abstractions come first in reading order.** Each feature declares its concepts as small model types (records and their variants) before any class that computes them. A reader learns what a check is from its model, and how it is computed from its steps.
2. **Dependencies point down.** A namespace uses only the layers below it. No cycle exists between namespaces, and a test fails the build when one appears.
3. **One responsibility per type.** A class holds one step, one rule or one piece of state. A class that needs several paragraphs to describe is several classes.
4. **Each abstraction takes the form that states it most directly.** A model record, a variant hierarchy, a class per step, an abstract class and an interface are all abstractions; an interface is worth adding for a seam or a contract even with one implementation. The one role Fuse implements several times is the harness ([Harnesses](#harnesses)).
5. **Variants replace nulls that change control flow.** When code branches on whether a value is null, the value is two concepts, and each gets a named case. A null that is only printed, such as an optional message on a wire record, can stay.
6. **Features do not know the wire.** Check and Testing return domain results. The engine maps them to `Protocol` records in one place.
7. **One word per concept.** [Vocabulary](#vocabulary) is the authority for names, and [Naming rules](#naming-rules) decide the next one.

## Layers

Listed from the top. Each layer may use the layers below it, with the exceptions stated in [Dependency rules](#dependency-rules).

| Layer | Namespaces | Owns |
| --- | --- | --- |
| Composition | `Fuse` (`Program`) | Parsing the command line and choosing a surface or the engine |
| Surfaces | `Fuse.Hooks`, `Fuse.Mcp`, `Fuse.Harnesses` | `fuse hook`, `fuse mcp`, and `fuse init` with one type per harness |
| Operations | `Fuse.Operations` | Check, test and build as a client runs them, and rendering their output |
| Client | `Fuse.Engine.Client` | Finding, starting and calling the engine |
| Wire | `Fuse.Protocol` | Request and response records, their JSON, and `EngineVersion` |
| Engine | `Fuse.Engine` | The engine process: pipe server, request routing, preload, mapping results to `Protocol` |
| Features | `Fuse.Check`, `Fuse.Testing` | The check algorithm, and test selection and planning |
| Changes | `Fuse.Changes` | Which declarations differ between HEAD and the working tree, from syntax |
| Workspace | `Fuse.Workspace` | The current and baseline Roslyn solutions, project loading, and keeping both views current |
| Sources | `Fuse.Repo`, `Fuse.Graph`, `Fuse.Dotnet` | git and the working tree, MSBuild evaluation, and child processes with their output parsers |
| Foundation | `Fuse.Paths`, `Fuse.Failures`, `Fuse.Telemetry` | `RepoRoot`, `RepoPath` and `PathRules`; `FuseException` and `ErrorCode`; `PhaseTimes`, `Phase` and `EngineLog` |

The client and the engine are separate processes. `fuse init`, `fuse hook`, `fuse check`, `fuse test`, `fuse build` and `fuse mcp` run in a short-lived client. `fuse engine <root>` runs the long-lived engine, which alone loads MSBuild and Roslyn. The layers above Wire run in the client and the layers from Engine down run in the engine; Wire, Foundation, `Fuse.Repo`, `Fuse.Dotnet` and `Fuse.Check.Model` (for `CompilerError`, [D11](#decisions)) are shared.

## Dependency rules

| Namespace | May use |
| --- | --- |
| `Fuse.Paths`, `Fuse.Failures`, `Fuse.Telemetry` | nothing in Fuse |
| `Fuse.Dotnet` | Foundation |
| `Fuse.Repo` | `Fuse.Dotnet`, Foundation |
| `Fuse.Graph` | `Fuse.Repo`, `Fuse.Dotnet`, Foundation |
| `Fuse.Workspace` | Sources, Foundation |
| `Fuse.Changes.Model` | Foundation |
| `Fuse.Changes` | `Fuse.Changes.Model`, `Fuse.Workspace`, Sources, Foundation |
| `Fuse.Check.Model`, `Fuse.Testing.Model` | `Fuse.Changes.Model`, Foundation; never `Fuse.Changes` |
| `Fuse.Check`, `Fuse.Testing` | their own `Model` namespace, `Fuse.Changes`, `Fuse.Changes.Model`, `Fuse.Workspace`, Sources, Foundation; never each other |
| `Fuse.Engine` | Features and everything below them, `Fuse.Protocol` |
| `Fuse.Protocol` | Foundation, `Fuse.Check.Model` (for `CompilerError` only, [D11](#decisions)) |
| `Fuse.Engine.Client` | `Fuse.Protocol`, `Fuse.Repo`, `Fuse.Dotnet`, Foundation |
| `Fuse.Operations` | `Fuse.Engine.Client`, `Fuse.Protocol`, `Fuse.Repo`, `Fuse.Dotnet`, Foundation |
| `Fuse.Harnesses` | `Fuse.Operations`, `Fuse.Protocol`, `Fuse.Repo`, Foundation |
| `Fuse.Hooks` | `Fuse.Harnesses`, `Fuse.Operations`, `Fuse.Protocol`, `Fuse.Repo`, Foundation |
| `Fuse.Mcp` | `Fuse.Operations`, `Fuse.Engine.Client`, `Fuse.Protocol`, `Fuse.Repo`, Foundation |
| `Fuse` (`Program`) | any namespace |

Three rules carry most of the value:

1. **Features never use `Fuse.Engine`, `Fuse.Protocol` or anything above them.** A check is testable without a pipe, and the wire can change without touching the algorithm.
2. **No client namespace uses `Microsoft.CodeAnalysis`, `Microsoft.Build`, or a Fuse namespace that does.** Those are `Fuse.Graph`, `Fuse.Workspace`, `Fuse.Changes`, the features and `Fuse.Engine`. A hook pays for every assembly it loads, and the runtime loads one as soon as a method that uses it runs, so a single client call into an engine namespace would load Roslyn into every hook. The test follows every namespace a client namespace uses, directly or through others, and fails when that reaches Roslyn, MSBuild or an engine-side namespace.
3. **`Program` is the only type that references both the client and the engine**, because it is where `fuse engine` and the client commands are told apart.

`tests/Fuse.Tests/Architecture/NamespaceDependencyTests.cs` enforces the table and rule 2. It reads every `.cs` file under `src/Fuse`, takes the file's namespace from its `namespace` line, and collects every `Fuse.X`, `Microsoft.CodeAnalysis` and `Microsoft.Build` name in its `using` directives and in fully qualified references. A type a file names from an enclosing namespace without a `using`, such as a `Fuse.Engine` type in `Fuse.Engine.Client`, counts too, and the test also fails on a cycle between namespaces. It fails naming the file, the namespace it used and the rule it broke. A new namespace fails the test until it has a row, which is the point at which its layer is decided. The table on this page and the test change together.

## Reading order

For a contributor new to the code:

1. [How it works](how-it-works.md): what Fuse does and why.
2. `Fuse.Protocol`: the questions the engine answers and the shape of each answer. It is small and is the product's contract.
3. `Fuse.Check.Model`, then the steps in `Fuse.Check` in the order [Check](#check) lists them.
4. `Fuse.Testing.Model`, then the steps in `Fuse.Testing`.
5. `Fuse.Changes.Model`, then `Fuse.Changes`, `Fuse.Workspace` and the Sources layer, when a change needs them.
6. `Fuse.Engine`, `Fuse.Engine.Client`, `Fuse.Operations` and the surfaces, for how a request travels.

Model types sit in a `Model` folder and namespace inside their feature (`src/Fuse/Check/Model`, namespace `Fuse.Check.Model`). A model type uses only other model types of its feature, `Fuse.Changes` model types and Foundation, so a reader can understand the folder without opening anything else.

## Foundation

| Type | Namespace | What it is |
| --- | --- | --- |
| `RepoRoot` | `Fuse.Paths` | The canonical repository root, its pipe name and its state directory. |
| `RepoPath` | `Fuse.Paths` | A file or directory of the repository, by its absolute path. A `readonly struct` that holds the root, the absolute path and its hash, computed once; it compares the way the file system does (ignoring case on Windows, ordinally elsewhere), so every set and dictionary of paths takes no comparer. `Relative` gives the repository-relative path with forward slashes, `FileName` the last segment, `IsUnder` whether it lies in a directory, and `Matches` whether a path Roslyn or MSBuild reports is the same spelling, which needs no canonicalization. `RepoRoot.PathOf` is the only way to make one: it takes a path absolute or relative to the root, and canonicalizes any spelling not under the root's own (a junction, a symlink, a `subst` drive), so each file has one value. A path outside the root, such as a project in a sibling folder, is a value too, and its relative form starts with `../`. The wire, command-line arguments and the Roslyn and MSBuild APIs keep strings: `RequestRouter` turns the files a request names into values and answers an empty or invalid one with `ErrorCode.InvalidPath`, and `ResponseMapper` writes them back as absolute strings. `tests/Fuse.Tests/Architecture/PathComparisonTests.cs` fails when a string comparison chosen by the operating system appears outside `RepoPath`, when code outside `Fuse.Paths` uses `RepoPath.Comparison`, or when `Fuse.Protocol` names `RepoPath`. |
| `PathRules` | `Fuse.Paths` | Which files are sources, project inputs or build output, which the client and the engine both use. |
| `FuseException`, `ErrorCode` | `Fuse.Failures` | A failure that ends a request, with the message that names the fix. `ErrorMessages` holds each message that does not depend on the request, so every surface words a failure the same way ([D8](#decisions)). |
| `PhaseTimes`, `Phase`, `PhaseLine` | `Fuse.Telemetry` | The timed phases of one request, the phase names as constants, and the one log line that carries them. The engine writes the line and `evals/Fuse.Evals` reads it, so the names and the format are a contract and live in one place. `PhaseTimes.None` records nothing, so no caller passes or checks a null collector. |
| `EngineLog` | `Fuse.Telemetry` | The engine log writer. Features write to it through `RepoWorkspace.Log`, so none of them takes a second constructor argument. |

## Changes

`Fuse.Changes` answers one question for both features: which declarations differ between a file at HEAD and on disk, decided from syntax. The features ask it in two forms, whether a file's visible declarations changed (Check) and whether any declaration's code changed (Testing), so there are two functions. Both read a file the same way, into the same declarations under the same key.

**Model** (`Fuse.Changes.Model`). It uses only Foundation and no Roslyn, so a feature's model may use it:

| Type | Cases or fields |
| --- | --- |
| `DeclarationKey` | `Using(directive)`, `GlobalUsing(directive)`, `AssemblyAttribute(attributes)`, `TopLevelStatements`, `NamedType(name)` for a type or delegate, `Member(NamedType container, signature)` |
| `DeclarationChange` | `Added(after)`, `Removed(before)`, `Changed(before, after)`, each with its key and the names other code reaches it by; the texts are the header as written |
| `FileChanges` | the declaration changes of one file, and `HasBroadChange` |

A key is read from syntax alone. A member's signature holds its kind, name, explicit interface, arity and parameter types with their modifiers, `checked` for a checked operator, and a conversion's target type, so overloads, a static constructor, an explicit implementation, a checked operator and two conversions to one type each have their own key. An extension block has no name, so its key is its receiver's type. A field has one key per variable. When a file declares one key more than once (the parts of a partial type or member, or two extension blocks for the same receiver), the parts are one declaration: its surface holds every part's, so a change to any part is a change to the declaration, and `CodeDiff` compares the parts one by one.

**Steps** (`Fuse.Changes`):

1. `FileDeclarations`: reads one version of a file into its declarations, each a `DeclarationNode` with its key, its syntax node, its surface (what other files can observe, with bodies and trivia removed; none for a finalizer or top-level statements), the names other code reaches it by, and its containing type. It is the one walk of namespaces, types and members that both functions use.
2. `DeclarationHeader`: a declaration's header as written, on one line. `DeclarationChange` carries it and a cause quotes it.
3. `SurfaceDiff`: Check's question. It compares the surfaces of two `FileDeclarations` and returns `FileChanges`, which is broad when a type header, a delegate, a global using or a file-level attribute list changed.
4. `CodeDiff`: Testing's question. It compares the code of the same declarations, a type by its header (its surface) and anything else by its whole syntax, and returns the nodes the test walks start from: each added or changed declaration, the type of each removed member, the compilation unit when top-level statements change, and every type, and the compilation unit of a file with top-level statements, when the file's usings (at file level or inside a namespace) or file-level attributes change.

## Check

The steps follow the numbered list in [How it works](how-it-works.md#check). Each is one class.

**Model** (`Fuse.Check.Model`):

| Type | Cases or fields |
| --- | --- |
| `CheckScope` | `AllChanges`, `Files(IReadOnlyList<RepoPath>)` |
| `Reach` | `None` (no declaration change), `Broad(IReadOnlySet<RepoPath>)` (every file in the reached projects), `Precise(IReadOnlyDictionary<RepoPath, Cause>)` |
| `Cause` | `Changed(declaration)`, `Removed(declaration as it was at HEAD)` |
| `IntroducedError` | `CompilerError`, `Cause?`, and `IsCauseLeftOut` when the cap on causes left its cause out |
| `CheckResult` | introduced errors, files checked, the projects the errors are in, projects with declaration changes, dependents checked, whether whole projects were checked |

`CompilerError` lives in `Fuse.Check.Model` and `Fuse.Protocol` uses it, because the wire carries it unchanged ([D11](#decisions)). The cause is nullable because only rendering reads it: an error in a target file, an analyzer error and an error past the cap all print with no cause line.

**Steps** (`Fuse.Check`):

1. `TargetResolver`: turns a `CheckScope` into the source files that have an owning project.
2. `IntroducedErrors`: binds files or whole projects in both views and keeps the errors with no match at HEAD, using `DiagnosticCollector`, `AnalyzerSelector` and `DiagnosticDelta`.
3. `ChangeReach`: turns the declaration changes of the targets, as `SurfaceDiff` finds them, into a `Reach`.
4. `CandidateBinding`: binds the reached files, or whole projects past 500 candidates.
5. `CauseLines`: attaches causes to errors in candidate files, at most ten per answer.
6. `Checker`: answers one check request, behind the engine's request lock. It syncs the workspace, loads the owners of the targets and later the dependents of the projects with declaration changes, runs steps 1 to 5 in order, records each as a `Phase`, and returns a `CheckResult`.

## Testing

**Model** (`Fuse.Testing.Model`):

| Type | Cases or fields |
| --- | --- |
| `TestScope` | `Affected`, `All` |
| `TestSelection` | `Whole(reason)`, `Methods(ImmutableHashSet<string> patterns)` |
| `RunMode` | `Shadow(assembly)`, `Build` |
| `PlannedRun` | test project, name, `RunMode`, filter, whether it uses Microsoft.Testing.Platform |
| `TestPlanResult` | one `PlannedRun` per run, selected and total test counts, summary |

A selection never reaches the wire: `TestFilter` turns it into the filter of a run. The run mode does, as `Protocol.TestRunMode`, a record with the same two cases that System.Text.Json writes with a `kind` property (`"mode":{"kind":"Shadow","assembly":"..."}` or `"mode":{"kind":"Build"}`) through `[JsonPolymorphic]` and `[JsonDerivedType]`. It is a second record because `Fuse.Protocol` may not use a feature's model beyond [D11](#decisions), and naming rule 5 keeps the two names apart.

**Steps** (`Fuse.Testing`), following [How it works](how-it-works.md#test-selection):

1. `TypeGraph`: which types name which, built from syntax with each file's facts cached by text version, and its reverse closure.
2. `TypeWalk`: the class-level answer. It walks `TypeGraph` back from the types that declare a changed declaration and selects each test class it reaches. A reached test file without classes selects its project whole, and a reached application applies `HostRule`.
3. `MemberWalk`: the member-level refinement within its budget of 300 symbols and 8 seconds. It also seeds both walks: it finds each changed file's changed declarations with `CodeDiff`, selects the ones in test projects at once, and selects the test projects behind an application whole when its top-level statements change.
4. `HostRule`: application code reached through an application host selects every test project that depends on the application. It decides which members an application host or a framework calls (`IsEntryPoint`, `IsFrameworkInvoked`, `IsCalledByHost`) and which test projects that selects (`DependentTestProjects`).
5. `TestSelector`: chooses between the walks and merges their selections. The class-level answer is the projects the seeds selected whole, overlaid by the type walk, whose whole selections replace the seeds' reasons. The member walk's answer replaces it only when at most 40 test classes are reachable and the walk finishes, and then a project the seeds selected whole keeps the class-level answer's reason, so the summary does not depend on which answer was returned. `SelectionBuilder` holds one walk's selections while it runs.
6. `TestFilter`: builds the VSTest filter for a `TestSelection` and collapses it when it grows past 8,000 characters, to class prefixes and then to no filter.
7. `ShadowEmitter`: prepares a shadow run and returns `RunMode.Shadow`, or returns `RunMode.Build` when a shadow run is not safe.
8. `TestPlanner`: turns the selections into a `TestPlanResult`, with `TestCounter` counting tests from source for the summary, and records the `sync`, `selection` and `mirror` phases.

## Workspace and sources

`Fuse.Repo` finds which files differ from HEAD, and `Fuse.Workspace` keeps the current and baseline solutions in step with them. Every request starts with a sync: `ChangeTracker` folds what changed into its set and returns a `SyncResult`, and `WorkspaceSync` acts on it.

**Change tracking** (`Fuse.Repo`):

1. `HeadResolver`: the commit HEAD points at and whether git can read it. `GitHead` reads the ref files, loose or packed, without starting git; git answers when they do not (reftable refs, unusual layouts), and a new HEAD is checked with `git cat-file` so a broken commit cannot pass as clean. `HeadResolver.GitFailure` words the failure when git cannot read the repository, which `GitStatus` reports too.
2. `GitStatus`: the seed. It reads the NUL-separated records of `git status --porcelain=v1 -z` and takes each path exactly as git wrote it, since a name may begin or end with a space, hold a quote or not be ASCII.
3. `WatchedPaths`: the file watcher and what it recorded since the last sync, which `Drain` hands over as `WatchedChanges`: the sources, the directories that are gone, the project files that changed and the watcher's errors. It also decides which paths are ignored (git's own directory, and `bin`, `obj`, `.git` and `node_modules` folders).
4. `HeadComparison`: whether a file on disk differs from HEAD. Every carriage return is skipped on both sides, so a checkout that converts line endings is no change, and a file on one side only always is.
5. `ChangeTracker`: composes them. It holds HEAD and the set of changed sources, and reads a file at HEAD through `GitBlobReader`. After HEAD moves, after a watcher error, or when more than 300 sources changed (`MaxPatchedPaths`), it seeds the set again from `GitStatus`, because the recorded events are not the whole change; otherwise it compares each reported path with HEAD.

`SyncResult` has one case per response, and `ChangeTracker` chooses them in this order:

| Case | Chosen when | Carries | `WorkspaceSync` then |
| --- | --- | --- | --- |
| `Reevaluate(Paths, Trigger)` | HEAD moved, the watcher reported an error, or a project, props, targets, editorconfig or global.json file changed | the files to apply again; the trigger names the new commit, the watcher's error or the last project file | evaluates every project, closes them all, reopens the ones that were loaded and applies the files again |
| `Reload(Paths, Trigger)` | more than 300 sources changed, and none of the above | the same; the trigger names how many files changed | the same, without evaluating |
| `Patch(Paths, VanishedDirectories)` | otherwise | the files to apply and the directories that are gone | applies each file, and each file it holds under a vanished directory, to both views, after deriving them again if the loader holds a project they lack |

Each case is one response, so a combination that cannot happen cannot be written. A moved HEAD always re-evaluates, because the commit it moved to can have other project files. The engine log names the case and the trigger, as in `reloading: Reevaluate trigger=HEAD moved to <commit>` and `reloading: Reload trigger=<count> changed files`.

**Workspace** (`Fuse.Workspace`):

1. `ProjectLoader`: evaluates every project into the `RepoGraph` and opens the ones requests need in an MSBuildWorkspace, which is used only as a loader because its `TryApplyChanges` writes to disk. It refuses a project that is not restored (`RestoreNeeded`, naming the `dotnet restore` to run) or that did not load (`LoadFailed`, with `LoadFailure` telling a real failure from a restore warning), and increments `ConfigurationGeneration` whenever it closes every project. A background load (`PreloadAsync`) opens a project outside a request, which neither view holds until the next request derives them again.
2. `SolutionViews`: the current and baseline solutions, both derived from the loader's solution with the repository's own analyzers loaded from a copy (`AnalyzerShadow`), and `BaselineGeneration`, which changes only when the baseline's content does. It applies a file's disk content to the current solution and its HEAD content to the baseline, adding or removing documents as needed, and remembers every path it applied so that a rebuild from the loader applies them again. It also remembers the loader solution it last finished deriving both views from, so `HoldEveryOpenProject` says whether the loader has opened a project since.
3. `WorkspaceSync`: acts on each `SyncResult`, and folds the projects the loader opens into both views (`EnsureLoadedAsync`). A result it did not finish acting on, such as a re-evaluation after HEAD moved that was cancelled or failed, is kept, and the next sync acts on it followed by its own result (`SyncResult.Then`, where the case that does more wins), because the tracker has already taken the new HEAD and does not report the move again. A rebuild at a HEAD other than the one the baseline was read at increments `BaselineGeneration`. It rebuilds both views whenever `HoldEveryOpenProject` is false, which covers a request's own load, a background load, and a load that a cancelled or failed request cut short after some projects opened, all in one rebuild.
4. `RepoWorkspace`: the entry point the features use. It composes the tracker and the three classes above and adds no rule of its own.

## Harnesses

A harness is an agent host that runs Fuse's hooks: Claude Code, Cursor, Gemini CLI, Codex, GitHub Copilot CLI and OpenCode. `Fuse.Harnesses` holds a `Harness` base class with six implementations, `ClaudeCode`, `Cursor`, `GeminiCli`, `Codex`, `CopilotCli` and `OpenCode`. Adding a harness means adding one class and listing it in `SupportedHarnesses`. Each implementation owns everything about its harness:

- its `Name` on the command line (`claude`, `cursor`, `gemini`, `codex`, `copilot`, `opencode`), which every registration already written into users' settings contains, so it never changes;
- how to detect it in a repository (`IsUsedIn`, from its settings directory or instructions file), and where and how its hooks are registered (`RegisterHooks`), including the permission allowances Claude Code gets for `fuse build` and `fuse test` and the OpenCode plugin, `Harnesses/opencode-plugin.js`, which the tool embeds;
- how it answers a pre-shell, post-edit and stop event: `ReplaceShellCommand`, `ReportAfterEdit`, and `AllowStop` or `BlockStop`, each returning a `HookAnswer` with the standard output, standard error and exit code the harness reads;
- whether it runs the post-edit hook in the background (`RunsPostEditInBackground`), and whether Cursor also runs the hooks in its settings (`IsAlsoRunByCursor`). Both are true for Claude Code only.

The base class holds only that contract and the helpers the six share: the hook command for an event, detection from paths in the repository, and replacing Fuse's handler in the nested hook format Claude Code, Gemini CLI and Codex use. Each harness's hook configuration keeps the harness's own event and tool names (Claude Code's `PreToolUse` with a `Bash` matcher, Gemini CLI's `BeforeTool`); only the command it runs names Fuse's event.

`HookEvent` names the three events once, for the commands `RegisterHooks` writes and the events `fuse hook` accepts. `SupportedHarnesses` lists the harnesses in the order `fuse init` registers them and finds one by its name on the command line, for `fuse hook` and its usage line. `SettingsFile` reads and writes the files `fuse init` writes: it replaces a file through a temporary file beside it, and tries that move up to five times, 50 ms apart, while it fails with `IOException` or `UnauthorizedAccessException`, which is how Windows reports a file a virus scanner or the search indexer is holding.

`InitCommand` registers every harness it detects, or Claude Code when it detects neither a harness nor VS Code, plus the VS Code MCP server in `.vscode/mcp.json`, which is not a harness because VS Code runs no hooks. `HookCommand` in `Fuse.Hooks` reads the event and the payload (`HookPayload`), does the work that is the same for every harness (rewriting the command with `CommandRewriter`, running the check), and asks the harness for the answer. An unknown harness or event, `pre-bash` included ([D12](#decisions)), gets the usage line on standard error and exit code 0.

## Engine and client

`Fuse.Engine.Client` holds `EngineClient`, which finds, starts and calls the engine, and `EngineLauncher`, which starts it detached from the caller. `EngineVersion` is in `Fuse.Protocol`, because the build id it defines is part of every request.

The engine process is `EngineServer`, the pipe server, and four classes behind it:

- `RequestRouter`: initialization and routing a request to its feature.
- `Preloader`: the background load of dependents, one load at a time, and the projects that failed to preload, each tried again once the projects reload or a restore writes a `project.assets.json` in its closure.
- `RequestLog`: the log line and the phase line per request, both naming the request by its case (`CheckChanges`, `CheckFiles`, `PlanAffectedTests`, `PlanAllTests`), which the phase line writes as its `kind`.
- `ResponseMapper`: `CheckResult` and `TestPlanResult` to `Protocol` records, and `FuseException` to an unanswered response.

`EngineRequest` and `EngineResponse` are variants, which System.Text.Json source generation writes through `[JsonPolymorphic]` and `[JsonDerivedType]` with the case in a property written first. Every request carries `BuildId` and `RequestId` on the base record. A request line that leaves out an optional field reads it as its default (an empty build id or request id, an empty file list, not waiting for the load), because the source-generated reader would otherwise set it to null, and `RequestRouter` releases the request lock in a `finally` of its own around the request log.

| Record | Cases |
| --- | --- |
| `EngineRequest`, its case in a `request` property | `Ping`, `ShutDown`, `CheckChanges(WaitForLoad)`, `CheckFiles(Files, WaitForLoad)`, `PlanAffectedTests`, `PlanAllTests` |
| `EngineResponse`, its case in a `status` property | `Acknowledged` (to `Ping` and `ShutDown`), `CheckAnswered(Report)`, `PlanAnswered(Plan)`, `Unanswered(Code, Message)`, `Restart` |

The scope is part of the request's case, so the wire has no null that means every change and no flag for every test, and `Fuse.Protocol` still uses no feature type beyond `CompilerError` ([D11](#decisions)). `RequestRouter` turns each case into a `CheckScope` or a `TestScope`. A plan request always waits for the engine to finish loading, because `fuse test` runs nothing until it has the plan; only a check can ask not to wait. The engine's failure word is the operation's: an `Unanswered` response becomes the `Unanswered` outcome.

The engine reads `BuildId` from the request line before the case (`ProtocolJson.ReadBuildId`), so a client of any other build gets `Restart`, whatever shape that build gives its requests. The case properties are named so that an engine of an earlier build can answer too: it reads `kind` into its own enum and drops a request whose value it does not know, so no request has a `kind` property, and it writes `Restart` as `{"status":"Restart"}`, which this build reads as `EngineResponse.Restart`. Because a mismatched engine restarts, changing these records needs no compatibility code.

`OperationResult` carries an `Outcome` (`Clean`, `ProblemsFound`, `Unanswered`) and derives the exit code from it (0, 1, 2). MCP marks `isError` on `Unanswered`, and the hooks treat `ProblemsFound` as a reason to wake the agent.

## Vocabulary

Each word names one thing. "Output" says whether agents and people see the word in CLI, hook and MCP text; a word marked no stays in code and contributor docs. Review rejects a word from the last column.

| Word | Means | Output | Do not write |
| --- | --- | --- | --- |
| HEAD | The commit that is checked out, which Fuse compares against. Gloss it once as "the commit you have checked out" where a reader may not know git. | yes | the last commit, the latest commit |
| Working tree | The files as they are on disk. | yes | working copy, local files |
| Scope | What a request covers: every change, or named files. | no | reach, targets |
| Target | A source file inside the scope. | no | input, edited file (unless it was edited) |
| Surface | The declarations a file exposes to other files. | no | API, signature |
| Declaration change | A declaration in the surface that was added, removed or changed. | yes ("declarations changed") | surface change, API change |
| Signature edit, body edit | The eval scenarios: an edit that makes a declaration change, and one that does not. Stored in `evals/results`. | no | |
| Reach | The files a declaration change can break: none, broad, or precise. | no | scope, impact |
| Candidate | A file bound because of reach, not because it is a target. | no | affected file, dependent file |
| Cause | The declaration change that made a candidate a candidate, printed under an error in that file. | yes | context, provenance, reason |
| Introduced | An error the working tree has and HEAD does not, matched by file, id and message. | yes ("2 errors introduced") | new, regression |
| Compiler error | An error the compiler or an analyzer reports, at error severity under the project's configuration. `CompilerError` on the wire. | yes | diagnostic (for Fuse's record) |
| Current | The solution with every file as it is on disk. | no | working view |
| Baseline | The same projects with every changed file at its HEAD content. | no | HEAD view, head solution |
| Owner | A project whose sources include a file. | no | parent project |
| Dependent | A project that depends on another, as `RepoGraph.DependentsOf` returns it. | yes | consumer, downstream |
| Bind | Compute a file's or a project's diagnostics with Roslyn. | no | (in output, write "check") |
| Affected | A test a change can break, as Fuse selects it. | yes | reached, impacted, selected (in output) |
| Selection | The tests chosen in one test project. | no | filter (the filter is how a selection is passed to `dotnet test`) |
| Plan | Every selection, and how each runs. | no | |
| Shadow run | Running a test assembly from a copy of its build output with the changed assemblies emitted into it. | yes ("without MSBuild") | fast path, fast build |
| Summary | The sentence at the end of an answer that says what was done and why. | no | scope, message |
| Client | The process that sent a request. | no | caller, agent (the agent is outside Fuse) |
| Phase | One named, timed part of a request. | no | stage, step |
| Configuration generation | The counter that invalidates everything derived from project configuration. | no | loader generation |
| Unanswered | Fuse could not produce an answer: an `Unanswered` engine response, and exit code 2. | yes (exit code 2, and `isError` in MCP) | failed (for Fuse itself) |
| Failed | A test or a build failed. | yes | error (for a test); failed (for Fuse or the engine not answering) |
| Harness | An agent host that runs Fuse's hooks. | yes | agent, IDE |
| MCP host | The application that runs an MCP client, in the MCP specification's sense. Always written in full. | yes | host (alone) |
| Application host | The host that framework-called code in an application runs behind. Always written in full. | yes | host (alone) |
| Pre-shell, post-edit, stop | The three hook events. | yes (`fuse hook <harness> <event>`) | pre-bash |
| Fuse, `fuse` | The product, and the command and the `fuse:` output prefix. | yes | fuse (for the product) |

## Naming rules

These decide a name the vocabulary does not list yet. Each exists because breaking it once made two things share a name.

1. **A word in the vocabulary is reserved.** A second concept gets a different word, and the collision is added to the "Do not write" column. Without it, "failed" would mean both a test failure and Fuse not answering.
2. **One concept, one word.** Review rejects a synonym, in code, docs and output alike. Without it, one concept collects several names, as the cause once had four.
3. **Output uses only words marked yes.** Compiler and implementation words (bind, shadow, surface, reach) stay in code and contributor docs. A reader of the output does not know what "bound" or "fast path" mean.
4. **No Fuse type shares a simple name with a BCL, Roslyn or MSBuild type.** An alias to resolve a clash is the signal, as a Fuse `Diagnostic` next to Roslyn's would need.
5. **A domain type and the wire record it maps to never share a name.** Features return `*Result` types (`CheckResult`, `TestPlanResult`); `Protocol` records are named for what the client receives (`CheckReport`, `TestPlan`).
6. **A boolean reads as a yes-or-no question**: `Is`, `Has`, `Uses`, or a verb phrase such as `WaitForLoad` or `FromAnalyzer`.
7. **A request is a verb; a result is a noun; a variant case names the state.** `CheckFiles`, `PlanAffectedTests`; `CheckResult`; `AllChanges`, `Whole`, `Unanswered`.
8. **A namespace is named for what it owns**, which is also its layer on this page, not for one of its callers.
9. **A class is named for its one responsibility.** A nested or private helper type gets a real name too (`MemberWalk`), never a placeholder such as `Walk`. `Native`, the .NET convention for P/Invoke declarations, is the exception.
10. **A number with a unit carries the unit** in its name (`TotalMs`) or is a `TimeSpan`.

## Decisions

A later change that contradicts one records a new decision here rather than editing the old one.

| Id | Decision | Why |
| --- | --- | --- |
| D1 | Foundation is three namespaces: `Fuse.Paths`, `Fuse.Failures`, `Fuse.Telemetry`. | Naming rule 8. A single `Fuse.Core` would name nothing and collect unrelated types. |
| D2 | Each feature keeps its model types in a `Model` sub-namespace. | It makes the reading order visible in the tree. The cost is one `using` per step file. |
| D3 | Fuse stays one project. Layering, including keeping Roslyn and MSBuild out of the client, is enforced by `NamespaceDependencyTests`. | The test catches every edge a project split would, including the Roslyn ban, without extra projects, packing or internal-visibility wiring. Revisit only if the test is worked around. |
| D4 | The output says "introduced", as the code does. | One word per concept in code and output. "fuse: 2 error(s) introduced in 2 file(s)" and "fuse: no errors introduced" read as plainly as "new" does. |
| D5 | The shell event is `pre-shell`. `fuse init` writes it, and `fuse hook` also accepts `pre-bash`. Replaced by D12. | "pre-bash" names one harness's tool. An unknown event gets a usage line on standard error and exit code 0, so dropping `pre-bash` would not break a session. |
| D6 | Fuse's error record is `CompilerError`, with `FromAnalyzer`. | Analyzers run inside the compilation, and the docs call what they report compiler errors. `Diagnostic` would clash with Roslyn's type (naming rule 4). |
| D7 | HEAD is the only name for the comparison point, in code, output, MCP descriptions and docs. | It is git's term, and "the last commit" is wrong on a detached HEAD or an older checkout ([git glossary](https://git-scm.com/docs/gitglossary)). |
| D8 | Every failure has an `ErrorCode`, including `NotARepository`, and each message is written once, in `ErrorMessages` when it does not depend on the request. | The command line, `fuse init` and the MCP server each detect some failures on their own, and one wording keeps them saying the same thing. |
| D9 | Domain results and wire records are named apart (naming rule 5). | `ResponseMapper` needs no aliases, and a reader always knows which side of the pipe a type is on. |
| D10 | An operation's outcome is an enum, `Clean`, `ProblemsFound`, `Unanswered`, and the exit code derives from it. | "Failed" is the word for a test, so Fuse not answering needs another. |
| D11 | `CompilerError` lives in `Fuse.Check.Model`, and `Fuse.Protocol` may use that one type. | The wire carries it unchanged, and features may not use `Fuse.Protocol`, so it cannot live there. A second record with the same fields would only add a copy step. `Fuse.Check.Model` has no Roslyn or MSBuild reference, so a client that uses `Fuse.Protocol` still loads neither. |
| D12 | `fuse hook` accepts only `pre-shell`. This replaces [D5](#decisions)'s accepted `pre-bash` alias. | Fuse carries no compatibility code for earlier builds, and one alias is one more thing to remove later. A user who does not rerun `fuse init` after updating loses the `dotnet` rewrite until they do, and the changelog says so. |

## Known deviations

These are decisions that bend a principle, recorded so they are not copied as precedent.

| Deviation | Where | Why it stands |
| --- | --- | --- |
| A failure that ends a request is an exception, not a result value | `FuseException`, thrown from Sources and Workspace, and in the client by `Fuse.Engine.Client` | These failures (restore needed, load failed, git unreadable, no projects) end the whole request and start deep in loading. A result type would pass through every layer without making a caller safer. The engine catches it in `Fuse.Engine`, and the client in `Fuse.Engine.Client`, and each turns it into an `Unanswered` response. |
| Layers are namespaces in one project, enforced by a test rather than by the compiler | `src/Fuse` | D3. |
| Hooks catch every exception | `HookCommand.RunAsync` | A hook must never break the agent's session ([AGENTS.md](../AGENTS.md)). The failure is logged to `hook.log`. |
| Wire records keep nullable fields that are only printed | `Fuse.Protocol` | Principle 5 applies where code branches on the null; a message or a count that is absent is not a second concept. |
| The `Engine` prefix repeats inside `Fuse.Engine` | `EngineClient`, `EngineLauncher`, `EngineServer`, `EngineLog`, `EngineVersion` | These types are mostly named from outside the namespace, where the prefix carries meaning. New types that never leave the namespace do not take it. |
