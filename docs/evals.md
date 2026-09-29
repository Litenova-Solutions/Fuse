# Evals

This page explains how the evals in `evals/Fuse.Evals` measure Fuse and how to run them, for a contributor whose change touches checking, test selection or performance and has to show its effect. [Results](results.md) has the published numbers.

`evals/Fuse.Evals` is a console project with three suites: correctness, test selection and latency. Every measurement goes through the `fuse` executable, the way an agent calls it, and every answer is compared with the truth from a real `dotnet build` or `dotnet test`. Each run of a suite writes one JSON file to `evals/results`, and the published numbers, the chart and the site quote the newest file of each suite and repository.

## Run an eval

Build Fuse first; the suites run `src/Fuse/bin/Release/net10.0/fuse` (or `fuse.exe`), and the Debug build when there is no Release build.

```bash
dotnet build Fuse.slnx -c Release
dotnet run --project evals/Fuse.Evals -c Release -- correctness fixture --mutations 2
```

```text
repo <fuse>\evals\.work\fixture, solution Fixture.sln: 3 code project(s), 2 test project(s); fuse <fuse>\src\Fuse\bin\Release\net10.0\fuse.exe
[correctness] fixture: HEAD build...
[correctness] HEAD build exit 0, 0 error(s), 1.1 s
[correctness] fuse warm-up: fuse: no errors introduced (0 file(s) checked) (1158 ms)
[correctness] 1/2 agree-clean  truth=  0 fuse=  0   5184 ms  change-return-type src/Services/ReportBuilder.cs
[correctness] 2/2 agree        truth=  2 fuse=  4    505 ms  remove-member src/Services/OrderService.cs
[correctness] fixture: 2 cases, 1 breaking, false green 0, false-red cases 0, unverifiable diagnostics 2, deferred by csc 0, message mismatches 0, partial 0, exact 2, file agreement 2, tree clean True
wrote evals\results\correctness-fixture-20260929-0452.json
```

`<fuse>` stands for the path of the Fuse checkout, shortened here. In the second case the build reported 2 errors in `Services` and Fuse reported 4: the build's 2, and 2 in projects that reference `Services`. MSBuild skipped those projects because `Services` failed, so the suite counted Fuse's errors there as unverifiable rather than contradicted.

A run with fewer cases than the default replaces the newest result for its suite and repository, so delete its file rather than commit it. Commit a result file only from a full run.

## Commands

```text
$ dotnet run --project evals/Fuse.Evals -c Release
usage: Fuse.Evals <suite> <repo> [--mutations N] [--seed S] [--solution path] [--fuse path]
       Fuse.Evals chart
       Fuse.Evals clone <repo>
       Fuse.Evals clean
  suite: correctness | selection | latency | all
  repo:  fixture (generated under evals/.work/fixture), a pinned repository name, or a path to a git repository
  clone: NodaTime | Jellyfin | CommunityToolkit, checked out at the pinned commit under
         %LOCALAPPDATA%/fuse/evals/repos (outside this repository, so its build settings do not leak in)
  clean: removes every checkout and the generated fixture; the result files in evals/results stay
```

- **`<suite> <repo>`** runs `correctness`, `selection`, `latency`, or `all` three in that order, on one repository. `<repo>` is `fixture`, the name of a pinned repository that has been cloned, or a path to any git repository.
    - `--mutations N`: the number of cases, 30 by default for correctness and 10 for selection. The latency suite has a fixed size.
    - `--seed S`: the seed of the random choice of edits, 1 by default, so a run with the same seed on the same commit makes the same edits.
    - `--solution path`: the solution the truth side builds, relative to the repository. The default is the pinned solution, or the only `.sln` or `.slnx` file in the repository root.
    - `--fuse path`: the `fuse` executable to measure.
- **`clone <repo>`** checks `NodaTime`, `Jellyfin` or `CommunityToolkit` out at its pinned commit in `fuse/evals/repos/<repo>` under the user's local application data (`%LOCALAPPDATA%` on Windows). A checkout already at that commit is left alone. The checkouts live outside the Fuse repository because a repository without its own `Directory.Packages.props` would inherit Fuse's and fail to restore.
- **`chart`** renders `site/benefits.svg` from the newest correctness and selection file of each repository: one panel for `fuse check` against `dotnet build` and one for `fuse test` against `dotnet test`, with every time and speedup rounded as the results are.
- **`clean`** deletes `fuse/evals` under the local application data, which holds the checkouts. The result files stay. The generated fixture under `evals/.work/fixture` stays too, although the usage text says otherwise; delete that folder by hand to regenerate it.

