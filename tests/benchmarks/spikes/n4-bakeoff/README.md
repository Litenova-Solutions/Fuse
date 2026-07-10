# N4 build-capture bake-off spike

De-risking measurement that decides N4's mechanism (v4 plan, item zero): does the current
MSBuildWorkspace loader or a build-capture ladder reach oracle-grade (tier-1) load more often on
real checkouts?

## What it measures

For each repo, `n4-bakeoff.ps1` records:

- **(a) MSBuildWorkspace**: the index mode the current loader achieves, via `fuse index --force`
  (`semantic` | `partial` | `syntax`).
- **(b) build-capture ladder**: tier-1 (oracle-grade) is achievable if and only if the repo's own
  `dotnet build -c Release -bl` succeeds. A successful C# build emits Csc invocations in the binary
  log that `CSharpCommandLineParser` rehydrates into exact compilations, so mechanism (b)'s tier-1
  rate equals the plain build-success rate. On build failure, (b) falls back to the same salvage as
  (a). The first error code is recorded as the doctor-style downgrade reason.

## Reproduce

```pwsh
# Corpus repos (expects tests/benchmarks/.corpus populated):
./n4-bakeoff.ps1 -RepoListJson ./repos-corpus.json -OutFile ./corpus-out.json
# Popular OSS repos (shallow-cloned at HEAD):
./n4-bakeoff.ps1 -RepoListJson ./repos-oss.json -OutFile ./oss-out.json
```

Requires the freshly built CLI at `src/Host/Fuse.Cli/bin/Release/net10.0/fuse.dll`.

## Result

Recorded in `tests/benchmarks/results/n4-bakeoff.json`. Headline (2026-07-03, 17 evaluable repos,
3 clone failures excluded as environmental): MSBuildWorkspace reached semantic on 2/17 (12%);
build-capture reached tier-1 on 11/11 buildable repos (65% of all evaluable repos build). The
mechanism decision is the build-capture ladder; the 65% build-success rate is the published oracle
coverage ceiling in this environment.
