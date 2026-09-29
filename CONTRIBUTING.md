# Contributing

This page is for anyone who wants to change Fuse: what to read first, the gates a change must pass, and what a pull request needs.

Read [AGENTS.md](AGENTS.md) first: it covers the repository layout, the build, test and format commands, and the rules the code follows. [Architecture](https://fuse.codes/docs/architecture) describes the layers, which namespace may use which, and the words the code and the output use; read it before adding a namespace, a type other namespaces use, or a word to the output. [How it works](https://fuse.codes/docs/how-it-works) explains what the code does.

## Make a change

1. Branch from `main`.
2. Make the change with tests. The tests generate real git repositories and restore them, so the first run needs NuGet access and a git identity (`user.name` and `user.email`).
3. Run the gates, which CI runs on Windows, Linux and macOS:

    ```bash
    dotnet build Fuse.slnx -c Release
    dotnet test --solution Fuse.slnx -c Release --no-build
    dotnet format Fuse.slnx --verify-no-changes
    ```

    A test you add must run: confirm that the test count went up.
4. If the change touches checking, test selection or the path of a request, run the eval [Evals](https://fuse.codes/docs/evals#what-to-run) names for it, for example `dotnet run --project evals/Fuse.Evals -c Release -- correctness fixture`, and attach its result to the pull request.
5. Sign off every commit with the Developer Certificate of Origin: `git commit -s` adds the `Signed-off-by:` line that [DCO.txt](DCO.txt) describes. The DCO check fails a pull request with a commit whose sign-off does not match its author; `git rebase --signoff` adds the line to commits you already made.
6. Open a pull request that says what changed and why, and which AI tools you used, as the [AI policy](https://fuse.codes/docs/ai-policy) asks. The template lists the gates and the sign-off.

Everyone who takes part follows the [code of conduct](.github/CODE_OF_CONDUCT.md).

CI also packs the tool, installs the package, and checks that `fuse check` finds an introduced error in a fresh repository.

## Documentation

The documentation is part of the website in `site/`, static HTML with no build step, served at fuse.codes from `main`. The pages are in `site/docs/`, and the stylesheet, icons and images they share with the home page are in `site/assets/`; the root of `site/` holds only the home page, the not-found page, and the files hosts and crawlers read there (`_headers`, `_redirects`, `robots.txt`, `sitemap.xml`, `llms.txt`). Open a page in a browser straight from the checkout to see it as it will look. Each documentation page carries the same header, sidebar and footer, so a new page adds its link to the sidebar of every page, and its URL to `site/sitemap.xml` and `site/llms.txt`. A page that moves gets a line in `site/_redirects`, so links to its old address keep working. The Markdown files that remain are the ones GitHub and the release workflow read. The root `README.md` is also the package's page on nuget.org, so its links are absolute. Its images use paths under `site/`, which `dotnet pack` rewrites to the version's tag on GitHub, because nuget.org renders no image with a relative path. A change that alters output, a command or a limit updates the page that describes it in the same pull request. Every example in a page is one you ran, and every number quoted from a measurement follows the rules in [Evals](https://fuse.codes/docs/evals#quoting-the-numbers).

## Releases

The version lives in `Directory.Build.props`. A release is a `vX.Y.Z` tag that matches it: the publish workflow checks the match, packs the tool, pushes it to NuGet, and creates a GitHub release whose notes are the version's section of [CHANGELOG.md](CHANGELOG.md).
