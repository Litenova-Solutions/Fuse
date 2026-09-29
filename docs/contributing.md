# Contributing

This page is for anyone who wants to change Fuse: what to read first, the gates a change must pass, and what a pull request needs.

Read [AGENTS.md](../AGENTS.md) first: it covers the repository layout, the build, test and format commands, and the rules the code follows. [architecture.md](architecture.md) describes the layers, which namespace may use which, and the words the code and the output use; read it before adding a namespace, a type other namespaces use, or a word to the output. [How it works](how-it-works.md) explains what the code does.

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
4. If the change touches checking, test selection or the path of a request, run the eval [Evals](evals.md#what-to-run) names for it, for example `dotnet run --project evals/Fuse.Evals -c Release -- correctness fixture`, and attach its result to the pull request.
5. Sign off every commit with the Developer Certificate of Origin: `git commit -s` adds the `Signed-off-by:` line that [DCO.txt](../DCO.txt) describes. The DCO check fails a pull request with a commit whose sign-off does not match its author; `git rebase --signoff` adds the line to commits you already made.
6. Open a pull request that says what changed and why. The template lists the gates and the sign-off.

CI also packs the tool, installs the package, and checks that `fuse check` finds an introduced error in a fresh repository.

## Documentation

The documentation lives in `docs`, one page per question, and the root `README.md` is also the package's page on nuget.org, so its links are absolute. A change that alters output, a command or a limit updates the page that describes it in the same pull request. Every example in a page is one you ran, and every number quoted from a measurement follows the rules in [Evals](evals.md#quoting-the-numbers).

## Releases

The version lives in `Directory.Build.props`. A release is a `vX.Y.Z` tag that matches it: the publish workflow checks the match, packs the tool, pushes it to NuGet, and creates a GitHub release whose notes are the version's section of [changelog.md](changelog.md).