Before a suite runs, the tool restores the solution and every project under the repository that the solution leaves out, because Fuse reads every project in the working tree and does not answer while one is unrestored. Every suite needs a clean working tree, fails otherwise, and discards each change it makes with `git checkout -- .` and `git clean -fdq`, so run the suites only on a checkout you do not work in.

## Repositories

| Name | Source | Commit | Solution |
| --- | --- | --- | --- |
| `fixture` | generated | differs per generation | `Fixture.sln` |
| `NodaTime` | `https://github.com/nodatime/NodaTime` | `fcd80e11216ba403ccce0abbcedc41ba37bb352e` | `src/NodaTime.slnx` |
| `Jellyfin` | `https://github.com/jellyfin/Jellyfin` | `1d7c6af520da5c84ceac1c21a1d2da34837540ac` | `Jellyfin.sln` |
| `CommunityToolkit` | `https://github.com/CommunityToolkit/dotnet` | `b135626dd54d33b8f05f2ff31591592c004aa848` | `dotnet.slnx` |

`evals/Fuse.Evals/PinnedRepo.cs` holds the pins and the labels the chart draws. Jellyfin is pinned to its final commit before it moved to Roslyn 5, because from then on its analyzer project needs a newer compiler than the SDK the evals run on. The Community Toolkit is measured because its test projects reach separate parts of the code, which lets test selection narrow.

The fixture is generated in `evals/.work/fixture` on its first use and reused afterwards: two libraries (`Core`, and `Services`, which references it), a console app (`App`, which references `Services`) and two xUnit test projects (`Core.Tests` and `Services.Tests`), committed to git and built. It writes its own `global.json`, `Directory.Build.props`, `Directory.Build.targets` and `Directory.Packages.props`, so Fuse's own build settings do not apply to it. Its commit id changes each time it is generated, while its content stays the same.

## Suites

### Correctness

The correctness suite measures whether `fuse check` reports the errors a real build reports.

1. It builds the solution at HEAD and keeps the errors HEAD already has.
2. For each case, it makes one generated edit, or with a probability of 0.3, two or three edits in different projects: removing, renaming or re-parameterizing a method, making it private, duplicating its signature, changing its return type, removing a `using` directive, or deleting a file.
3. It runs `fuse check` on the edited files, then `dotnet build` on the solution, and takes the build's errors beyond HEAD's as the truth.
4. It matches Fuse's errors with the truth by position (file, line and column), then resets the tree.

