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

- [docs/project/contributing.md](docs/project/contributing.md): the full contribution workflow and project layout.
- [AGENTS.md](AGENTS.md): the documentation and code-comment standard.
- [docs/architecture/index.md](docs/architecture/index.md): how the pipeline fits together.
- [docs/extending/language-plugin.md](docs/extending/language-plugin.md): adding a new language.
- [ROADMAP.md](ROADMAP.md): planned direction, if you want to pick up a larger item.

## Pull requests

Keep a change scoped to one part of the pipeline. New public API needs XML docs. Run the three commands above before opening a pull request, and describe what changed and why in the description.
