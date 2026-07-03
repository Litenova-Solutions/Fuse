# Contributing to Fuse

Thanks for your interest in improving Fuse. This page covers the local loop and points to the deeper guides.

## Local loop

Fuse is a .NET 10 solution defined in `Fuse.slnx`. Three commands cover the local workflow, and CI verifies all three on every pull request:

```bash
dotnet build Fuse.slnx -c Release
dotnet test Fuse.slnx -c Release --no-build
dotnet format Fuse.slnx --verify-no-changes
```

Build first, then test with `--no-build` so the test run reuses the build output. To apply formatting fixes instead of verifying, run `dotnet format Fuse.slnx` without the flag. On Windows, `install.bat` builds, packs, and installs the global tool locally for manual testing.

The build enforces public XML documentation: a missing `public` or `protected` XML comment produces warning CS1591. Do not add new CS1591 warnings.

## Project layout

The solution separates concerns along the pipeline. A change should touch one part and respect the boundaries between them.

- Core: the pipeline libraries (`Fuse.Collection`, `Fuse.Reduction`, `Fuse.Emission`, `Fuse.Fusion`).
- Host: the user-facing surfaces (`Fuse.Cli` commands and the MCP server).
- Plugins: language and format behavior registered into the core pipeline (`Fuse.Plugins.Languages.CSharp`, `Fuse.Plugins.Formats.Web`).

The core libraries do not depend on the host, and language-specific behavior lives in plugins rather than in the core pipeline.

## Where to read more

- [fuse.codes/docs/project/contributing](https://fuse.codes/docs/project/contributing): the full contribution workflow and project layout.
- [AGENTS.md](AGENTS.md): the documentation and code-comment standard.
- [fuse.codes/docs/internals/pipeline](https://fuse.codes/docs/internals/pipeline): how the pipeline fits together.
- [fuse.codes/docs/internals/extending/language-plugin](https://fuse.codes/docs/internals/extending/language-plugin): adding a new language.
- [ROADMAP.md](ROADMAP.md): planned direction, if you want to pick up a larger item.

## Pull requests

Keep a change scoped to one part of the pipeline. New public API needs XML docs. Run the three commands above before opening a pull request, and describe what changed and why in the description.

## License and sign-off

Fuse is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) for the full text and [NOTICE](NOTICE) for attribution. Contributions are accepted under the same license. The project migrated from MIT to Apache 2.0 in the 4.0 release.

Every commit must carry a Developer Certificate of Origin (DCO) sign-off. Add it with `git commit -s`, which appends a `Signed-off-by: Your Name <you@example.com>` trailer whose name and email match the commit author. The sign-off certifies the DCO 1.1 statement in [DCO.txt](DCO.txt): you attest you have the right to submit the change under the project license. A DCO check runs on every pull request and fails any commit missing a matching trailer; commits merged before adoption are grandfathered. Fuse uses the DCO in place of a Contributor License Agreement.
