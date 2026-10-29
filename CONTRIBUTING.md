# Contributing

This page is for anyone who wants to change Fuse. It says what to read first, the gates a change must pass, and what a pull request needs.

Read [AGENTS.md](AGENTS.md) first. It covers the repository layout, the build, test and format commands, and the rules the code follows. [Architecture](https://fuse.codes/docs/architecture) describes the layers, which namespace may use which, and the words the code and the output use. Read it before you add a namespace, a type that other namespaces use, or a word to the output. [How Fuse Works](https://fuse.codes/docs/how-it-works) explains what the code does.

## Make a change

1. Branch from `main`.
2. Make the change with tests. The tests generate real git repositories and restore them, so the first run needs NuGet access and a git identity (`user.name` and `user.email`).
3. Run the gates. CI runs them on Windows, Linux and macOS:

    ```bash
    dotnet build Fuse.slnx -c Release
    dotnet test --solution Fuse.slnx -c Release --no-build
    dotnet format Fuse.slnx --verify-no-changes
    ```

    A test you add must run. Confirm that the test count went up.
4. If your change touches checking, test selection or the path of a request, run the eval that the [What to run](https://fuse.codes/docs/evals#what-to-run) section names for it. Run it as a targeted run through the GitHub CLI, for example `gh workflow run evals.yml --ref <branch> -f repos='["NodaTime"]' -f suites='["latency"]'`, and attach its result file to the pull request. Evals run on GitHub Actions and never on your machine. A local machine runs other work, so its numbers are not comparable. Commit the result files from the run's artifacts.
5. Sign off every commit with the Developer Certificate of Origin. `git commit -s` adds the `Signed-off-by:` line that [DCO.txt](DCO.txt) describes. The DCO check fails a pull request when a commit's sign-off does not match its author. `git rebase --signoff` adds the line to commits you already made.
6. Open a pull request that says what changed and why, and that names the AI tools you used, as the [AI Policy](https://fuse.codes/docs/ai-policy) asks. The template lists the gates and the sign-off.

Everyone who takes part follows the [Code of Conduct](.github/CODE_OF_CONDUCT.md).

CI also packs the tool (`.github/workflows/package.yml`). It packs the native client for win-x64, linux-x64, linux-arm64 and osx-arm64, each on its own operating system, and the Linux ones against a glibc 2.27 sysroot. It packs the framework-dependent packages for every other platform. It then installs the packages on each platform and runs `.github/scripts/check-installed-tool.sh`. That script checks that the command is the native client, that `fuse check` finds an introduced error in a fresh repository, that the hooks and the MCP server answer, and that one engine of the same build served every call.

To build the native client locally, publish for your platform, for example `dotnet publish src/Fuse -c Release -r win-x64 -o artifacts/native`. The output holds the native `fuse` and, beside it, the `fuse.dll` it starts the engine from. The native compile needs the platform's C toolchain:

- on Windows, the Visual Studio "Desktop development with C++" workload, with `vswhere.exe` reachable (it is in `C:\Program Files (x86)\Microsoft Visual Studio\Installer`, which a Developer PowerShell puts on the `PATH`)
- on Linux, `clang` and `zlib1g-dev` or the distribution's equivalent
- on macOS, the Xcode command line tools

A plain `dotnet build` runs the trim and AOT analyzers, so code the native client cannot run fails the build without a native compile.

## Documentation

The documentation is part of the website in `site/`. The site is static HTML with no build step, and it is served at fuse.codes from `main`. The pages are in `site/docs/`. The stylesheet, icons and images they share with the home page are in `site/assets/`. The root of `site/` holds only the home page, the not-found page, and the files hosts and crawlers read there (`_headers`, `_redirects`, `robots.txt`, `sitemap.xml`, `llms.txt`).

Serve the site with `npx wrangler dev` from the repository root to see it as it will look: it serves `site/` the way fuse.codes does, so links without `.html` resolve. Opening a file straight from the checkout does not follow those links. Each documentation page carries the same header, sidebar and footer, so a new page adds its link to the sidebar of every page, and its URL to `site/sitemap.xml` and `site/llms.txt`. A page that moves gets a line in `site/_redirects`, so links to its old address keep working.

The Markdown files that remain are the ones GitHub and the release workflow read. The root `README.md` is also the package's page on nuget.org, so its links are absolute. Its images use paths under `site/`. `dotnet pack` rewrites those paths to the version's tag on GitHub, because nuget.org renders no image with a relative path.

When a change alters output, a command or a limit, update the page that describes it in the same pull request. Every example in a page is an example you ran, and every number quoted from a benchmark follows the rules in [Quoting the numbers](https://fuse.codes/docs/evals#quoting-the-numbers).

## Releases

The version lives in `Directory.Build.props`. A release is a `vX.Y.Z` tag that matches it. The publish workflow checks the match, packs the tool with the package workflow, and pushes the RID packages to NuGet, then the `Fuse` package that points to them. It then creates a GitHub release whose notes are the version's section of [CHANGELOG.md](CHANGELOG.md).
