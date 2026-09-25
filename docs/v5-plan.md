# Fuse v5: rewrite plan

Fuse v5 is a single executable that keeps a warm Roslyn compilation of a .NET repository and plugs into coding agents through hooks. After every edit it tells the agent which compiler errors that edit introduced, in this project and in every project that depends on it, in well under a second. When the agent runs `dotnet build` or `dotnet test`, Fuse transparently replaces the command with a faster version that runs only the affected tests and prints only what failed. The agent does not need to know Fuse exists, choose to call it, or read instructions about it.

Hosts that cannot run hooks or shell commands (IDE chat panels, Visual Studio, JetBrains assistants, remote agents) get the same three operations through a small MCP server. That is the whole product. There is no index, no configuration file, no environment variables, no skill file, no navigation or search, and no optional mode. Nothing is ported from v4; its code is reference material only. There is no migration path and no compatibility with v4 commands, tools, files or configs.

This plan comes from a read-only investigation on 2026-09-24: four audits of the repository at `ba75d7c` and a web survey of the agent-harness landscape. Nothing was built, run or benchmarked. Line references point at v4.4.0 source.

## Implementation notes

The implementation on the `v5` branch follows this plan with these differences, each decided during the build:

- **Reach of a declaration change.** Section 4.5 step 3 is implemented with Roslyn's `SymbolFinder` against the HEAD baseline (references, implementations, overrides, derived types), not with a name search. A name search made a change to a common name such as `Format` bind whole projects on NodaTime (4.7 s per edit); symbol references cut that to about 1.3 s with no missed error in the correctness eval.
- **State location.** Logs, shadow test output and test results live in the user's local application data (`fuse/repos/<id>`), not in `obj/fuse`, so Fuse writes nothing inside the repository except the hook settings `fuse init` creates.
- **No binlog in `fuse build`.** It costs build time on every run and nothing reads it.
- **Engine binaries.** The engine runs from a per-build copy of the tool in local application data, because an engine running from the tool directory locks it and breaks `dotnet tool update`.
- **Background preload.** After a check, the engine loads the dependents of projects with uncommitted changes in the background, outside the request lock, so the first declaration change does not pay for loading them.
- **Stop gate.** The stop hook blocks once per stop attempt (it honors `stop_hook_active` and Cursor's `loop_count`), so an agent that cannot fix an error is not trapped in a loop.
- **MCP.** Kept as three tools for hosts without hooks, as section 2 D1 records.
- **Test selection.** Section 4.6's reference walk alone took 125 to 600 s on NodaTime: one `SymbolFinder` query per reached symbol over thousands of files. Selection now starts from a syntax-only type graph (milliseconds) and runs the member-level walk, with an 8 s budget, only when fewer than 40 test classes are reachable. The whole-project fallback applies to application projects and entry points; library members called through an external interface are reached through their type.
- **Multi-targeted projects.** MSBuild's outer evaluation of a multi-targeted project has no `Compile` items, so each target framework's inner evaluation is read and unioned.
- **Test runs.** Shadow runs execute in parallel, one process per target framework; runs through MSBuild stay sequential because they share build output.

Measured results are in the README and `evals/results`.

## 1. What the evidence says to build

### 1.1 What v4 proved and disproved

| Finding | Evidence |
|---|---|
| A tool the agent must choose to call gets ignored | Agents called `fuse_check` 0.7 times per task; build+test calls were 3.1 with Fuse and 3.2 without (`tests/benchmarks/results/loop.json`) |
| Context, retrieval and reduction features did not change outcomes | 211,502 vs 209,182 session tokens; 30 vs 26 percent file recall at N=12 (`agent.json`); 37.7 percent localize recall (`localize.json`) |
| A richer persisted graph does not help retrieval | 15.0 vs 15.0 percent recall (`localize.tier1.json`) |
| Relaying Roslyn diagnostics is exact | 0 false green, 0 false red over 1,000 mutations (`checkgate.json`) |
| A warm in-memory check is fast | 31 to 42 ms per check (`resident-latency.json`, 2 projects loaded) |

The default v4 `fuse_check` never delivered the fast path. It ran `dotnet build --no-incremental` in the working tree, fell back to copying the repository and cold-building it, and labelled the result "no build" (`BuildCaptureRehydrator.cs:136-151`, `BuildGradeChecker.cs:148-207`). It also checked one file in isolation, so it missed breaks in dependent projects.

The lesson has two parts. The core mechanism (a warm compilation relaying exact diagnostics) works. What failed was the delivery (an opt-in tool buried among nine) and the implementation (a cold build behind the "speculative" label).

### 1.2 What the harness landscape gives and lacks

These findings come from the web survey (2026-09-24); the survey records a source for each.

- Every major harness runs hooks. Claude Code `PostToolUse` injects `additionalContext` into the agent's next turn. With `asyncRewake`, a hook runs in the background and wakes the agent only if it exits with code 2. `PreToolUse` can rewrite a Bash command before it runs; rtk (81.7k stars) is built on exactly that, and it does not cover `dotnet`. Cursor has `afterFileEdit`, and Gemini CLI has hooks. Codex hooks reportedly intercept Bash only (unconfirmed).
- No harness gets edit-time C# diagnostics from a language server today. Claude Code accepts only push diagnostics, and Microsoft's `roslyn-language-server` uses pull (Claude Code issues #96044 and #38683; plugins #1359). Codex has no LSP support (openai/codex#8745).
- No tool tells an agent that its edit broke a different project.
- `dotnet build` output floods agent context even at `-v q` (msbuild#13525).
- Microsoft covers navigation (`roslyn-language-server`, the `dotnet/skills` plugins) and build forensics (the Binlog MCP server). Fuse should not duplicate either.
- Always-loaded tool definitions cost tokens and attention. Harnesses now defer them, and a hook costs zero definition tokens.

### 1.3 The product that follows

Fuse v5 does three things, delivered through hooks where the harness has them and through three MCP tools where it does not:

1. **Post-edit check.** After each file edit, it reports the errors the working tree now has that `HEAD` did not, in the edited project and in its dependents.
2. **Fast, focused tests.** It replaces `dotnet test` with a run of only the tests the changes can affect. It uses the warm compilation to emit changed assemblies instead of invoking MSBuild, and prints failures only.
3. **Compact build.** It replaces `dotnet build` with the real build and prints errors only.

A Stop hook ties them together: the agent cannot finish while its changes leave errors that `HEAD` did not have.

## 2. Decisions

Each decision states the choice, the reason, and what was rejected.

**D1. Hooks first, MCP for hosts without hooks.**
- *Reason:* v4 showed agents do not call an optional tool. Hooks run without the agent deciding, cost no definition tokens, and work in every major terminal harness. IDE hosts and remote agents often have no hooks and no shell, so an MCP server with three tools (`fuse_check`, `fuse_test`, `fuse_build`) gives them the same operations. The MCP tools are thin clients of the same engine, with short descriptions and structured results.
- *Rejected:* a skill file, which is unnecessary because the agent never needs to know the commands; an LSP server, because only one server per file extension can own it and Microsoft already ships one; MCP resources, prompts and server instructions, which cost context and add nothing the three tools do not.

**D2. One executable, one project, no plugins or abstractions.**
- *Reason:* `fuse` is both the short-lived hook client and, when started as `fuse engine`, the long-lived warm engine. One binary means one version, so the client and engine cannot disagree.
- *Rejected:* a separate AOT client. Adopt it only if the startup eval (section 7.3) fails.

**D3. No configuration.**
- *Reason:* every knob in v4 (29 environment variables, `fuse.json`, 3 caches with their own limits) was a place for defaults to be wrong. Fuse discovers everything from the repository.
- *Rejected:* a config file, environment variables, CLI flags other than `--all` on `fuse test`.

**D4. The repository is the unit, not a solution file.**
- *Reason:* choosing between solution files caused three v4 bugs. Fuse evaluates every `.csproj` in the repository into a static project graph and loads projects lazily, only when a change touches them.
- *Rejected:* solution discovery and whole-solution loading.

**D5. The baseline is `HEAD`.**
- *Reason:* "errors your changes introduced" must survive engine restarts and must not depend on when the engine started. Comparing against the committed version of each changed file is deterministic.
- *Rejected:* a baseline captured at engine start (v4), which counts the agent's own earlier errors as pre-existing if the engine started late.

**D6. Errors only, matching what fails the build.**
- *Reason:* the agent needs to know what will break the build. Fuse reports compiler errors, warnings the project promotes to errors, and analyzer diagnostics whose effective severity is Error. Nothing else.
- *Rejected:* reporting warnings (noise), running all analyzers (about 870 ms on NodaTime and mostly noise), and skipping analyzers entirely (false green when `EnforceCodeStyleInBuild` or error-severity rules are on).

**D7. Disk is the truth.**
- *Reason:* agents write files through their own edit tools, and a post-edit hook runs after the write. Fuse checks what is on disk.
- *Rejected:* speculative checks of unwritten content (v4's contract). No hook delivers unwritten content, and the agent can simply write and check.

**D8. No persistence.**
- *Reason:* lazy loading makes a cold start cost only the projects the session touches, so a persisted index buys nothing. Every v4 SQLite bug class disappears with it.
- *Rejected:* a SQLite index, a compiler-log cache, capture bundles.

**D9. Test runs bypass MSBuild when it is safe.**
- *Reason:* on a large repository, `dotnet test` spends most of its time in MSBuild. The engine already holds current compilations, so it can emit the changed assemblies into a shadow copy of the last real build output and run the test host directly. When that is not safe (no prior build output, project files changed, or build steps Fuse cannot reproduce), it runs the real `dotnet test` without asking.
- *Rejected:* always using MSBuild, which leaves the loop slow; and in-process test execution, which isolates poorly.

**D10. C# only.**
- *Reason:* VB and F# are outside the product, and Razor is supported because its compiler is a source generator over C#.
- *Rejected:* language seams and provider registries.

## 3. User experience

### 3.1 Install

```bash
dotnet tool install -g fuse
cd my-repo
fuse init
```

`fuse init` detects which harnesses the repository already uses (`.claude/`, `.cursor/`, `.gemini/`, `.codex/`, `.vscode/`) and writes Fuse's hooks into each one's settings. For hosts without hooks (VS Code agent mode through `.vscode/mcp.json`) it registers the MCP server instead. It prints every file it changed and writes nothing else. The same hook command serves every harness; `fuse hook` detects the payload format from the JSON it receives.

### 3.2 Commands

| Command | What it does |
|---|---|
| `fuse init` | Writes hooks for the harnesses found |
| `fuse check` | Errors in the working tree that `HEAD` did not have, across affected projects |
| `fuse test [dotnet test args]` | With no arguments, runs the tests affected by the working-tree changes. With arguments, runs the scope the arguments name. Either way it prints failures only. |
| `fuse test --all` | Runs every test, printing failures only |
| `fuse build [dotnet build args]` | The real `dotnet build`, printing errors only |
| `fuse hook` | The entry point every installed hook calls |
| `fuse mcp` | Stdio MCP server with `fuse_check`, `fuse_test` and `fuse_build` |
| `fuse engine` | Internal: the warm engine process, started automatically |

There is no `status`, `stop`, `doctor`, `index` or `setup`. Whenever Fuse cannot answer (still loading, restore needed, a project failed to load), `fuse check` says so in one line that names the command that fixes it.

### 3.3 Hooks

| Hook | Harness event | Behavior |
|---|---|---|
| Post-edit | Claude Code `PostToolUse` on `Edit\|Write\|MultiEdit`, run with `asyncRewake`; Cursor `afterFileEdit`; Gemini equivalent | Runs a check scoped to the edited file and its dependents. Clean: no output, exit 0. New errors: prints them and wakes the agent (exit 2 in Claude Code). Engine still loading: exits 0 silently, and the next edit or the Stop hook catches up. |
| Command rewrite | Claude Code `PreToolUse` on `Bash`; Codex Bash hook if confirmed | Rewrites `dotnet build ...` to `fuse build ...` and `dotnet test ...` to `fuse test ...`. Nothing else is touched. |
| Stop | Claude Code `Stop`; equivalents where they exist | Runs `fuse check` over all changes. Introduced errors block the stop and are shown to the agent. Pre-existing errors never block. |

### 3.4 Output

All output uses the MSBuild canonical diagnostic format, which models already parse. It is capped at 20 diagnostics, with one closing line:

```text
src/Orders/OrderService.cs(41,17): error CS1061: 'Order' does not contain a definition for 'Total'
src/Api/OrdersController.cs(88,9): error CS7036: There is no argument given that corresponds to the required parameter 'currency' of 'OrderService.Place(Order, string)'
fuse: 2 new errors in 2 files (Orders, Api); Orders public API changed, 3 dependent projects checked
```

Test output:

```text
FAILED Orders.Tests.OrderServiceTests.Place_RejectsEmptyCart
  Assert.Throws() Failure: No exception was thrown
  at Orders.Tests.OrderServiceTests.Place_RejectsEmptyCart() in tests/Orders.Tests/OrderServiceTests.cs:line 52
fuse: 1 failed, 37 passed; ran 38 tests affected by your changes out of 2,914 (fuse test --all runs everything)
```

The scope line is mandatory. The agent must never believe a selective run was a full run.

## 4. Engine design

### 4.1 Project graph

At engine start, Fuse finds every `.csproj` under the repository root, skipping `bin`, `obj`, and directories ignored by git. It evaluates them with MSBuild's static graph (`Microsoft.Build.Graph.ProjectGraph`). Evaluation is not a design-time build: it resolves imports, conditions, `ProjectReference` items and `Compile` globs without running targets. From the graph Fuse keeps three things:

- file to owning projects (a linked or multi-targeted file maps to several),
- project to referenced projects and to transitive dependents,
- test projects (`IsTestProject`, or a reference to xunit, NUnit, MSTest or Microsoft.Testing.Platform).

A change to any `.csproj`, `.props`, `.targets`, `Directory.Packages.props`, `global.json` or `.editorconfig` re-evaluates the graph and invalidates the affected loaded projects.

### 4.2 Lazy compilation loading

Nothing is compiled at start. The first check that touches project P loads P and its reference closure through `MSBuildWorkspace.OpenProjectAsync`. Loading P also loads the projects it references, which is the minimum needed to bind P. Dependents are loaded only when a change alters P's surface (section 4.5). A session that edits one leaf project never pays for the rest of the repository.

The engine holds one immutable `Solution` and swaps it atomically. It never calls `TryApplyChanges`, because on `MSBuildWorkspace` that writes to disk. If a project needs a restore (no `project.assets.json`), the check reports `restore needed: run dotnet restore <path>`; Fuse never runs a restore itself.

### 4.3 Staying current

- A `FileSystemWatcher` on the repository root marks paths dirty. On a buffer overflow, the engine marks every loaded document dirty.
- Every check first stats the dirty paths and the path named by the hook payload, so a write that landed before its watcher event is still seen.
- `.cs` changes use `WithDocumentText`; created and deleted files use `AddDocument` and `RemoveDocument`, with the owning project taken from the graph's globs.
- `.razor`, `.cshtml`, and other additional files use `WithAdditionalDocumentText`, so source generators, including Razor's, see edits.
- More than 300 changed paths at once (a branch switch) drops every loaded project; they reload lazily on the next check.

### 4.4 Baseline against HEAD

For each changed document, the engine reads the `HEAD` version (`git cat-file --batch`, one long-lived process fed through stdin) and forks the solution with those texts to get the baseline diagnostics. A diagnostic counts as introduced if it exists now and has no match in the baseline. A match means the same id, file, and message with identifiers normalized, allowing the line to shift by the edit's line delta. The engine caches baseline results per blob id, so each committed version is bound once. New files have an empty baseline, and deleted files contribute only their dependents' errors.

### 4.5 Scoping a check

For the changed documents D in the projects that own them:

1. **Bind the changed documents.** `GetSemanticModelAsync(doc).GetDiagnostics()` binds one tree each.
2. **Compute a surface fingerprint** for each changed document: a hash over every declaration's signature, accessibility, attributes, base types and constraints, excluding member bodies and trivia. Compare it with the baseline fingerprint. If nothing changed, which is the case for body-only edits (the common case), the check is complete.
3. **If the surface changed,** compute the changed symbol set (added, removed, and changed declarations). Candidate documents are those in the owning project and its transitive dependents (loaded on demand) that either reference a removed or changed symbol, found with `SymbolFinder.FindReferencesAsync` against the baseline fork, or mention the simple name of an added symbol, which catches new overload ambiguities and name hiding. Re-bind only those documents.
4. **Cap the scope.** If more than 500 documents are candidates, bind the affected projects whole with `Compilation.GetDiagnostics()` and say so in the closing line.
5. **Run error-level analyzers only,** on the scoped documents. Per project, the engine computes the analyzers that can report a diagnostic with effective severity Error under that project's editorconfig and globalconfig, and runs only those with `CompilationWithAnalyzers`. Usually that set is empty.
6. **Report the delta** against the baseline (section 4.4).

Surface detection runs at declaration level, not per project, because `GetDependentSemanticVersionAsync` changes for any top-level edit in the project, and the fingerprint lets a change to one type's surface re-check only that type's users.

Known limits: design-time evaluation can differ from a real build in custom targets; generators that read inputs outside the project's additional files are invisible; IL weaving and post-compile steps are invisible. `fuse build` is the ground truth for these cases, and the correctness eval measures how often they occur.

### 4.6 Affected-test selection

1. Map the text changes in each changed document to the enclosing member declarations. The changed-member set also includes the members whose declarations were added, removed, or changed in the surface diff.
2. Walk references backwards from each changed member: callers, implementations of changed interface and abstract members (`FindImplementationsAsync`), overrides, and references to changed types (constructors, static members, attributes). The walk stays inside the test projects that transitively depend on the changed projects, and it stops at methods carrying a test attribute.
3. A test class whose type is referenced (for example through a fixture or a DI registration in test setup) counts all of its tests.
4. Anything the walk cannot resolve statically (reflection, source-generated registration, assembly scanning) makes selection fall back to every test in the affected test projects. The scope line says so.

The walk is bounded by the size of the test projects, not by a depth limit, because a missed failing test is the one outcome the selection must not produce. The eval in section 7.2 gates on zero missed failures.

### 4.7 The fast test path

When `fuse test` runs:

1. Resolve the scope: the affected set, the arguments' scope, or everything.
2. **Fast path**, used when the test projects and their references all have prior build output from a real build, and no project file changed since:
   - Copy each test project's output directory into a shadow directory under `obj/fuse/` (hardlinks where the filesystem allows).
   - `Compilation.Emit` every changed project, and every dependent whose surface fingerprint changed, into the shadow directory, with the same assembly name, references and PDB.
   - Run the test host on the shadow output: the executable for a Microsoft.Testing.Platform project, or `dotnet test <dll>` for a VSTest project, with the filter for the selected tests.
3. **Slow path**, used otherwise: run `dotnet test` with `--no-restore` and the filter. MSBuild builds what it needs.
4. Parse TRX or MTP results into the output in section 3.4.

The fast path is invisible except for one word in the closing line (`fast` or `build`), which lets the evals attribute time. Resources, embedded content and generated files come from the last real build, so an edit to one of those forces the slow path. The graph knows which items are `Compile` and which are not.

### 4.8 Compact build

`fuse build` runs `dotnet build` with the caller's arguments plus `-nologo -tl:off -v q -clp:ErrorsOnly;NoSummary -bl:obj/fuse/last.binlog`. It parses canonical diagnostics, dedupes those repeated per target framework, caps the output, and exits with the SDK's exit code. On failure, it prints the binlog path for anyone who needs Microsoft's Binlog MCP server. A successful build refreshes the output directories the fast test path copies from.

### 4.9 Process model

- **One engine per repository root.** The root is the nearest `.git` ancestor, canonicalized with `GetFinalPathNameByHandle` on Windows and `realpath` elsewhere, which fixes the v4 two-daemons-per-root and non-ASCII path bugs. An OS mutex per root guarantees a single engine.
- **Start.** A client that cannot connect starts `fuse engine` detached, with stdio redirected to a log file under `obj/fuse/`, and connects as soon as the pipe exists (bounded at 5 s). Graph evaluation and loading happen after the pipe is up, and requests during loading get a `loading` answer immediately.
- **Transport.** One named pipe (Windows) or Unix socket per root. Each connection carries one request and one response as newline-delimited JSON, using source-generated serialization.
- **Version.** Every request carries the executable's version. On a mismatch the engine answers `restart` and exits, and the client starts a fresh one. An old engine cannot block a new client.
- **Lifetime.** The engine exits after 30 minutes without a request, measured from the last request (not open connections, which was the v4 bug), or when the repository root disappears.
- **Cancellation.** Each request has a deadline, and a disconnect cancels the work.
- **No in-process mode.** CI uses `dotnet build` and `dotnet test` directly; Fuse is an agent tool.

## 5. Codebase

### 5.1 Layout

```text
Fuse.slnx
src/Fuse/                  the only product project; exe, packed as a dotnet tool
  Program.cs               command dispatch (no command-line library)
  Cli/                     init, check, test, build, hook, engine commands; output formatting
  Mcp/                     the three MCP tools over the engine client
  Hooks/                   payload parsing and response writing per harness
  Engine/                  pipe server, lifecycle, request handling
  Graph/                   static project graph, file ownership, test-project detection
  Compilation/             lazy loader, sync, HEAD baseline, surface fingerprint
  Check/                   scoping, analyzer filtering, delta
  Tests/                   selection walk, shadow emit, test host launch, result parsing
  Dotnet/                  dotnet process runner, canonical-diagnostic parser
tests/Fuse.Tests/          unit and integration tests
evals/Fuse.Evals/          console app; the eval suites in section 7; not part of the test run
```

### 5.2 Dependencies

`Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`, `Microsoft.Build.Locator`, and `Microsoft.Build` (for `ProjectGraph`, excluded from runtime assets and resolved from the SDK through the locator). Plus `ModelContextProtocol` for the MCP server. That is five packages. No command-line library (seven commands do not need one), no DI container, no logging framework (one log file writer), no JSON library beyond `System.Text.Json`.

### 5.3 Code rules

- No interface without two production implementations. Tests use real objects over real fixture repositories.
- A file holds one type plus its private helpers. No file over 400 lines.
- No static mutable state except the engine's single `Solution` holder.
- Every process start uses `ArgumentList` and kills its process tree on cancellation.
- Every variable-length argument list (git paths, test filters) goes through stdin or is chunked.
- Errors are a closed enum in one file: `NotARepository`, `NoProjects`, `Loading`, `RestoreNeeded`, `LoadFailed`, `Restart`, `Timeout`, `Internal`. Each has one message template that names the fix.

### 5.4 Tests

- **Fixture generator.** Tests create real repositories in temp directories from C# builders: a library, a dependent app, a Razor project, a source generator, a multi-targeted project, an analyzer package with error-severity rules, and xunit, NUnit, MSTest and MTP test projects. Each fixture is restored once per test run and copied per test.
- **Engine tests** drive the engine in process over fixtures and cover:
  - body edits, surface edits and cross-project breaks;
  - added overloads that cause ambiguity;
  - new, deleted and renamed files;
  - `.razor` edits and `.csproj` edits;
  - multi-edit sequences and branch switches;
  - pre-existing errors that must not count;
  - watcher overflow.
- **Process tests** run the real executable:
  - start, connect and version restart;
  - idle exit, and disconnect cancelling work;
  - two roots at once, and junction and non-ASCII paths.
- **Hook tests** feed recorded payloads from each harness and assert the output bytes and exit codes.
- **Selection tests** assert that each fixture mutation's failing tests are inside the selected set.
- **CI** runs build, tests and format on Windows, Linux and macOS. A test that needs the SDK runs everywhere, because the product needs the SDK anyway.

## 6. What does not exist in v5

This list is final for v5, and nothing on it returns without an eval result that justifies it:

- MCP resources, prompts, server instructions, and any tool beyond the three; LSP server or proxy; skill files; rule blocks in `CLAUDE.md` or `AGENTS.md`.
- Search, navigation, symbol lookup, references, impact analysis, wiring resolution (DI, MediatR, routes), context packing, token reduction, review packets.
- Refactors and any tree write other than `fuse init` writing hook settings.
- Persistent index, SQLite, compiler logs, capture bundles, secret scanning.
- Configuration file, environment variables, verbosity or format flags, JSON output.
- Speculative checks of unwritten content, candidate racing.
- Self-update, OS services, installers, winget, release archives, MCP registry. The one channel is the NuGet tool package.
- Python, JavaScript, VB and F#.
- The documentation site. v5 ships a README covering install, what the hooks do, the output, and the limits in section 4.5.
- v4's roadmap, briefing, benchmark suites and results, which move to an archive branch. The `v4.4.0` tag keeps the old product.

## 7. Evals

Four suites in `evals/Fuse.Evals`, each with a pre-registered gate. They run on the fixture repositories plus a pinned set of public repositories chosen for coverage:

- NodaTime (multi-project library, multi-targeting)
- eShopOnWeb or a newer reference application (ASP.NET Core, Razor, EF Core)
- a repository with source generators
- a repository with error-severity analyzers enforced in build
- one repository with 150 or more projects

Every measurement goes through the `fuse` executable, never internal methods.

### 7.1 Check correctness

- **Corpus:** Roslyn syntax rewriters generate mutations: removed members, renamed members, changed signatures, changed accessibility, added overloads, type changes, removed usings, and deleted files. Mutations are applied singly and as sequences of 2 to 5 edits across projects.
- **Truth:** a real `dotnet build` of the mutated tree, diffed against a real build at `HEAD`.
- **Metrics:** false green (build fails, `fuse check` reports nothing new), false red (`fuse check` reports an error the build does not), and file-level agreement.
- **Gate:** false green 0 across the whole corpus; false red under 0.5 percent. Every divergence gets a written cause: either a fix, or a documented limit in section 4.5.

### 7.2 Test selection

- **Corpus:** behavior mutations (flipped conditions, off-by-one errors, dropped statements, changed constants) in non-test code of repositories with real test suites.
- **Truth:** the full test suite run through `dotnet test`.
- **Metrics:** missed failing tests, selected fraction, and wall time of `fuse test` against `dotnet test` with the same scope.
- **Gate:** missed failing tests 0. The selected fraction and time are reported, not gated.

### 7.3 Latency and footprint

- **Measurements:** post-edit hook latency for body edits and for surface edits with dependents (P50 and P95); cold time to first check answer; `fuse` client overhead against a warm engine; engine RSS after 1, 10 and 100 touched projects; `fuse test` fast path against slow path.
- **Gates:**
  - Body-edit P95 under 300 ms warm.
  - Surface-edit P95 under 2 s on the 150-project repository.
  - Client overhead under 100 ms. If this fails, split out an AOT client (D2).
  - First answer after cold start under 30 s for a leaf project on the 150-project repository.

### 7.4 Agent outcome

- **Design:** paired A/B on a verified task corpus with fail-to-pass oracles: the harness's native tools against native tools plus `fuse init` hooks. The current Claude model runs in Claude Code, with three rollouts per task per arm. At least 60 tasks, 40 of them from repositories with 20 or more projects.
- **Metrics:** true pass@1 (oracle), false-done (the agent stopped with the oracle red), wall-clock time, total tokens, and `dotnet` build and test invocations.
- **Gate:** pass@1 no worse than native, and either wall-clock time or tokens lower, with the 95 percent confidence interval excluding zero. If this gate fails, the product thesis is wrong. That result is recorded, and v5 does not ship as described.

## 8. Build order

The order follows the dependencies between pieces. Each step ends when its gate is green. There are no dates.

1. **Evals first.** Build the fixture generator, the pinned public repositories, the mutation generators, and the correctness and selection harnesses, using `dotnet build` and `dotnet test` as both truth and a trivial baseline implementation. This fixes the measuring stick before any engine code exists.
2. **Graph and loading.** Static graph, file ownership, and lazy `OpenProjectAsync` loading with incremental cone expansion. First confirm with a small experiment that `MSBuildWorkspace` reuses already-loaded projects when a second project is opened into the same workspace. If it does not, load dependents into a fresh workspace and merge `ProjectInfo`s into the held `Solution`. Gate: cold-start budget from section 7.3 on the 150-project repository.
3. **Check.** Sync, `HEAD` baseline, surface fingerprint, scoping, and error-level analyzers. Gate: section 7.1.
4. **Engine process, hooks and MCP.** Pipe, lifecycle, `fuse hook` for Claude Code first, then Cursor, Gemini and Codex, the MCP server, `fuse init`. Gate: process tests, client overhead, and a recorded Claude Code session where a cross-project break appears after the edit and a clean edit produces nothing.
5. **Compact build and slow-path test.** `fuse build`, `fuse test` over MSBuild, output parsing, and the `PreToolUse` rewrite. Gate: output tests.
6. **Selection.** Gate: section 7.2.
7. **Fast test path.** Shadow output, emit, and test-host launch. Gate: section 7.2 still has zero missed failures with the fast path on, plus the fast-versus-slow timing reported in section 7.3.
8. **Agent outcome.** Gate: section 7.4. Only after this passes does Fuse 5.0.0 ship to NuGet.

## 9. Risks and kill criteria

- **Design-time cost at scale.** If lazy loading cannot meet the cold-start or RSS budget on the 150-project repository, the engine is not viable there. Response: measure where the time goes. If `MSBuildWorkspace` itself is the floor, a compiler-log loader (Basic.CompilerLog, requires one full build) is the alternative worth an experiment.
- **Surface fingerprint misses a break.** For example, an extension-method addition that changes binding elsewhere. The correctness eval exists to find these. The response is a wider candidate set for that declaration kind, never a relaxed gate.
- **Fast test path diverges from a real build.** The selection eval runs with the fast path on. Any divergence routes that case to the slow path permanently.
- **Harness hook changes.** Hooks are young APIs. Keep the adapters in one folder with recorded payload tests, so a format change is a small edit.
- **Microsoft closes the gap.** If Claude Code gains pull diagnostics, the Roslyn language server gives per-file errors after edits. Fuse's remaining edge would be cross-project checking against `HEAD`, the Stop gate, and fast affected tests. The agent-outcome eval decides whether that edge is enough.
- **The thesis is wrong.** If section 7.4 fails, compiler feedback through hooks does not improve agent outcomes. Record the result, and do not ship.

## 10. Lessons carried from v4

These are knowledge, not code:

- An editorconfig severity survives a syntax-tree swap only if the tree options provider is carried over. v4 handled this in `ForkedTreeOptionsProvider` (`ResidentWorkspace.cs:450-466`). With `WithDocumentText` inside a real workspace it is automatic, which is one reason v5 uses a workspace.
- Solution selection by name is fragile. v4 bound NodaTime's 2-project tooling solution instead of the 21-project product solution until a late fix. v5 avoids the question.
- A child process that inherits the parent's stdio can hang the parent (commits 2af9343 and 8659c38). Redirect every child's streams.
- A build without `--no-incremental` produces no compiler invocation for up-to-date projects, which is why the v4 resident workspace held 2 of 21 projects. v5 does not rehydrate compiler logs, so this does not apply unless the compiler-log fallback in section 9 is ever tried.
- A benchmark whose gate can be re-derived is not a gate. The v5 gates are written here before any code exists.

The next step is build order step 1: the fixture generator and the correctness harness, with `dotnet build` as the reference implementation.
