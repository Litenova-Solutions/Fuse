# Packaging and publishing

How Fuse is distributed, and what each file here is for. The canonical install
is the .NET global tool; the other channels serve users without the .NET SDK.

## Channels and how they publish

| Channel | Trigger | Workflow / source | Credentials |
|---------|---------|-------------------|-------------|
| NuGet (`dotnet tool install -g Fuse`) | `v*.*.*` tag | `.github/workflows/publish.yml` (`build/pack-aot.ps1`) | `NUGET_API_KEY` repo secret |
| GitHub Release (installer, win-x64 zip, linux-x64 tarball, `SHA256SUMS.txt`) | `v*.*.*` tag | `.github/workflows/release.yml` | built-in `GITHUB_TOKEN` |
| MCP Registry (`fuse serve` discovery) | `v*.*.*` tag | `.github/workflows/mcp-registry.yml` (`mcp-registry/server.json`) | GitHub OIDC, no secret |
| Scoop (`scoop install fuse`) | manual / autoupdate | `packaging/scoop/fuse.json` -> the `scoop-bucket` repo | none |
| Homebrew (`brew install fuse`, Linux) | manual / bump | `packaging/homebrew/fuse.rb` -> the `homebrew-fuse` tap | none |
| WinGet (`winget install Litenova.Fuse`) | manual PR | `packaging/winget/*` -> `microsoft/winget-pkgs` | none (PR review) |

## Order of operations for a release

1. Set `NUGET_API_KEY` once in repo secrets (Settings, Secrets and variables, Actions).
2. Push a tag, for example `git tag v2.0.0 && git push origin v2.0.0`.
3. The three tag workflows run: NuGet push, the GitHub Release with assets, and
   the MCP Registry publish. The MCP publish validates the NuGet package and the
   `mcp-name: io.github.litenova-solutions/fuse` marker in the packed README, so
   it depends on the NuGet push having succeeded.
4. Once the Release exists, update the downstream manifests with the real version
   and hashes (the placeholder zeros below are filled from the release assets):
   - **Scoop:** in the `scoop-bucket` repo, run the bundled checkver/autoupdate to
     bump `fuse.json` (it reads hashes from `SHA256SUMS.txt`), or copy the updated
     `packaging/scoop/fuse.json` over.
   - **Homebrew:** set `version` and the `sha256` (from `SHA256SUMS.txt`) in the
     tap's `fuse.rb`.
   - **WinGet:** set `PackageVersion` and `InstallerSha256`, then submit the three
     manifests as a PR to `microsoft/winget-pkgs` (the `wingetcreate` tool can do
     both).

## Notes

- The placeholder `0000...` hashes are intentional: a manifest cannot carry a real
  hash until the release asset it points at exists.
- macOS is not packaged yet. Mac users with the .NET SDK still get Fuse via
  `dotnet tool install -g Fuse`. Adding a macOS channel needs a macOS AOT build
  (the `aot-osx-x64` profile exists) and, for an unwarned binary, Apple notarization.
- The external `scoop-bucket` and `homebrew-fuse` repos hold the published copies
  of the manifests in `packaging/scoop` and `packaging/homebrew`; this folder is
  the source of truth that they track.
