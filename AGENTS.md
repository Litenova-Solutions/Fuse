# Fuse: contributor and agent guide

Fuse is one .NET tool (`fuse`) that keeps a warm Roslyn compilation of a repository and gives coding agents the errors their edits introduced, affected-test runs, and compact build output, through agent hooks and a three-tool MCP server. [README.md](README.md) describes the product, [site/docs/index.html](site/docs/index.html) indexes the documentation, and [site/docs/how-it-works.html](site/docs/how-it-works.html) describes how it works.

## Layout

- `src/Fuse`: the only product project, packed as the `Fuse` dotnet tool. Its namespaces are layers; [site/docs/architecture.html](site/docs/architecture.html) says which may use which, in what order to read them, and what its words mean. From the top:
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
- `site/`: the website at fuse.codes, static HTML deployed from `main` with no build step. Cloudflare Workers serves it as static assets, configured by `wrangler.jsonc` at the repository root.
  - `index.html` and `404.html`: the home page and the not-found page.
  - `docs/`: all documentation, one page per file, with `index.html` as the overview.
  - `assets/`: `style.css`, the icons, `og.png`, and `benefits.svg`, which `dotnet run --project evals/Fuse.Evals -c Release -- chart` renders from `evals/results`.
  - `_headers` and `_redirects`: response headers, and redirects from addresses that moved.
  - `robots.txt`, `sitemap.xml` and `llms.txt`: for crawlers and agents.
- Documents outside `site/docs/`: only the files GitHub and the release workflow read, the README, `CONTRIBUTING.md`, `SECURITY.md` and `CHANGELOG.md` in the root, and `CODE_OF_CONDUCT.md` and `SUPPORT.md` in `.github/`.

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
- Evals run on GitHub Actions, never on a developer's machine: add the `evals` label to the pull request (latency and analyzers), or start the Evals workflow from the Actions tab with the repositories and suites to run. Commit the result files from the run's artifacts. A local machine runs other work and differs from the runners, so its numbers are not comparable and are never quoted.
- Iterate with targeted runs, through the GitHub CLI. While a change is in progress, rerun only what it can affect: `gh workflow run ci.yml --ref <branch> -f filter="--filter-class <class> ..." -f os='["ubuntu-latest"]'` for the test classes it touches, and `gh workflow run evals.yml --ref <branch> -f repos='["<repo>"]' -f suites='["<suite>"]'` (with `-f latency_parts='["main"]'` for one latency part) for the eval that measures it. The full CI run and the full eval set run once the targeted runs pass. A failing job's engine logs are in its artifact (`engine-logs/`), so read them before running it again. When a run is slow, split it: rebalance the CI shards in `.github/workflows/ci.yml` and the truth shards in `evals.yml` so no job waits on one long tail, within the 20 concurrent jobs the repository gets.
- Numbers quoted in docs come from files in `evals/results`. Counts are quoted exactly. Times, sizes and percentages are rounded half up for display: seconds to two decimals below 10 s and one decimal from 10 s, milliseconds and megabytes to whole numbers, percentages to at most one decimal. Each benchmark table names the result files it comes from.
- A file holds one type plus its private helpers.
- Layers follow [site/docs/architecture.html](site/docs/architecture.html). A new namespace gets a row in its dependency table and in `NamespaceDependencyTests` in the same change.
- One word per concept, in code, output, comments and docs: the vocabulary in [site/docs/architecture.html](site/docs/architecture.html) decides. That means no synonyms, not repeating the full phrase every time: once a thing is named, "it" or a short name is fine. Text says what is true now, in plain sentences, with keyboard punctuation only. Docs address the reader as "you" and explain a mechanism on one page, which other pages link to rather than repeat.
- New tests must run: confirm the test count went up.
- A commit an agent helped write carries an `Assisted-by:` trailer, and its `Signed-off-by:` line is the person's, as [site/docs/ai-policy.html](site/docs/ai-policy.html) says.
- Every documentation page carries the same header, sidebar and footer. A new page adds its link to the sidebar of every page, its URL to `site/sitemap.xml` and `site/llms.txt`, and its pager links to its neighbors. Pages link each other by their canonical address, without `.html` (`commands#fuse-check`, `../`), which fuse.codes serves directly; a `.html` link costs a redirect. A page's title, description, Open Graph and Twitter tags and structured data say the same thing, and its `dateModified` and sitemap `lastmod` change with its content. A page opens with who it is for and what they do after it, and explains each term where it first appears.

## Releases

The version lives in `Directory.Build.props`. A release is a `vX.Y.Z` tag matching it; the publish workflow checks the match. A release description is scoped to one baseline, named in the text: a major and its first preview carry the full changelog for that major, and every later release compares against the immediately previous version only. [CHANGELOG.md](CHANGELOG.md) keeps the cumulative history, and the release workflow reads its section as the release notes. Each release also gets a section with its upgrade steps in [site/docs/changelog.html](site/docs/changelog.html), and its row in that page's supported versions table, in the same change.

A changelog is read by a user deciding whether to upgrade and by one checking what changed afterward. Follow the changelog guidance in `~/.config/agent-rules/writing/technical.md`. For Fuse, name the immediately previous version at the top, put required upgrade actions near the top, list only the groups that have entries, and end an entry with its issue link and a link to the page that explains the details when one exists. Keep [CHANGELOG.md](CHANGELOG.md) and [site/docs/changelog.html](site/docs/changelog.html) synchronized in substance; the site section adds the upgrade steps. Implementation detail belongs in an entry only when it explains observable behavior, a limit or an action the reader must take.
