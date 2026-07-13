<!-- mcp-name: io.github.Litenova-Solutions/fuse -->

# Fuse

Fuse is a local .NET tool with a persistent semantic index and pre-write compiler
verification for coding agents. From a .NET project directory:

```bash
dotnet tool install -g Fuse
fuse mcp install --rules
```

Reload your MCP client, then ask:

```text
Resolve IOrderService to its implementation, then check the proposed OrderService.cs edit
with fuse_check before writing it.
```

The first MCP tool call builds `.fuse/fuse.db` automatically. Run `fuse index` first if
you want a warm index before the agent's first turn. `fuse mcp install --rules` also adds
`.fuse/` to `.gitignore` at project scope.

<p align="center">
  <img src="assets/demo/fuse-check-demo.gif" alt="An agent proposes an edit with an invalid OrderOptions member. fuse_check returns CS1061 and a repair packet before the edit lands, then verifies the corrected proposal." width="820">
</p>

Fuse runs on your machine. It builds a warm persistent index in `.fuse/fuse.db`, walks a
typed graph of DI registrations, handlers, routes, and callers, and lets your coding agent
typecheck a proposed file before writing it. No hosted Fuse service or downloaded embedding
model is required.

## Warm Index and Typed Graph

Fuse indexes the workspace once through MSBuild and Roslyn, then reuses the store on later
calls. When a project loads semantically, the graph records DI registrations, request
handlers, routes, options bindings, and call edges. When it does not, Fuse falls back to
syntax-level indexing for that project and reports the mode.

- **Resolve wiring.** `fuse_find` traces a service, request, route, or configuration
  section to the code that handles it. Text search finds `IOrderService`; Fuse follows the
  registration to the implementation that runs.
- **Pack branch context.** `fuse_review` seeds on the git diff and returns related callers,
  handlers, and tests with provenance. On 69 recorded pull requests the median response was
  1,026 tokens at 93.4 percent precision (`review.json`).
- **Read warm.** On the recorded NodaTime run, exact symbol lookup took 2.2 ms at the
  median, task localization 42 ms, and review planning 117 ms (`performance.json`;
  timings are environment-dependent).

<p align="center">
  <img src="assets/fuse-wiring-example.svg" alt="Fuse resolves an interface through dependency injection registration to its concrete implementation and related callers." width="820">
</p>

## Use It During Daily Work

- Before an edit, `fuse_check` tests the proposed file and returns the compiler
  diagnostics without changing the working tree. Supported API-shape errors come back
  with a repair packet carrying a machine-applicable fix; in the recorded run, the top
  suggestion repaired 20 of 20 near-miss member and type errors (`diagbench.json`).
- Before changing a public method, `fuse_impact` finds callers, implementations, and
  referencing types. Given a package id and two versions, it returns the break set for
  that NuGet upgrade.
- When a signature must change, `fuse_refactor` stages the refactor as a diff and
  returns it only when the compiler reports no new diagnostic.
- After an edit, `fuse_test` selects and runs the test types that reach the changed
  symbol instead of starting with the whole suite.

Every answer names how it was produced. Fuse calls this the **verification grade**:
oracle grade checks against the compilation captured from the real build, build grade
runs a scoped `dotnet build`, and when neither compiler path can answer, Fuse abstains
and names the missing prerequisite instead of guessing.

## What the Recorded Results Cover

Every result below comes from `tests/benchmarks/results` and has a reproduction command
on the [benchmarks page](https://fuse.codes/docs/project/benchmarks).

- Across 1,000 compiler-labeled edits in the recorded OrderingApp test app (500 breaking,
  500 neutral), `fuse_check` reported zero broken edits as clean and rejected zero valid
  edits (`checkgate.json`).
- In the same app, Fuse matched all 24 expected .NET wiring links with no extra matches
  (`semantics.json`).
- Across 69 real pull requests, branch review retained every git-changed file by
  construction at 93.4 percent precision with a median size of 1,026 tokens (`review.json`).
- In the reduced-scope agent-loop run, the Fuse arm's edits passed the project's own
  tests on the first attempt in 89 percent of scored rollouts versus 82 percent for
  native tools, with overlapping confidence intervals. The Fuse arm declared success on a
  failing edit 8 times versus 9 for native. Build and test calls were essentially equal at
  3.1 versus 3.2 (`loop.json`).
- Across four recorded repositories, skeleton reduction removed 38 to 44 percent of
  tokens while keeping every public and protected type name and 96.3 to 99.4 percent of
  method names (`reduce.json`).

The opt-in resident workspace answered repeated `fuse_check` calls in 31.2 ms at the
median on the recorded NodaTime run (`resident-latency.json`). That path is faster than a
full build for speculative checks; it does not replace normal builds or tests before merge.

These measurements have defined limits. Read the
[full methods and results](https://fuse.codes/docs/project/benchmarks) before comparing
tools or applying the figures to another repository.

## Local and Write-Safe

Fuse reads source, compiler state, git metadata, and its local `.fuse/fuse.db` index.
Read, check, impact, refactor, and review operations do not write the working tree.
`fuse_workspace` with `action=apply` is the one explicit tree-write path, and it is a dry
run unless `write=true`.

Compiler-backed wiring analysis is .NET-only. Other languages receive syntax-level search
and reduction.

## Start Here

- [Quickstart](https://fuse.codes/docs/start/quickstart)
- [Connect your coding agent](https://fuse.codes/docs/start/connect-your-ai)
- [How Fuse works](https://fuse.codes/docs/concepts/how-fuse-works)
- [Tool reference](https://fuse.codes/docs/reference/mcp-tools)
- [Install options](https://fuse.codes/docs/start/install)
- [Contributing](https://fuse.codes/docs/project/contributing)

Apache 2.0. Copyright (c) 2026 Litenova Solutions. See [LICENSE](LICENSE) and
[NOTICE](NOTICE).
