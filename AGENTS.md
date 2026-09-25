# Fuse: contributor and agent guide

Fuse is one .NET tool (`fuse`) that keeps a warm Roslyn compilation of a repository and gives coding agents the errors their edits introduced, affected-test runs, and compact build output, through agent hooks and a three-tool MCP server. [README.md](README.md) describes the product; [docs/design.md](docs/design.md) describes how it works.

## Layout

- `src/Fuse`: the only product project, packed as the `Fuse` dotnet tool.
  - `Cli/`: the check, test and build operations shared by the CLI, hooks and MCP.
  - `Engine/`: the per-repository engine process, its pipe protocol client, and the detached launcher.
  - `Protocol/`: request and response records for the pipe.
  - `Repo/`: repository root, git HEAD and blob reading, change tracking.
  - `Graph/`: MSBuild evaluation of every project (ownership, references, test projects).
  - `Workspace/`: the lazily loaded current and HEAD-baseline solutions.
  - `Check/`: the check algorithm (surface diff, scoping, analyzer selection, delta).
  - `Testing/`: affected-test selection, the shadow-emit fast path, planning.
  - `Dotnet/`: process running and output parsing.
  - `Hooks/`: `fuse hook` adapters per harness and `fuse init`.
  - `Mcp/`: the MCP server.
- `tests/Fuse.Tests`: unit, engine and process tests over generated fixture repositories.
- `evals/Fuse.Evals`: the correctness, selection and latency evals, and the chart renderer; results in `evals/results`.
- `site/`: the website at fuse.codes, one static page (`index.html`), the icon, and `benefits.svg`, which `dotnet run --project evals/Fuse.Evals -c Release -- chart` renders from `evals/results`. Vercel deploys it from `main` with `site` as the project root and no build step.

## Build, test, format

```bash
dotnet build Fuse.slnx -c Release
dotnet test --solution Fuse.slnx -c Release --no-build
dotnet format Fuse.slnx --verify-no-changes
```

Tests generate real git repositories and restore them, so the first run needs NuGet access.

## Rules

- The product answers from the working tree as it is on disk, compared with HEAD. Never report an error that already existed at HEAD as introduced.
- A hook must never break the agent's session: `fuse hook` reports only new errors and missing restores, and every internal failure exits 0 with no output and logs to `hook.log` in the state directory.
- No configuration knobs. A behavior that needs a setting is a behavior to decide, not to expose.
- The engine never writes the working tree and never runs `dotnet restore` on its own. Its state (logs, shadow test output) lives in the user's local application data, never in the repository.
- The pipe protocol needs no versioning by hand: every request carries `EngineVersion.Build`, and an engine from another build restarts.
- Child processes take argument lists, never shell strings. Variable-length lists (paths, filters) are bounded or chunked.
- Numbers quoted in docs come from files in `evals/results`, exactly as recorded.
- A file holds one type plus its private helpers. No interface without two implementations.
- New tests must run: confirm the test count went up.

## Releases

The version lives in `Directory.Build.props`. A release is a `vX.Y.Z` tag matching it; the publish workflow checks the match. A release description is scoped to one baseline, named in the text: a major and its first preview carry the full changelog for that major, and every later release compares against the immediately previous version only. `CHANGELOG.md` keeps the cumulative history.
