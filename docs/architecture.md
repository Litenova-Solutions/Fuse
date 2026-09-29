# Architecture

This page describes how Fuse's code is organized: its layers, which namespace may use which, the order to read it in, the types each feature is built from, and the words those types are named with. It is for contributors and coding agents changing Fuse. Read it before adding a namespace, a type that other namespaces use, a step to the check or test pipeline, or a word to the output. [design.md](design.md) describes what Fuse does; this page describes where that behavior lives and what it is called.

Status: decided on 2026-09-28, against commit `20199cb` on `release/v5.1.0`. The code does not match it yet, and breaking changes to the wire, the output and the hook tokens are accepted to get there. [Where the code is today](#where-the-code-is-today) lists every gap, [Renames](#renames) every name that changes, and [Migration order](#migration-order) the steps that close them. Delete the first two sections once they are empty.

## Principles

1. **Abstractions come first in reading order.** Each feature declares its concepts as small model types (records and their variants) before any class that computes them. A reader learns what a check is from its model, and how it is computed from its steps.
2. **Dependencies point down.** A namespace uses only the layers below it. No cycle exists between namespaces, and a test fails the build when one appears.
3. **One responsibility per type.** A class holds one step, one rule or one piece of state. A class that needs several paragraphs to describe is several classes.
4. **Abstractions are types, not interfaces.** The rule in [AGENTS.md](../AGENTS.md) stands: no interface without two implementations. A model record, a variant hierarchy or a class per step is an abstraction too. The one place Fuse has several implementations of one role is the harness ([Harnesses](#harnesses)).
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

The client and the engine are separate processes. `fuse init`, `fuse hook`, `fuse check`, `fuse test`, `fuse build` and `fuse mcp` run in a short-lived client. `fuse engine <root>` runs the long-lived engine, which alone loads MSBuild and Roslyn. The layers above Wire run in the client and the layers from Engine down run in the engine; Wire, Foundation, `Fuse.Repo` and `Fuse.Dotnet` are shared.

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
2. **No client namespace uses `Microsoft.CodeAnalysis`, `Microsoft.Build`, or a Fuse namespace that does.** Those are `Fuse.Graph`, `Fuse.Workspace`, `Fuse.Changes`, the features and `Fuse.Engine`. A hook pays for every assembly it loads. Today no client file references Roslyn or MSBuild, but only because the runtime loads an assembly when a method that uses it first runs; nothing prevents a client call from reaching one.
3. **`Program` is the only type that references both the client and the engine**, because it is where `fuse engine` and the client commands are told apart.

`tests/Fuse.Tests/Architecture/NamespaceDependencyTests.cs` enforces the table and rule 2. It reads every `.cs` file under `src/Fuse`, takes the file's namespace from its `namespace` line, and collects every `Fuse.X`, `Microsoft.CodeAnalysis` and `Microsoft.Build` name in its `using` directives and in fully qualified references. It fails naming the file, the namespace it used and the rule it broke. A new namespace fails the test until it has a row, which is the point at which its layer is decided. The table on this page and the test change together.

## Reading order

For a contributor new to the code:

1. [design.md](design.md): what Fuse does and why.
2. `Fuse.Protocol`: the questions the engine answers and the shape of each answer. It is small and is the product's contract.
3. `Fuse.Check.Model`, then the steps in `Fuse.Check` in the order [Check](#check) lists them.
4. `Fuse.Testing.Model`, then the steps in `Fuse.Testing`.
5. `Fuse.Changes.Model`, then `Fuse.Changes`, `Fuse.Workspace` and the Sources layer, when a change needs them.
6. `Fuse.Engine`, `Fuse.Engine.Client`, `Fuse.Operations` and the surfaces, for how a request travels.

Model types sit in a `Model` folder and namespace inside their feature (`src/Fuse/Check/Model`, namespace `Fuse.Check.Model`). A model type uses only other model types of its feature, `Fuse.Changes` model types and Foundation, so a reader can understand the folder without opening anything else.

## Foundation

| Type | Namespace | What it is |
| --- | --- | --- |
| `RepoRoot` | `Fuse.Paths` | The canonical repository root, its pipe name and its state directory. Moves from `Fuse.Repo`. |
| `RepoPath` | `Fuse.Paths` | A path inside the repository. It holds the absolute path, gives the repository-relative one with forward slashes, and compares the way the file system does. It replaces the bare strings and the `ChangeTracker.PathComparer` passed to every set and dictionary. |
| `PathRules` | `Fuse.Paths` | Which files are sources, project inputs or build output. Moves from the static members of `ChangeTracker`, which the client and the engine both use. |
| `FuseException`, `ErrorCode` | `Fuse.Failures` | A failure that ends a request, with the message that names the fix. Moves from `Fuse.Workspace` and `Fuse.Protocol`. `ErrorCode` gains `NotARepository`, so every failure a client or the engine reports has a code ([D8](#decisions)). |
| `PhaseTimes`, `Phase`, `PhaseLine` | `Fuse.Telemetry` | The timed phases of one request, the phase names as constants, and the one log line that carries them. The engine writes the line and `evals/Fuse.Evals` reads it, so the names and the format are a contract and live in one place. `PhaseTimes.None` records nothing, so no caller passes or checks a null collector. |
| `EngineLog` | `Fuse.Telemetry` | The engine log writer. Features take it directly rather than through `RepoWorkspace.Log`. |

## Changes

`Fuse.Changes` answers one question for both features: which declarations differ between a file at HEAD and on disk, decided from syntax. The features ask it in two forms, whether a file's visible declarations changed (Check) and whether any declaration's code changed (Testing), so there are two functions. Both read a file the same way, into the same declarations under the same key.

**Model** (`Fuse.Changes.Model`). It uses only Foundation and no Roslyn, so a feature's model may use it:

| Type | Cases or fields | Replaces |
| --- | --- | --- |
| `DeclarationKey` | `Using(directive)`, `GlobalUsing(directive)`, `AssemblyAttribute(attributes)`, `TopLevelStatements`, `NamedType(name)` for a type or delegate, `Member(NamedType container, signature)` | two string keys for one declaration: `SurfaceMap`'s (``M:N.C`0.Add`0( int)``, read back with `StartsWith("U:")`) and `ChangedDeclarations`' (``N.C\|M:Add`0(int)``) |
| `DeclarationChange` | `Added(after)`, `Removed(before)`, `Changed(before, after)`, each with its key and the names other code reaches it by; the texts are the header as written | `SurfaceChange`, whose null `Before` or `After` said which case it was |
| `FileChanges` | the declaration changes of one file, and `HasBroadChange` | the list `SurfaceMap.Changes` returned, from which `ChangeReach` decided change by change whether the reach was broad |

A key is read from syntax alone. A member's signature holds its kind, name, explicit interface, arity and parameter types with their modifiers, so overloads, a static constructor and an explicit implementation each have their own key. A field has one key per variable. When a file declares one key twice (two parts of a partial type or member), the first is kept.

**Steps** (`Fuse.Changes`):

1. `FileDeclarations`: reads one version of a file into its declarations, each a `DeclarationNode` with its key, its syntax node, its surface (what other files can observe, with bodies and trivia removed; none for a finalizer or top-level statements), the names other code reaches it by, and its containing type. It is the one walk of namespaces, types and members that both functions use.
2. `DeclarationHeader`: a declaration's header as written, on one line. `DeclarationChange` carries it and a cause quotes it.
3. `SurfaceDiff`: Check's question. It compares the surfaces of two `FileDeclarations` and returns `FileChanges`, which is broad when a type header, a delegate, a global using or a file-level attribute list changed.
4. `CodeDiff`: Testing's question. It compares the code of the same declarations, a type by its header (its surface) and anything else by its whole syntax, and returns the nodes the test walks start from: each added or changed declaration, the type of each removed member, the compilation unit when top-level statements change, and every type when the file's usings or file-level attributes change.

## Check

The steps follow the numbered list in [design.md](design.md#check). Each is one class.

**Model** (`Fuse.Check.Model`):

| Type | Cases or fields | Replaces |
| --- | --- | --- |
| `CheckScope` | `AllChanges`, `Files(IReadOnlyList<RepoPath>)` | `IReadOnlyCollection<string>? files`, where null means every change |
| `Reach` | `None` (no declaration change), `Broad(IReadOnlySet<RepoPath>)` (every file in the reached projects), `Precise(IReadOnlyDictionary<RepoPath, Cause>)` | `ReachProvenance?` together with a project count, which `Checker` reads in one conditional |
| `Cause` | `Changed(declaration)`, `Removed(declaration as it was at HEAD)` | `(string Declaration, bool Removed)` |
| `IntroducedError` | `CompilerError`, `Cause?` | the pair `CheckReport.Introduced` and `CheckReport.Context`, two arrays that must stay the same length |
| `CheckResult` | introduced errors, files checked, the projects the errors are in, projects with declaration changes, dependents checked, whether whole projects were checked, causes left out | `CheckReport` used as the domain result |

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

| Type | Cases or fields | Replaces |
| --- | --- | --- |
| `TestScope` | `Affected`, `All` | `bool AllTests` |
| `TestSelection` | `Whole(reason)`, `Methods(ImmutableHashSet<string> patterns)` | `ProjectSelection`, a mutable class whose `All`, `AllReason` and `Patterns` can disagree |
| `RunMode` | `Shadow(assembly)`, `Build` | `TestRun.ShadowAssembly`, where null means build, and the null `ShadowEmitter` returned when a shadow run was not safe |
| `PlannedRun` | test project, name, `RunMode`, filter, whether it uses Microsoft.Testing.Platform | `Protocol.TestRun` used as the domain result |
| `TestPlanResult` | one `PlannedRun` per run, selected and total test counts, summary | `Protocol.TestPlan` used as the domain result |

A selection never reaches the wire: `TestFilter` turns it into the filter of a run. The run mode does, as `Protocol.TestRunMode`, a record with the same two cases that System.Text.Json writes with a `kind` property (`"mode":{"kind":"Shadow","assembly":"..."}` or `"mode":{"kind":"Build"}`) through `[JsonPolymorphic]` and `[JsonDerivedType]`. It is a second record because `Fuse.Protocol` may not use a feature's model beyond [D11](#decisions), and naming rule 5 keeps the two names apart.

**Steps** (`Fuse.Testing`), following [design.md](design.md#test-selection):

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
| `Patch(Paths, VanishedDirectories)` | otherwise | the files to apply and the directories that are gone | applies each file, and each file it holds under a vanished directory, to both views, after deriving them again if a background load opened a project |

It replaces `ChangeBatch`, whose three booleans (`HeadMoved`, `ProjectFilesChanged`, `Storm`) could express combinations that never happen. `HeadMoved` was true only together with `Storm`, and `RepoWorkspace.SyncAsync` returned on `Storm` before it reached `if (batch.HeadMoved || _rebuildPending)`, so `batch.HeadMoved` was always false there. With named cases only the real combinations can be written: a moved HEAD always re-evaluates, because the commit it moved to can have other project files. The engine log names the case and the trigger, as in `reloading: Reevaluate trigger=HEAD moved to <commit>` and `reloading: Reload trigger=<count> changed files`.

**Workspace** (`Fuse.Workspace`):

1. `ProjectLoader`: evaluates every project into the `RepoGraph` and opens the ones requests need in an MSBuildWorkspace, which is used only as a loader because its `TryApplyChanges` writes to disk. It refuses a project that is not restored (`RestoreNeeded`, naming the `dotnet restore` to run) or that did not load (`LoadFailed`, with `LoadFailure` telling a real failure from a restore warning), and increments `ConfigurationGeneration` whenever it closes every project. A background load (`PreloadAsync`) leaves a mark that the next request takes (`TakePreloaded`), because neither view holds the project yet.
2. `SolutionViews`: the current and baseline solutions, both derived from the loader's solution with the repository's own analyzers loaded from a copy (`AnalyzerShadow`), and `BaselineGeneration`, which changes only when the baseline's content does. It applies a file's disk content to the current solution and its HEAD content to the baseline, adding or removing documents as needed, and remembers every path it applied so that a rebuild from the loader applies them again.
3. `WorkspaceSync`: acts on each `SyncResult`, and folds the projects the loader opens into both views (`EnsureLoadedAsync`), rebuilding them once for a request's own load and a background load together.
4. `RepoWorkspace`: the entry point the features use. It composes the tracker and the three classes above and adds no rule of its own.

## Harnesses

A harness is an agent host that runs Fuse's hooks: Claude Code, Cursor, Gemini CLI, Codex, GitHub Copilot CLI and OpenCode. `Fuse.Harnesses` holds a `Harness` base class with six implementations, `ClaudeCode`, `Cursor`, `GeminiCli`, `Codex`, `CopilotCli` and `OpenCode`, which meets the two-implementation rule. Adding a harness means adding one class and listing it in `SupportedHarnesses`. Each implementation owns everything about its harness:

- its `Name` on the command line (`claude`, `cursor`, `gemini`, `codex`, `copilot`, `opencode`), which every registration already written into users' settings contains, so it never changes;
- how to detect it in a repository (`IsUsedIn`, from its settings directory or instructions file), and where and how its hooks are registered (`RegisterHooks`), including the permission allowances Claude Code gets for `fuse build` and `fuse test` and the OpenCode plugin, `Harnesses/opencode-plugin.js`, which the tool embeds;
- how it answers a pre-shell, post-edit and stop event: `ReplaceShellCommand`, `ReportAfterEdit`, and `AllowStop` or `BlockStop`, each returning a `HookAnswer` with the standard output, standard error and exit code the harness reads;
- whether it runs the post-edit hook in the background (`RunsPostEditInBackground`), and whether Cursor also runs the hooks in its settings (`IsAlsoRunByCursor`). Both are true for Claude Code only.

The base class holds only that contract and the helpers the six share: the hook command for an event, detection from paths in the repository, and replacing Fuse's handler in the nested hook format Claude Code, Gemini CLI and Codex use. Each harness's hook configuration keeps the harness's own event and tool names (Claude Code's `PreToolUse` with a `Bash` matcher, Gemini CLI's `BeforeTool`); only the command it runs names Fuse's event.

`HookEvent` names the three events once, for the commands `RegisterHooks` writes and the events `fuse hook` accepts. `SupportedHarnesses` lists the harnesses in the order `fuse init` registers them and finds one by its name on the command line, for `fuse hook` and its usage line. `SettingsFile` reads and writes the files `fuse init` writes: it replaces a file through a temporary file beside it, and retries that move up to five times, 50 ms apart, while it fails with `IOException` or `UnauthorizedAccessException`, which is how Windows reports a file a virus scanner or the search indexer is holding.

`InitCommand` registers every harness it detects, or Claude Code when it detects neither a harness nor VS Code, plus the VS Code MCP server in `.vscode/mcp.json`, which is not a harness because VS Code runs no hooks. `HookCommand` in `Fuse.Hooks` reads the event and the payload (`HookPayload`), does the work that is the same for every harness (rewriting the command with `CommandRewriter`, running the check), and asks the harness for the answer. An unknown harness or event, `pre-bash` included ([D12](#decisions)), gets the usage line on standard error and exit code 0.

## Engine and client

`Fuse.Engine.Client` takes `EngineClient` and `EngineLauncher`. `EngineVersion` moves to `Fuse.Protocol`, because the build id it defines is part of every request.

`EngineHost` (205 lines) splits into:

- `RequestRouter`: initialization and routing a request to its feature.
- `Preloader`: the background load of dependents and the set of projects that failed to preload.
- `RequestLog`: the log line and the phase line per request, both naming the request by its case (`CheckChanges`, `CheckFiles`, `PlanAffectedTests`, `PlanAllTests`), which the phase line writes as its `kind`.
- `ResponseMapper`: `CheckResult` and `TestPlanResult` to `Protocol` records, and `FuseException` to an unanswered response.

`EngineRequest` and `EngineResponse` are variants, which System.Text.Json source generation writes through `[JsonPolymorphic]` and `[JsonDerivedType]` with the case in a property written first. Every request carries `BuildId` and `RequestId` on the base record.

| Record | Cases | Replaces |
| --- | --- | --- |
| `EngineRequest`, its case in a `request` property | `Ping`, `ShutDown`, `CheckChanges(WaitForLoad)`, `CheckFiles(Files, WaitForLoad)`, `PlanAffectedTests`, `PlanAllTests` | `RequestKind` with `Files`, where null meant every change, `Wait` and `AllTests` |
| `EngineResponse`, its case in a `status` property | `Acknowledged` (to `Ping` and `ShutDown`), `CheckAnswered(Report)`, `PlanAnswered(Plan)`, `Unanswered(Code, Message)`, `Restart` | `ResponseStatus` (`Ok`, `Error`, `Restart`) with nullable `Error`, `Message`, `Check` and `Tests` |

The scope is part of the request's case, so the wire has no null that means every change and no `AllTests` flag, and `Fuse.Protocol` still uses no feature type beyond `CompilerError` ([D11](#decisions)). `RequestRouter` turns each case into a `CheckScope` or a `TestScope`. A plan request always waits for the engine to finish loading, because `fuse test` runs nothing until it has the plan; only a check can ask not to wait. The engine's failure word is the operation's: an `Unanswered` response becomes the `Unanswered` outcome.

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
| Unanswered | Fuse could not produce an answer: an `Unanswered` engine response, and exit code 2. | yes ("Fuse could not answer") | failed (for Fuse itself) |
| Failed | A test or a build failed. | yes | error (for a test); failed (for Fuse or the engine not answering) |
| Harness | An agent host that runs Fuse's hooks. | yes | agent, IDE |
| MCP host | The application that runs an MCP client, in the MCP specification's sense. Always written in full. | yes | host (alone) |
| Application host | The host that framework-called code in an application runs behind. Always written in full. | yes | host (alone) |
| Pre-shell, post-edit, stop | The three hook events. | yes (`fuse hook <harness> <event>`) | pre-bash |
| Fuse, `fuse` | The product, and the command and the `fuse:` output prefix. | yes | fuse (for the product) |

## Naming rules

These decide a name the vocabulary does not list yet. Each came from a pattern the code had repeated at least twice.

1. **A word in the vocabulary is reserved.** A second concept gets a different word, and the collision is added to the "Do not write" column. Before this rule, "failed" meant both a test failure and Fuse not answering.
2. **One concept, one word.** Review rejects a synonym, in code, docs and output alike. Before this rule, the cause had four names.
3. **Output uses only words marked yes.** Compiler and implementation words (bind, shadow, surface, reach) stay in code and contributor docs. Before this rule, the output said "whole projects bound" and "fast path".
4. **No Fuse type shares a simple name with a BCL, Roslyn or MSBuild type.** An alias to resolve a clash is the signal. Before this rule, `Diagnostic` needed `FuseDiagnostic` and `RoslynDiagnostic`.
5. **A domain type and the wire record it maps to never share a name.** Features return `*Result` types (`CheckResult`, `TestPlanResult`); `Protocol` records are named for what the client receives (`CheckReport`, `TestPlan`).
6. **A boolean reads as a yes-or-no question**: `Is`, `Has`, `Uses`, or a verb phrase such as `WaitForLoad` or `FromAnalyzer`.
7. **A request is a verb; a result is a noun; a variant case names the state.** `CheckFiles`, `PlanAffectedTests`; `CheckResult`; `AllChanges`, `Whole`, `Unanswered`.
8. **A namespace is named for what it owns**, which is also its layer on this page, not for one of its callers.
9. **A class is named for its one responsibility.** A nested or private helper type gets a real name too (`MemberWalk`), never a placeholder such as `Walk`. `Native`, the .NET convention for P/Invoke declarations, is the exception.
10. **A number with a unit carries the unit** in its name (`TotalMs`) or is a `TimeSpan`.

## Decisions

Accepted on 2026-09-28. A later change that contradicts one records a new decision here rather than editing the old one.

| Id | Decision | Why |
| --- | --- | --- |
| D1 | Foundation is three namespaces: `Fuse.Paths`, `Fuse.Failures`, `Fuse.Telemetry`. | Naming rule 8. A single `Fuse.Core` would name nothing and collect unrelated types. |
| D2 | Each feature keeps its model types in a `Model` sub-namespace. | It makes the reading order visible in the tree. The cost is one `using` per step file. |
| D3 | Fuse stays one project. Layering, including keeping Roslyn and MSBuild out of the client, is enforced by `NamespaceDependencyTests`. | The test catches every edge a project split would, including the Roslyn ban, without extra projects, packing or internal-visibility wiring. Revisit only if the test is worked around. |
| D4 | The output says "introduced", as the code does. | One word per concept in code and output. "fuse: 2 errors introduced in 2 files" and "fuse: no errors introduced" read as plainly as "new" does. |
| D5 | The shell event is `pre-shell`. `fuse init` writes it, and `fuse hook` also accepts `pre-bash`. | "pre-bash" names one harness's tool. An unknown event gets a usage line on standard error and exit code 0 ([HookCommand.cs:29](../src/Fuse/Hooks/HookCommand.cs#L29)), so dropping `pre-bash` would not break a session, but it would switch off the `dotnet` rewrite for every user who does not rerun `fuse init`. One accepted alias costs one pattern. |
| D6 | Fuse's error record is `CompilerError`, with `FromAnalyzer`. | Analyzers run inside the compilation, and the README already calls them compiler errors. `Diagnostic` clashed with Roslyn's type (naming rule 4). |
| D7 | HEAD is the only name for the comparison point, in code, output, MCP descriptions and docs. | It is git's term, and "the last commit" is wrong on a detached HEAD or an older checkout ([git glossary](https://git-scm.com/docs/gitglossary)). |
| D8 | Every failure has an `ErrorCode`, including `NotARepository`, and each message is written once. | design.md already promised a "not a repository" code. Today three places (`Program.cs:90`, `InitCommand.cs:23`, `McpCommand.cs:68`) word it three ways. |
| D9 | Domain results and wire records are named apart (naming rule 5). | `ResponseMapper` needs no aliases, and a reader always knows which side of the pipe a type is on. |
| D10 | An operation's outcome is an enum, `Clean`, `ProblemsFound`, `Unanswered`, and the exit code derives from it. | "Failed" meant Fuse not answering while the output next to it said "1 failed" for tests. |
| D11 | `CompilerError` lives in `Fuse.Check.Model`, and `Fuse.Protocol` may use that one type. | The wire carries it unchanged, and features may not use `Fuse.Protocol`, so it cannot live there. A second record with the same fields would only add a copy step. `Fuse.Check.Model` has no Roslyn or MSBuild reference, so a client that uses `Fuse.Protocol` still loads neither. |
| D12 | `fuse hook` accepts only `pre-shell`. This replaces [D5](#decisions)'s accepted `pre-bash` alias. | Fuse carries no compatibility code for earlier builds, and one alias is one more thing to remove later. A user who does not rerun `fuse init` after updating loses the `dotnet` rewrite until they do, and the changelog says so. |

## Known deviations

These are decisions that bend a principle, recorded so they are not copied as precedent.

| Deviation | Where | Why it stands |
| --- | --- | --- |
| A failure that ends a request is an exception, not a result value | `FuseException`, thrown from Sources and Workspace | These failures (restore needed, load failed, git unreadable) end the whole request and start deep in loading. A result type would pass through every layer without making a caller safer. It is thrown only below Features and caught only in `Fuse.Engine` and at the top of `fuse hook`. |
| Layers are namespaces in one project, enforced by a test rather than by the compiler | `src/Fuse` | D3. |
| Hooks catch every exception | `HookCommand.RunAsync` | A hook must never break the agent's session ([AGENTS.md](../AGENTS.md)). The failure is logged to `hook.log`. |
| Wire records keep nullable fields that are only printed | `Fuse.Protocol` | Principle 5 applies where code branches on the null; a message or a count that is absent is not a second concept. |
| The `Engine` prefix repeats inside `Fuse.Engine` | `EngineClient`, `EngineLauncher`, `EngineServer`, `EngineLog`, `EngineVersion` | These types are mostly named from outside the namespace, where the prefix carries meaning. New types that never leave the namespace do not take it. |

## Where the code is today

Measured at commit `20199cb`, including the uncommitted work on `release/v5.1.0` (`ErrorContext`, `ReachProvenance`).

**Namespace cycles.** Each row lists the Fuse namespaces the namespace uses:

```text
Repo      -> Dotnet Protocol Workspace
Graph     -> Dotnet Protocol Repo Workspace
Workspace -> Graph Protocol Repo
Check     -> Engine Graph Protocol Repo Workspace
Testing   -> Engine Graph Protocol Repo Workspace
Engine    -> Check Graph Protocol Repo Testing Workspace
Cli       -> Dotnet Engine Protocol Repo
Hooks     -> Cli Protocol Repo
Mcp       -> Cli Engine Repo
```

There are four cycles (Repo and Workspace, Graph and Workspace, Check and Engine, Testing and Engine), all from three misplaced types: `FuseException` in `Workspace` is thrown by `Repo` and `Graph`; `ErrorCode` in `Protocol` is used by `Repo`; `PhaseTimes` in `Engine` is used 19 times by `Check` and `Testing`.

**Features return wire records.** `Checker.CheckManyAsync` returns `Protocol.CheckReport` and `TestPlanner.PlanAsync` returns `Protocol.TestPlan`.

**Large classes.** `Checker.CheckManyAsync` is about 150 lines covering every step in [Check](#check). `TestSelector` is 437 lines, of which the nested `Walk` class is about 285. `RepoWorkspace`, `ChangeTracker`, `EngineHost` and the per-harness code are described in their sections above.

**Paths.** Paths are strings, relative or absolute by convention, and `ChangeTracker.PathComparer` appears 36 times in `src/Fuse`, once per set, dictionary or comparison that holds paths.

**Names.** Every entry in [Renames](#renames) is a name the code has today.

## Renames

Every name that changes, with the migration step that changes it. Reach says who notices: internal (identifiers only), wire (the pipe protocol, free because the engine restarts on a build mismatch), output (text agents and people read; README examples change with it), configuration (written into users' harness settings).

| Today | Becomes | Reach | Step |
| --- | --- | --- | --- |
| `Fuse.Repo.RepoRoot` | `Fuse.Paths.RepoRoot` | internal | 1 |
| `ChangeTracker.IsSource`, `IsProjectFile`, `IsBuildOutput`, `PathComparer` | `Fuse.Paths.PathRules` | internal | 1 |
| `Fuse.Workspace.FuseException` | `Fuse.Failures.FuseException` | internal | 1 |
| `Fuse.Protocol.ErrorCode` | `Fuse.Failures.ErrorCode`, plus `NotARepository` | wire | 1 |
| `Fuse.Engine.PhaseTimes`, phase string literals | `Fuse.Telemetry.PhaseTimes`, `Phase` constants | internal | 1 |
| `Fuse.Engine.EngineLog` | `Fuse.Telemetry.EngineLog` | internal | 1 |
| `Fuse.Cli` | `Fuse.Operations` | internal | 2 |
| `OperationResult.ExitCode`, `Found`, `Failed` | `OperationResult.Outcome` (`Clean`, `ProblemsFound`, `Unanswered`) | internal | 2 |
| `EngineClient`, `EngineLauncher` in `Fuse.Engine` | the same names in `Fuse.Engine.Client` | internal | 2 |
| `Fuse.Engine.EngineVersion` | `Fuse.Protocol.EngineVersion` | internal | 2 |
| `EngineRequest.Version` | `BuildId` | wire | 2 |
| `EngineHost` | `RequestRouter`, `Preloader`, `RequestLog`, `ResponseMapper` | internal | 2 |
| three "not inside a git repository" messages | one message for `ErrorCode.NotARepository` | output | 2 |
| `Protocol.Diagnostic`, `Analyzer` | `CompilerError`, `FromAnalyzer` | wire | 3 |
| `CheckReport.Introduced` and `Context` | `CheckReport.Errors`, each a `ReportedError` with its cause | wire | 3 |
| `CheckReport.ContextLeftOut` | `CausesLeftOut` | wire | 3 |
| `CheckReport.SurfaceChangedIn` | `DeclarationsChangedIn` | wire | 3 |
| `CheckReport.WholeProjects` | `CheckedWholeProjects` | wire | 3 |
| `ChangeReach.HasSurfaceChangeAsync` | `HasDeclarationChangeAsync` | internal | 3 |
| `ErrorContext` | `CauseLines` | internal | 3 |
| `ReachProvenance` | `Reach.Precise` | internal | 3 |
| "fuse: N new error(s) in M file(s)" | "fuse: N error(s) introduced in M file(s)" | output | 3 |
| "fuse: no new errors" | "fuse: no errors introduced" | output | 3 |
| "whole projects bound" | "checked whole projects" | output | 3 |
| "N more error(s) with no cause line" | "N cause(s) left out" | output | 3 |
| `TestPlan.Scope`; `scope` locals in the operations | `Summary`; `summary` | wire | 4 |
| `TestRun.ShadowAssembly` | `TestRun.Mode`, a `TestRunMode` (`Shadow(assembly)`, `Build`) mapped from the model's `RunMode` | wire | 4 |
| `TestRun.TestingPlatform` | `UsesTestingPlatform` | wire | 4 |
| `ProjectSelection` | `TestSelection` (`Whole`, `Methods`) | internal | 4 |
| `TestSelector.Walk` | `MemberWalk` | internal | 4 |
| `TestSelector.SelectByTypeGraphAsync` | `TypeWalk.SelectAsync` | internal | 4 |
| `Walk.IsFrameworkInvoked`, `Walk.IsEntryPoint` | `HostRule.IsFrameworkInvoked`, `IsEntryPoint`, `IsCalledByHost`, `DependentTestProjects` | internal | 4 |
| `TestPlanner.Filter` | `TestFilter.For(TestSelection)` | internal | 4 |
| `ShadowEmitter.TryPrepareAsync`, null when not safe | `ShadowEmitter.PrepareAsync`, returning a `RunMode` | internal | 4 |
| `TestPlanner.PlanAsync(bool all)`; `RequestRouter.ScopeOf` | `PlanAsync(TestScope)`; `RequestRouter.CheckScopeOf` and `TestScopeOf` | internal | 4 |
| "fast path", "fast path for N of M project(s)" | "without MSBuild", "without MSBuild for N of M project(s)" | output | 4 |
| "ran the tests you selected" | "ran the tests your dotnet test arguments name" | output | 4 |
| "no test reaches the changed code" | "no test is affected by the changes" | output | 4 |
| `SurfaceMap` and `ChangedDeclarations` string keys | `DeclarationKey` | internal | 5 |
| `SurfaceMap.Compute` and `ChangedDeclarations.Index`; `SurfaceEntry` | `FileDeclarations.Of`; `DeclarationNode` | internal | 5 |
| `SurfaceMap.Changes`, returning `SurfaceChange` | `SurfaceDiff.Compare`, returning `FileChanges` with each `DeclarationChange` (`Added`, `Removed`, `Changed`) | internal | 5 |
| `SurfaceMap.Declaration` | `DeclarationHeader.Of` | internal | 5 |
| `Fuse.Testing.ChangedDeclarations.Find` | `Fuse.Changes.CodeDiff.Find` | internal | 5 |
| `RequestKind` (`Ping`, `Check`, `TestPlan`, `Shutdown`) in a `kind` property | request variants `Ping`, `ShutDown`, `CheckChanges`, `CheckFiles`, `PlanAffectedTests`, `PlanAllTests` in a `request` property | wire | 6 |
| `EngineRequest.Files`, `Wait`, `AllTests` | `CheckFiles(Files, WaitForLoad)`, `CheckChanges(WaitForLoad)`, `PlanAllTests` | wire | 6 |
| `ResponseStatus` (`Ok`, `Error`, `Restart`), `EngineResponse.Ok`, `Fail`, `Error`, `Check`, `Tests` | response variants `Acknowledged`, `CheckAnswered(Report)`, `PlanAnswered(Plan)`, `Unanswered(Code, Message)`, `Restart` in a `status` property, so the engine and the operation use one word | wire | 6 |
| `kind=Check` and `kind=TestPlan` in the phase line; `Check all`, `Check <files>` and `TestPlan all` in the engine log | the case: `CheckChanges`, `CheckFiles <files>`, `PlanAffectedTests`, `PlanAllTests` | internal | 6 |
| `RequestRouter.CheckScopeOf` and `TestScopeOf` | one case per scope in `RequestRouter.HandleAsync` | internal | 6 |
| `CheckOperation.RunAsync(wait)` | `waitForLoad` | internal | 6 |
| `ChangeBatch` (`HeadMoved`, `SourcePaths`, `ProjectFilesChanged`, `Storm`, `VanishedDirectories`, `Trigger`) | `SyncResult` (`Reevaluate(Paths, Trigger)`, `Reload(Paths, Trigger)`, `Patch(Paths, VanishedDirectories)`) | internal | 7 |
| `reloading: project files changed=... storm=... headMoved=... trigger=...` in the engine log | `reloading: <case> trigger=<why>`, the `SyncResult` case name and the new commit, the watcher's error, the last project file or the number of changed files | internal | 7 |
| `LoaderGeneration`, `configurationGeneration` | `ConfigurationGeneration` | internal | 7 |
| `RepoWorkspace` | `ProjectLoader`, `SolutionViews`, `WorkspaceSync`, and `RepoWorkspace` as the entry point | internal | 7 |
| `RepoWorkspace.RebuildAsync(headMoved)`, always called with false | `SolutionViews.RebuildAsync` | internal | 7 |
| `ChangeTracker` | `HeadResolver`, `GitStatus`, `WatchedPaths` with `WatchedChanges`, `HeadComparison`, and `ChangeTracker` composing them | internal | 7 |
| `ChangeTracker.ResolveHeadAsync`, `GitFailure` | `HeadResolver.ResolveAsync`, `HeadResolver.GitFailure` | internal | 7 |
| `ChangeTracker.StormThreshold` | `ChangeTracker.MaxPatchedPaths` | internal | 7 |
| `ChangeTracker.IsIgnoredDirectory` | `WatchedPaths.IsIgnored` | internal | 7 |
| `Fuse.Hooks.InitCommand` | `Fuse.Harnesses.InitCommand` | internal | 8 |
| harness name strings, and `HookCommand.Harnesses` | `Harness` and its six implementations (`ClaudeCode`, `Cursor`, `GeminiCli`, `Codex`, `CopilotCli`, `OpenCode`), listed and found by name in `SupportedHarnesses` | internal | 8 |
| the per-harness flags and `InitCommand.WriteClaude`, `WriteCursor`, `WriteGemini`, `WriteCodex`, `WriteCopilot`, `WriteOpenCode` | `IsUsedIn` and `RegisterHooks` on each harness | internal | 8 |
| `InitCommand.WriteVsCode` | `InitCommand.RegisterMcpServer` | internal | 8 |
| `InitCommand.Load`, `Save`, `SaveText`, `Object` | `SettingsFile.Read`, `Write`, `WriteText`, `GetOrAddObject` | internal | 8 |
| `InitCommand.SetHook`, `SetFlatHook`, `IsFuse` | `Harness.SetNestedHook` and `IsFuse`; `SetFlatHook` in `Cursor` | internal | 8 |
| `HookCommand.PreBash`; the harness branches in it, `Report`, `Clean` and `StopAsync`; `harness == "claude"` in `PostEditAsync` and before the event | `HookCommand.PreShell`; `ReplaceShellCommand`, `ReportAfterEdit`, `AllowStop` and `BlockStop` on each harness, returning a `HookAnswer`; `RunsPostEditInBackground` and `IsAlsoRunByCursor` | internal | 8 |
| `Hooks/opencode-plugin.js`, resource `Fuse.Hooks.opencode-plugin.js` | `Harnesses/opencode-plugin.js`, resource `Fuse.Harnesses.opencode-plugin.js` | internal | 8 |
| `fuse hook <harness> pre-bash`, in `fuse init`'s commands, the OpenCode plugin and the usage line | `fuse hook <harness> pre-shell`, and `pre-bash` is no longer accepted ([D12](#decisions)) | configuration | 8 |
| strings for paths | `RepoPath` | internal | 9 |
| "the last commit" in the README, MCP descriptions and `site/how-it-works.html` | HEAD | output | 10 |
| "fuse" for the product in messages | "Fuse" | output | 10 |

## Migration order

Each step builds, passes the tests and can merge on its own. Each step renames what the table above lists for it, updates the README examples its output changes, and adds the words it introduces to [Vocabulary](#vocabulary).

1. Create `Fuse.Paths`, `Fuse.Failures` and `Fuse.Telemetry`, and move the Foundation types into them. This removes all four cycles. Add `NamespaceDependencyTests` in the same change.
2. Split `Fuse.Engine.Client` from `Fuse.Engine`, rename `Fuse.Cli` to `Fuse.Operations`, give `OperationResult` its `Outcome`, and split `EngineHost`.
3. Check: add the model types, split `Checker` into its steps, and move the mapping to `CheckReport` into `ResponseMapper`.
4. Testing: the same, with `TestSelection`, `RunMode` and `TestPlanResult`.
5. `Fuse.Changes`: move `SurfaceMap` and `ChangedDeclarations` onto the shared model.
6. `EngineRequest` and `EngineResponse` as variants.
7. `RepoWorkspace`, `ChangeTracker` and `SyncResult`.
8. `Fuse.Harnesses` with the six implementations, and the `pre-shell` event.
9. `RepoPath`, which touches every layer and is easiest once the others have settled.
10. The wording pass: HEAD and product-name casing in the README, the MCP descriptions and the site. Then update the layout section of [AGENTS.md](../AGENTS.md) to this page's layers, and link this page from there.

Start after the `release/v5.1.0` work is committed. Steps 3 and 7 rewrite `Checker`, `ErrorContext` and `RepoWorkspace`, which that branch changes.
