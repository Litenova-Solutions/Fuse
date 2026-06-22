# Packaging and publishing

How Fuse is distributed. Fuse is a developer tool for .NET developers, so the
canonical install is the .NET global tool. The other channels exist for people
who do not have the .NET SDK and want a single self-contained binary.

## Channels and how they publish

| Channel | Trigger | Workflow / source | Credentials |
|---------|---------|-------------------|-------------|
| NuGet (`dotnet tool install -g Fuse`) | `v*.*.*` tag | `.github/workflows/publish.yml` (`build/pack-aot.ps1`) | Trusted Publishing (OIDC) + `NUGET_USER` |
| GitHub Release (installer, self-contained win-x64 zip, linux-x64 tarball, `SHA256SUMS.txt`) | `v*.*.*` tag | `.github/workflows/release.yml` | built-in `GITHUB_TOKEN` |
| MCP Registry (`fuse serve` discovery) | `v*.*.*` tag | `.github/workflows/mcp-registry.yml` (`mcp-registry/server.json`) | GitHub OIDC, no secret |
| Install scripts (`curl ... \| sh`, `irm ... \| iex`) | served from the site | `site/public/install.sh`, `site/public/install.ps1` (at fuse.codes) | none |
| WinGet (`winget install Litenova.Fuse`) | manual PR | `packaging/winget/*` -> `microsoft/winget-pkgs` | none (PR review) |

The self-contained binaries are Native AOT builds, so they run without the .NET
SDK or runtime. The install scripts download those same release assets.

## Order of operations for a release

1. On nuget.org, create a Trusted Publishing policy (owner: the `Litenova-Solutions`
   org; repository owner `Litenova-Solutions`, repository `Fuse`, workflow file
   `publish.yml`). Add a `NUGET_USER` repo secret holding the nuget.org account
   name that owns the policy. No long-lived `NUGET_API_KEY` is needed.
2. Push a tag, for example `git tag v2.0.0 && git push origin v2.0.0`.
3. The three tag workflows run: NuGet push, the GitHub Release with assets, and
   the MCP Registry publish. The MCP publish validates the NuGet package and the
   `mcp-name: io.github.Litenova-Solutions/fuse` marker in the packed README, so
   it depends on the NuGet push having succeeded.
4. The install scripts need no per-release change: they resolve the latest release
   (or `FUSE_VERSION`) and verify against `SHA256SUMS.txt` at install time.
5. For WinGet, once the Release exists, set `PackageVersion` and `InstallerSha256`
   in `packaging/winget/*`, then submit the three manifests as a PR to
   `microsoft/winget-pkgs` (the `wingetcreate` tool can do both).

## Notes

- The placeholder `0000...` hash in the WinGet installer manifest is intentional:
  a manifest cannot carry a real hash until the release asset it points at exists.
- macOS is not packaged as a binary. Mac users get Fuse via
  `dotnet tool install -g Fuse`. Adding a macOS channel would need a macOS AOT
  build (the `aot-osx-x64` profile exists) and, for an unwarned binary, Apple
  notarization.
