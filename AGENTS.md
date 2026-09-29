# Fuse: contributor and agent guide

Fuse is one .NET tool (`fuse`) that keeps a warm Roslyn compilation of a repository and gives coding agents the errors their edits introduced, affected-test runs, and compact build output, through agent hooks and a three-tool MCP server. [README.md](README.md) describes the product, [site/docs.html](site/docs.html) indexes the documentation, and [site/how-it-works.html](site/how-it-works.html) describes how it works.

## Layout

- `src/Fuse`: the only product project, packed as the `Fuse` dotnet tool. Its namespaces are layers; [site/architecture.html](site/architecture.html) says which may use which, in what order to read them, and what its words mean. From the top:
  - `Program.cs`: parses the command line and picks a surface or the engine.
  - `Hooks/`, `Mcp/`, `Harnesses/`: `fuse hook`, `fuse mcp`, and `fuse init` with one class per harness.
  - `Operations/`: the check, test and build operations shared by every surface, and their output.
  - `Engine/Client/`: finds, starts and calls the engine.
  - `Protocol/`: the pipe's request and response records.
  - `Engine/`: the engine process: pipe server, request routing, preload, mapping results to `Protocol`.
  - `Check/`, `Testing/`: the check algorithm and test selection, each with a `Model/` folder read first.
  - `Changes/`: which declarations differ between HEAD and the working tree.
  - `Workspace/`: the current and baseline Roslyn solutions.
  - `Repo/`, `Graph/`, `Dotnet/`: git and the working tree, MSBuild evaluation, child processes and their output parsers.
  - `Paths/`, `Failures/`, `Telemetry/`: `RepoRoot` and `RepoPath`, `FuseException` and `ErrorCode`, `PhaseTimes` and `EngineLog`.
- `tests/Fuse.Tests`: unit, engine and process tests over generated fixture repositories, and `Architecture/NamespaceDependencyTests`, which fails the build when a namespace uses one it may not.
- `evals/Fuse.Evals`: the correctness, selection and latency evals, and the chart renderer; results in `evals/results`.
- `site/`: the website at fuse.codes and all documentation, static pages sharing `style.css`, with `robots.txt`, `sitemap.xml` and `llms.txt` for crawlers and agents, and `benefits.svg`, which `dotnet run --project evals/Fuse.Evals -c Release -- chart` renders from `evals/results`. Cloudflare Workers serves it as static assets, configured by `wrangler.jsonc` at the repository root with response headers in `site/_headers`, deployed from `main` with no build step. The only other documents are the files GitHub and the release workflow read: the README, `CONTRIBUTING.md`, `SECURITY.md` and `CHANGELOG.md` in the root, and `CODE_OF_CONDUCT.md` and `SUPPORT.md` in `.github/`.

## Build, test, format

```bash
dotnet build Fuse.slnx -c Release
dotnet test --solution Fuse.slnx -c Release --no-build
dotnet format Fuse.slnx --verify-no-changes
```

Tests generate real git repositories and restore them, so the first run needs NuGet access.

## Rules

- The product answers from the working tree as it is on disk, compared with HEAD. Never report an error that already existed at HEAD as introduced.
- A hook must never break the agent's session: `fuse hook` reports only introduced errors and missing restores, and every internal failure exits 0 with no output and logs to `hook.log` in the state directory.
- No configuration knobs. A behavior that needs a setting is a behavior to decide, not to expose.
- The engine never writes the working tree and never runs `dotnet restore` on its own. Its state (logs, shadow test output) lives in the user's local application data, never in the repository.
- The pipe protocol needs no versioning by hand: every request carries `EngineVersion.Build`, and an engine from another build restarts.
- Child processes take argument lists, never shell strings. Variable-length lists (paths, filters) are bounded or chunked.
- Numbers quoted in docs come from files in `evals/results`. Counts are quoted exactly. Times, sizes and percentages are rounded half up for display: seconds to two decimals below 10 s and one decimal from 10 s, milliseconds and megabytes to whole numbers, percentages to at most one decimal. Each results table names the result files it comes from.
- A file holds one type plus its private helpers.
- Layers follow [site/architecture.html](site/architecture.html). A new namespace gets a row in its dependency table and in `NamespaceDependencyTests` in the same change.
- One word per concept, in code, output, comments and docs: the vocabulary in [site/architecture.html](site/architecture.html) decides. Text says what is true now, in plain sentences, with keyboard punctuation only.
- New tests must run: confirm the test count went up.
- A commit an agent helped write carries an `Assisted-by:` trailer, and its `Signed-off-by:` line is the person's, as [site/ai-policy.html](site/ai-policy.html) says.
- Every documentation page carries the same header, sidebar and footer. A new page adds its link to the sidebar of every page, its URL to `sitemap.xml` and `llms.txt`, and its pager links to its neighbors. A page opens with who it is for and what they do after it, and explains each term where it first appears.

## Releases

The version lives in `Directory.Build.props`. A release is a `vX.Y.Z` tag matching it; the publish workflow checks the match. A release description is scoped to one baseline, named in the text: a major and its first preview carry the full changelog for that major, and every later release compares against the immediately previous version only. [CHANGELOG.md](CHANGELOG.md) keeps the cumulative history.
