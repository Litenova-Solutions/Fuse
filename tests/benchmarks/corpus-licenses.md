# Corpus v2 license posture (B3 precondition)

B3 publishes a public, reproducible benchmark: the manifests, the extraction method, the harness, and the health
gate. It publishes **manifests and scripts, never repo code**. A manifest entry is a repository name, its clone
URL, a pinned commit, and task metadata (titles, base/merge commits, test filters, changed-test file paths). A
reproducer clones each repository itself, under that repository's own license, from its own origin. Fuse
redistributes none of the corpus repositories' source, so the corpus licenses do not gate manifest publication.

This file records the license of each corpus-v2 repository as re-checked on 2026-07-11 from the copy provisioned
under `D:/fuse-work/bench`, so the published manifest can carry an accurate per-repo license column and the one
non-OSI caveat below is surfaced to reproducers rather than discovered by surprise.

## Per-repository licenses (24 repos)

| Repository | License |
|------------|---------|
| Ardalis.GuardClauses | MIT |
| Ardalis.Specification | MIT |
| AutoFixture | MIT |
| Bogus | MIT |
| CommandLineParser | MIT |
| Flurl | MIT |
| Humanizer | MIT |
| Nancy | MIT |
| Newtonsoft.Json | MIT |
| Scrutor | MIT |
| StackExchange.Redis | MIT |
| YamlDotNet | MIT |
| Dapper | Apache 2.0 |
| FluentValidation | Apache 2.0 |
| NodaTime | Apache 2.0 |
| RestSharp | Apache 2.0 |
| quartznet | Apache 2.0 |
| serilog | Apache 2.0 |
| Polly | BSD 3-Clause |
| Shouldly | BSD 3-Clause |
| NSubstitute | BSD 3-Clause |
| CsvHelper | MS-PL |
| AutoMapper | Lucky Penny Software (source-available, not OSI) |
| MediatR | Lucky Penny Software (source-available, not OSI) |

Summary: 12 MIT, 6 Apache 2.0, 3 BSD 3-Clause, 1 MS-PL, 2 source-available (Lucky Penny Software).

## The one caveat for reproducers

AutoMapper and MediatR relicensed to the Lucky Penny Software source-available terms (a commercial/source-available
license, not an OSI-approved open-source license). This does not affect publishing a manifest that references them
by URL and commit, but a reproducer who clones them to run the corpus is bound by those terms. The published
manifest and the reproduction README must state this so a user accepts (or excludes) those two repositories
knowingly. Every other corpus repository is under a permissive OSI license (MIT, Apache 2.0, BSD 3-Clause, or
MS-PL), all of which permit an unmodified clone-and-build for benchmarking.

## What the manifest may carry

- Repository name, clone URL, and pinned commit (a reference, not the code).
- Task metadata mined from public history: commit titles, base and merge commit SHAs, `dotnet test --filter`
  expressions, and changed-test file paths.
- No source file contents from any corpus repository.