A Fuse error at a truth position with a different id or message is a message mismatch. A Fuse error with no truth at its position is unverifiable when it is in a project MSBuild skipped because a project it references failed, deferred when the compiler skipped it (see [Results](results.md#reading-the-numbers)), and contradicted otherwise. A truth error without a Fuse error at its position is missed. Each case gets a verdict: `agree`, `agree-clean` (no error on either side), `partial` (some errors missed), `false-red` (an error contradicted), `false-green` (the build reported introduced errors and Fuse reported none) or `fuse-failed` (Fuse did not answer).

### Test selection

The selection suite measures whether `fuse test` runs every test a change makes fail.

1. It builds the solution at HEAD, runs every test project with `dotnet test`, and stops when a test project writes no results, because a missing project would hide misses.
2. For each case, it makes one behavior edit in a method body of a code project: flipping a comparison, shifting an integer constant by one, dropping a statement, or negating a boolean return. An edit that does not compile is skipped.
3. It waits 500 ms, runs `fuse test`, then runs every test project with `dotnet test` again. The truth is the tests that fail after the edit and did not fail at HEAD.

A case is `caught` when every truth failure is among the failures `fuse test` printed, `missed` when one is not, `no-failure` when the edit failed no test, and `unverified` when a truth failure is not printed because `fuse test` cut its list of names (`... and N more`).

### Latency

The latency suite times `fuse check` and `fuse test` the way an agent waits for them, and reads the engine's phase line for each call from `engine.log`.

1. It picks a public method with a block body in one of the two code projects with the most dependents, the one whose name the other projects mention most.
2. It times a check of the clean tree with a cold engine, and a check of a body edit with a cold engine, which loads the owning project.
3. It times 15 body edits (a statement added to the method) and 10 signature edits (the method renamed), each followed by `fuse check` on the file, then 10 checks of the unchanged tree and 5 `fuse test` rounds after body edits.
4. It reads the engine's working set, then runs the multi-agent scenario.

### Multi-agent scenario

The latency suite ends with three scenarios for several agents in one repository:

- **Writers.** Up to four clients each edit a file in a different code project ten times and check it, all at once, while a fifth client checks and runs the tests three times. The engine's `gate` phase shows whether requests queue behind each other.
- **Builds.** Three clients run `fuse build` at once, five times. A collision is an output line with MSB3021, MSB3026, MSB3027 or CS2012, and the pass bar is zero.
- **Rounds.** Three `fuse test` rounds: a body edit in one project, a body edit in a second project whose tests differ, and a second edit to the same file. The result records which test projects each round planned.

## Result files

A suite writes `evals/results/<suite>-<repo>-<yyyyMMdd-HHmm>.json`, with the local time of the run. The repository keeps only the newest file of each suite and repository; older files stay in git history. Every file records:

- `suite`, `repo`: what ran on which repository.
- `commit`: the commit the repository was measured at.
- `fuseBuild`: the build of `fuse` that was measured, as `<version>/<module id>`, the same id the engine compares.
- `treeCleanAfter`: whether the suite left the working tree clean.

**Correctness files** also hold `seed`, `requested` and `cases`; `breaking` and `neutral` (cases whose build failed or passed); `falseGreen`, `falseRedCases`, `falseRedDiagnostics` (contradicted errors), `partialMisses`, `exactAgreement` and `fileAgreement` (cases whose errors are in the same files); `unverifiableDiagnostics`, `deferredByCompilerDiagnostics` and `messageMismatchDiagnostics`; `contextLines`, `contextBytes` and `outputBytes` (cause lines, their size and the whole output's size); `fuseFailures`; `fuseMedianMs` and `buildMedianSeconds`; `headBuildErrors`; and `details`, one object per case with its edits, the truth and Fuse errors, each classification list, its verdict, and its times.

**Selection files** also hold `seed`, `requested`, `cases`, `withFailures`, `missedCases`, `unverifiedCases`, `missedTests` and `unverifiedTests`; `totalTests` (the tests in a full run at HEAD); `meanSelectedFraction` (the mean share of those tests `fuse test` ran); `fuseMedianSeconds` and `dotnetMedianSeconds`; `skippedNonCompiling` and `projectsWithoutResults`; and `details`, one object per case with its edit, the truth and Fuse failures, Fuse's counts and summary line (`fuseScope`), the misses, both times and its verdict.

**Latency files** also hold the `target` file and `method` and `referencingFilesInOtherProjects`; `coldCleanMs` and `coldBodyEditMs`; `bodyEdit`, `signatureEdit`, `warmUnchanged` and `testRound`, each `p50`, `p95`, `max` and `n` in milliseconds; `signatureEditSample`, the summary line of the first signature edit; `bodyEditPhases`, `signatureEditPhases`, `warmUnchangedPhases` and `testRoundPhases`, the same statistics per engine phase plus `outsideEngine`, the time the client spent outside the engine's request; `multiAgent`, with `writers`, `builds` and `rounds`; and `engineWorkingSetMb`.

Medians are over the cases, and the average of the two middle values for an even count. Percentiles use the nearest-rank method: P95 of 10 or 15 samples is the slowest one.

## What to run

| Change | Run |
| --- | --- |
| The check: `Fuse.Check`, `Fuse.Changes`, `Fuse.Workspace`, analyzer selection | `correctness` on the fixture and on at least one pinned repository |
| Test selection or test runs: `Fuse.Testing`, `TestOperation` | `selection` on the fixture and on at least one pinned repository |
| The engine, the protocol, the client, the build lock, or anything on a request's path | `latency` |

Attach the result to the pull request, as [Contributing](contributing.md) asks. The published set in [Results](results.md) is `all` on the fixture and on each pinned repository: twelve files.

## Quoting the numbers

The rule in [AGENTS.md](../AGENTS.md) binds every page that quotes a measurement: numbers come from files in `evals/results`, and each results table names the files it comes from. Counts are quoted exactly. Times, sizes and percentages are rounded half up: seconds to two decimals below 10 s and one decimal from 10 s, milliseconds and megabytes to whole numbers, percentages to at most one decimal. A speedup is the `dotnet` median divided by the Fuse median, to one decimal.

After committing the result files of a full run, delete the older file of the same suite and repository, run `chart`, and update every page that quotes the numbers: [Results](results.md), the root `README.md`, `site/index.html`, `site/results.html`, the questions in `site/how-it-works.html`, and `site/og.png`, whose text quotes the check speedup.
