<!-- mcp-name: io.github.Litenova-Solutions/fuse -->

# Fuse

Catch a bad C# edit before your coding agent writes it. From a .NET project directory:

```bash
dotnet tool install -g Fuse
fuse mcp install --rules
```

Reload Claude Code, Cursor, or GitHub Copilot, then ask:

```text
Check the next edit with fuse_check before writing it.
```

<p align="center">
  <img src="assets/demo/fuse-check-demo.gif" alt="An agent proposes an edit with an invalid OrderOptions member. fuse_check returns CS1061 and a repair packet before the edit lands, then verifies the corrected proposal." width="820">
</p>

Fuse runs against the repository on your machine. It gives an existing coding agent
compiler-backed results and a typed map of your .NET application. No hosted Fuse service
or downloaded embedding model is required.

## Use It During Daily Work

- Before an edit, `fuse_check` tests the proposed file and returns the compiler diagnostics
  without changing the working tree.
- Before changing a public method, `fuse_impact` finds callers, implementations, and
  referencing types.
- When following application flow, `fuse_find` traces a service, request, route, or
  configuration section to the code that handles it.
- When reviewing a branch, `fuse_review` starts from the git diff and returns focused
  context from related files.

<p align="center">
  <img src="assets/fuse-wiring-example.svg" alt="Fuse resolves an interface through dependency injection registration to its concrete implementation and related callers." width="820">
</p>

## What the Recorded Results Cover

Every result below comes from `tests/benchmarks/results` and has a reproduction command
on the [benchmarks page](https://fuse.codes/docs/project/benchmarks).

- In the recorded OrderingApp test app, `fuse_check` correctly separated all 1,000
  compiler-labeled breaking and neutral edits. This covers that app and edit set.
- In the same app, Fuse matched all 24 expected .NET wiring links with no extra matches.
- Across 69 real pull requests, branch review retained every git-changed file by
  construction at 93.4 percent precision. It returned focused context with a median size
  of 1,026 tokens.

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
- [Tool reference](https://fuse.codes/docs/reference/mcp-tools)
- [Install options](https://fuse.codes/docs/start/install)
- [Contributing](https://fuse.codes/docs/project/contributing)

Apache 2.0. Copyright (c) 2026 Litenova Solutions. See [LICENSE](LICENSE) and
[NOTICE](NOTICE).
