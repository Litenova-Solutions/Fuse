using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Fuse.Evals;

/// <summary>One behavior mutation: which tests really fail, and which ones fuse test ran and reported.</summary>
internal sealed record SelectionCase(
    int Index,
    FileEdit Edit,
    List<string> TruthFailing,
    List<string> FuseFailing,
    int FuseFailed,
    int FusePassed,
    string FuseSummary,
    List<string> Missed,
    double FuseSeconds,
    double DotnetSeconds,
    string Verdict)
{
    /// <summary>How many tests the run selected, or null when it never planned.</summary>
    public int? TestsSelected { get; init; }

    /// <summary>How many tests the run ran, failed plus passed.</summary>
    public int TestsRun { get; init; }

    /// <summary>How many test projects the plan covered.</summary>
    public int ProjectsPlanned { get; init; }

    /// <summary>How many test projects ran: those with failures in the results file, or every planned one when the run passed.</summary>
    public int ProjectsRun { get; init; }

    /// <summary>How many test projects the solution holds.</summary>
    public int TotalProjects { get; init; }

    /// <summary>How many projects ran without MSBuild, from their own copies.</summary>
    public int ShadowRuns { get; init; }

    /// <summary>How many projects built with MSBuild.</summary>
    public int MsbuildRuns { get; init; }

    /// <summary>How many projects the selection ran whole.</summary>
    public int WholeProjectRuns { get; init; }

    /// <summary>Why projects ran whole, from the summary's parenthetical.</summary>
    public List<string> WholeProjectReasons { get; init; } = [];

    /// <summary>The filter collapse notes, verbatim from the engine log.</summary>
    public List<string> FilterCollapses { get; init; } = [];

    /// <summary>How many test processes the plan implies: one per shadow assembly, one per MSBuild run, and one per planned project with no mode line.</summary>
    public int TestProcesses { get; init; }

    /// <summary>How many failing tests the answer named.</summary>
    public int PrintedFailures { get; init; }

    /// <summary>How many tests failed, named or not.</summary>
    public int TotalFailures { get; init; }

    /// <summary>How many failures the results file holds, for a capped answer; null when the answer named every failure.</summary>
    public int? ResultsFileFailures { get; init; }

    /// <summary>Whether the edit changed compiled code; false when the run says the changes compile to the same code as HEAD.</summary>
    public bool? CodeChanged { get; init; }

    /// <summary>How many symbols the member walk visited, or null when the engine logged no selection line.</summary>
    public int? CandidatesBound { get; init; }
}

/// <summary>
///     One fix-and-rerun round with <c>fuse test --failed</c>: a full run after a fix, then a rerun of the failures it
///     recorded, and how many of the affected tests the rerun avoided.
/// </summary>
internal sealed record FailedRound(
    int Round,
    int FailedBefore,
    int? AffectedTests,
    int? RerunTests,
    int? TestsAvoided,
    double AffectedSeconds,
    double FuseSeconds,
    bool RanNothing);

/// <summary>One edit that changes no compiled code: what <c>fuse test</c> selected and ran for it.</summary>
internal sealed record NoCodeSelectionCase(
    string Kind,
    string File,
    bool? CodeChanged,
    int? TestsSelected,
    int TestsRun,
    int? CandidatesBound,
    int FuseFailed,
    double FuseSeconds,
    string Verdict);

/// <summary>
///     Applies behavior mutations to non-test code and checks that every test the full suite reports as
///     newly failing is also run and reported by <c>fuse test</c>.
/// </summary>
internal static partial class SelectionSuite
{
    /// <summary>
    ///     Measures <c>fuse test</c> on every case of <paramref name="truth"/>, which holds the plan and the full test run of
    ///     HEAD and of every case, and compares the tests it reports failing with the tests the full run saw failing.
    /// </summary>
    /// <param name="runFailedRounds">Whether to measure the <c>fuse test --failed</c> rounds on the fixture afterwards.</param>
    public static async Task<object> RunAsync(EvalRepo repo, SolutionInfo solution, TruthFile truth, bool runFailedRounds)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the suite needs a clean tree");
        if (!truth.IsComplete)
            throw new InvalidOperationException($"the truth for {truth.Key} is not complete");
        var head = truth.Head!;
        Console.WriteLine($"[selection] {repo.Name}: HEAD build...");
        var headBuild = await repo.BuildAsync();
        if (headBuild.ExitCode != 0)
            throw new InvalidOperationException($"HEAD does not build: {string.Join("; ", headBuild.Errors.Take(3))}");
        Console.WriteLine($"[selection] HEAD: {head.Total} tests, {head.Names.Count} failing ({head.Origin})");
        var baselineFailing = head.Names.ToHashSet(StringComparer.Ordinal);
        await repo.FuseAsync("check");

        var cases = new List<SelectionCase>();
        var skipped = new List<string>(truth.Skipped);
        var withoutResults = new List<string>();
        foreach (var planned in truth.Plan!)
        {
            var edit = planned.Edits[0];
            var item = truth.Cases[planned.Index];
            if (item.BuildFailed)
            {
                skipped.Add($"{edit.Kind} {edit.Path}: {edit.Description} (a test project does not compile)");
                continue;
            }

            await CasePlanner.ApplyAsync(repo, planned);
            // An agent runs tests some time after its edit; give the file watcher a moment, as that gap would.
            await Task.Delay(500);
            var caseStart = DateTime.UtcNow;
            var logStart = repo.EngineLogLines().Count;
            var watch = Stopwatch.StartNew();
            var fuse = await repo.FuseAsync("test");
            var fuseSeconds = watch.Elapsed.TotalSeconds;
            var logLines = repo.EngineLogLines().Skip(logStart).ToList();
            var plan = TestPlanCounts.Parse(fuse.Result.Output, logLines);
            await repo.ResetAsync();

            // The full run produces one result unit per test project and target framework, a TRX file or an executed
            // test assembly on Microsoft.Testing.Platform, as HEAD's did; fewer means a project wrote no results, and
            // its failures are missing from the truth side.
            if (item.ResultFiles < head.ResultFiles)
                withoutResults.Add($"case {planned.Index}: {item.ResultFiles} of {head.ResultFiles} result units");
            var truthFailing = item.Names.Where(n => !baselineFailing.Contains(n)).ToList();
            var fuseFailing = FailingNames(fuse.Result.Output);
            var counts = Counts().Match(fuse.Result.Output);
            var fuseFailed = counts.Success ? int.Parse(counts.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var fusePassed = counts.Success ? int.Parse(counts.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var lastLine = fuse.Result.Output.Trim().Split('\n').Last().Trim();
            // Every failing test `fuse test` ran is named in its output (details for ten, names for the rest). A truth
            // failure with no matching name is missed; if `fuse test` truncated its name list, the case cannot be verified.
            var missed = truthFailing.Where(t => !fuseFailing.Contains(t)).ToList();
            var truncated = fuse.Result.Output.Contains("... and ", StringComparison.Ordinal);
            var verdict = missed.Count > 0 ? (truncated ? "unverified" : "missed") : truthFailing.Count == 0 ? "no-failure" : "caught";
            // A capped answer proves its completeness with its results file: the file holds every failure the summary
            // counts. A missing file or a short count is a defect verdict, not a silent null.
            int? resultsFileFailures = null;
            if (ResultsFileCounts.CappedResults(fuse.Result.Output) is { } capped)
            {
                var cappedText = ResultsFileCounts.ReadText(capped.Path);
                resultsFileFailures = cappedText is null ? null : ResultsFileCounts.TestFailuresIn(cappedText);
                if (resultsFileFailures != capped.Total)
                    verdict = "results-incomplete";
            }

            // The results file is read only when this run wrote it: a run that wrote nothing leaves the previous run's file.
            var resultsPath = Path.Combine(repo.StateDirectory, "last-test-results.txt");
            int projectsRun;
            if (fuseFailed > 0 && ResultsFileCounts.IsFresh(resultsPath, caseStart) && ResultsFileCounts.ReadText(resultsPath) is { } resultsText)
                projectsRun = ResultsFileCounts.ProjectGroupsIn(resultsText);
            else if (fuse.Result.ExitCode == 0)
                projectsRun = plan.ProjectsPlanned;
            else
                projectsRun = 0;
            cases.Add(new SelectionCase(cases.Count, edit with { NewText = null }, truthFailing, fuseFailing, fuseFailed, fusePassed, lastLine, missed, fuseSeconds, item.Seconds, verdict)
            {
                TestsSelected = TestPlanCounts.SelectedTests(fuse.Result.Output),
                TestsRun = fuseFailed + fusePassed,
                ProjectsPlanned = plan.ProjectsPlanned,
                ProjectsRun = projectsRun,
                TotalProjects = solution.TestProjects.Count,
                ShadowRuns = plan.ShadowRuns,
                MsbuildRuns = plan.MsbuildRuns,
                WholeProjectRuns = plan.WholeProjectRuns,
                WholeProjectReasons = plan.WholeProjectReasons,
                FilterCollapses = plan.FilterCollapses,
                TestProcesses = plan.TestProcesses,
                PrintedFailures = fuseFailing.Count,
                TotalFailures = fuseFailed,
                ResultsFileFailures = resultsFileFailures,
                CodeChanged = fuse.Result.ExitCode is 0 or 1 or 8 ? !TestPlanCounts.CompilesToSameCode(fuse.Result.Output) : null,
                CandidatesBound = TestPlanCounts.CandidatesBound(logLines),
            });
            Console.WriteLine($"[selection] {cases.Count}/{truth.Plan.Count} {verdict,-10} truth-failing={truthFailing.Count} fuse ran={fuseFailed + fusePassed} fuse={fuseSeconds:0.0}s dotnet={item.Seconds:0.0}s  {edit.Kind} {edit.Path}");
        }

        var (noCodeCases, noCodeCasesSkipped) = await NoCodeCasesAsync(repo, baselineFailing);
        object? failedRounds = null;
        string? failedRoundsSkipped = null;
        if (!runFailedRounds)
            failedRoundsSkipped = "no-flag-passed: pass --failed-rounds to run the fuse test --failed rounds";
        else if (repo.Name != PinnedRepo.Fixture.Name)
            failedRoundsSkipped = "fixture-only: the failed-rerun rounds edit the generated fixture";
        else
            (failedRounds, failedRoundsSkipped) = await FailedRoundsAsync(repo);

        await repo.ResetAsync();
        var clean = await repo.IsCleanAsync();
        var noCodeSelected = cases.Where(c => c.CodeChanged == false).ToList();
        var noCodeExtra = noCodeCases?.Where(c => c.CodeChanged == false).ToList() ?? [];
        var summary = new
        {
            suite = "selection",
            repo = repo.Name,
            commit = await repo.HeadAsync(),
            fuseBuild = await repo.VersionAsync(),
            seed = truth.Seed,
            requested = truth.Count,
            cases = cases.Count,
            withFailures = cases.Count(c => c.TruthFailing.Count > 0),
            missedCases = cases.Count(c => c.Verdict == "missed"),
            unverifiedCases = cases.Count(c => c.Verdict == "unverified"),
            // A case whose fuse output was cut at the name limit cannot be matched name by name, so its unmatched truth
            // names are counted apart from misses the suite could verify.
            missedTests = cases.Where(c => c.Verdict == "missed").Sum(c => c.Missed.Count),
            unverifiedTests = cases.Where(c => c.Verdict == "unverified").Sum(c => c.Missed.Count),
            totalTests = head.Total,
            meanSelectedFraction = cases.Count == 0 || head.Total == 0 ? 0 : cases.Average(c => (double)(c.FuseFailed + c.FusePassed) / head.Total),
            meanRunFraction = cases.Count == 0 || head.Total == 0 ? 0 : cases.Average(c => (double)c.TestsRun / head.Total),
            testsSelected = cases.Sum(c => c.TestsSelected ?? 0),
            testsRun = cases.Sum(c => c.TestsRun),
            projectsPlanned = cases.Sum(c => c.ProjectsPlanned),
            projectsRun = cases.Sum(c => c.ProjectsRun),
            shadowRuns = cases.Sum(c => c.ShadowRuns),
            msbuildRuns = cases.Sum(c => c.MsbuildRuns),
            wholeProjectRuns = cases.Sum(c => c.WholeProjectRuns),
            testProcesses = cases.Sum(c => c.TestProcesses),
            resultsIncompleteCases = cases.Count(c => c.Verdict == "results-incomplete"),
            cappedCasesComplete = cases.Where(c => c.ResultsFileFailures is not null || c.Verdict == "results-incomplete").All(c => c.Verdict != "results-incomplete"),
            casesWithNoCodeChange = noCodeSelected.Count + noCodeExtra.Count,
            testsAvoidedByNoCodeChange = noCodeSelected.Sum(c => head.Total - c.TestsRun) + noCodeExtra.Sum(c => head.Total - c.TestsRun),
            noCodeCases,
            noCodeCasesSkipped,
            failedRounds,
            failedRoundsSkipped,
            fuseMedianSeconds = CorrectnessSuite.Median(cases.Select(c => c.FuseSeconds)),
            dotnetMedianSeconds = CorrectnessSuite.Median(cases.Select(c => c.DotnetSeconds)),
            fuseStats = CaseStats.Of(cases.Select(c => c.FuseSeconds)),
            dotnetStats = CaseStats.Of(cases.Select(c => c.DotnetSeconds)),
            skippedNonCompiling = skipped,
            projectsWithoutResults = withoutResults,
            truthKey = truth.Key,
            truthOrigins = truth.Origins(),
            treeCleanAfter = clean,
            details = cases,
        };
        Console.WriteLine($"[selection] {repo.Name}: {cases.Count} cases, {summary.withFailures} with failures, missed cases {summary.missedCases}, selected fraction {summary.meanSelectedFraction:P1}, fuse median {summary.fuseMedianSeconds:0.0} s vs dotnet test {summary.dotnetMedianSeconds:0.0} s, tree clean {clean}");
        return summary;
    }

    /// <summary>
    ///     Two edits that change no compiled code on the generated fixture: a comment and a local rename. Each is applied,
    ///     measured with <c>fuse test</c>, and reset. A case is <c>caught</c> when Fuse ran no test and no test newly fails,
    ///     <c>no-failure</c> when it ran tests and none newly fails, following the suite's verdicts.
    /// </summary>
    private static async Task<(List<NoCodeSelectionCase>? Cases, string? Skipped)> NoCodeCasesAsync(EvalRepo repo, HashSet<string> baselineFailing)
    {
        var money = Path.Combine(repo.Root, "src", "Core", "Money.cs");
        var geometry = Path.Combine(repo.Root, "src", "Core", "Geometry.cs");
        if (!File.Exists(money) || !File.Exists(geometry))
            return (null, "fixture-only: the no-code-change cases edit the generated fixture's Core project");
        var cases = new List<NoCodeSelectionCase>();
        try
        {
            cases.Add(await NoCodeCaseAsync(repo, baselineFailing, "comment", money, text => "// fuse evals: a comment changes no compiled code\n" + text));
            cases.Add(await NoCodeCaseAsync(repo, baselineFailing, "local-rename", geometry, text =>
                ReplaceOnce(ReplaceOnce(text, "var sides = 2;", "var sidesEvals = 2;", "the sides declaration"), "return sides *", "return sidesEvals *", "the sides use")));
        }
        finally
        {
            await repo.ResetAsync();
        }

        return (cases, null);
    }

    private static async Task<NoCodeSelectionCase> NoCodeCaseAsync(EvalRepo repo, HashSet<string> baselineFailing, string kind, string file, Func<string, string> edit)
    {
        await File.WriteAllTextAsync(file, edit(await File.ReadAllTextAsync(file)));
        await Task.Delay(500);
        var logStart = repo.EngineLogLines().Count;
        var run = await repo.FuseTimedAsync("test");
        var logLines = repo.EngineLogLines().Skip(logStart).ToList();
        var counts = TestPlanCounts.RunCounts(run.Output);
        var testsRun = counts.Failed + counts.Passed;
        var newlyFailing = FailingNames(run.Output).Where(n => !baselineFailing.Contains(n)).ToList();
        var verdict = newlyFailing.Count > 0 || testsRun == 0 ? "caught" : "no-failure";
        var c = new NoCodeSelectionCase(
            kind,
            Path.GetRelativePath(repo.Root, file).Replace('\\', '/'),
            run.ExitCode is 0 or 1 or 8 ? !TestPlanCounts.CompilesToSameCode(run.Output) : null,
            TestPlanCounts.SelectedTests(run.Output),
            testsRun,
            TestPlanCounts.CandidatesBound(logLines),
            counts.Failed,
            run.Milliseconds / 1000,
            verdict);
        Console.WriteLine($"[selection] no-code {kind}: selected={c.TestsSelected?.ToString() ?? "?"} run={testsRun} code-changed={c.CodeChanged?.ToString() ?? "?"} {verdict}");
        await repo.ResetAsync();
        return c;
    }

    /// <summary>
    ///     Three fix-and-rerun rounds with <c>fuse test --failed</c> on the generated fixture: three tests broken across two
    ///     projects, one fixed, the rest fixed, each fix followed by a full run and a rerun of what it recorded; then a rerun
    ///     with the results files deleted, which runs nothing. A build without the flag records null and a note.
    /// </summary>
    private static async Task<(object? Failed, string? Skipped)> FailedRoundsAsync(EvalRepo repo)
    {
        // With the flag, --failed and --all together are refused naming --failed; without it, --failed passes through and
        // the --all refusal never names it. The probe runs no test either way.
        var probe = await repo.FuseAsync("test", "--failed", "--all");
        if (!probe.Result.Output.Contains("--failed", StringComparison.Ordinal))
            return (null, "no-failed-flag: this build has no fuse test --failed, so no round ran");
        var geometry = Path.Combine(repo.Root, "src", "Core", "Geometry.cs");
        var textTools = Path.Combine(repo.Root, "src", "Core", "TextTools.cs");
        var reports = Path.Combine(repo.Root, "src", "Services", "ReportBuilder.cs");
        if (!File.Exists(geometry) || !File.Exists(textTools) || !File.Exists(reports))
            return (null, "fixture-only: the failed-rerun rounds edit the generated fixture");
        var rounds = new List<FailedRound>();
        try
        {
            await File.WriteAllTextAsync(geometry, ReplaceOnce(await File.ReadAllTextAsync(geometry), "return sides * (width + height);", "return sides + (width + height);", "the Perimeter computation"));
            await File.WriteAllTextAsync(textTools, ReplaceOnce(await File.ReadAllTextAsync(textTools), "text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;", "text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 1;", "the WordCount computation"));
            await File.WriteAllTextAsync(reports, ReplaceOnce(await File.ReadAllTextAsync(reports), "Slugify(name), 20)", "Slugify(name), 5)", "the Title width"));
            rounds.Add(await FailedRoundAsync(repo, 1));
            await File.WriteAllTextAsync(geometry, (await File.ReadAllTextAsync(geometry)).Replace("return sides + (width + height);", "return sides * (width + height);", StringComparison.Ordinal));
            rounds.Add(await FailedRoundAsync(repo, 2));
            await File.WriteAllTextAsync(textTools, (await File.ReadAllTextAsync(textTools)).Replace(").Length + 1;", ").Length;", StringComparison.Ordinal));
            await File.WriteAllTextAsync(reports, (await File.ReadAllTextAsync(reports)).Replace("Slugify(name), 5)", "Slugify(name), 20)", StringComparison.Ordinal));
            rounds.Add(await FailedRoundAsync(repo, 3));

            foreach (var name in new[] { "last-test-results.txt", "last-test-results.json" })
            {
                try
                {
                    File.Delete(Path.Combine(repo.StateDirectory, name));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            var refused = await repo.FuseTimedAsync("test", "--failed");
            var ranNothing = refused.ExitCode == 2 && refused.Output.Contains("no test run is recorded", StringComparison.Ordinal);
            rounds.Add(new FailedRound(4, 0, null, null, null, 0, refused.Milliseconds / 1000, ranNothing));
            Console.WriteLine($"[selection] failed round 4: results files deleted, ran nothing: {ranNothing}");
        }
        finally
        {
            await repo.ResetAsync();
        }

        var failed = new
        {
            rounds,
            totalTestsAvoided = rounds.Where(r => r.TestsAvoided is not null).Sum(r => r.TestsAvoided!.Value),
            totalRoundsAvoidedSeconds = rounds.Where(r => !r.RanNothing).Sum(r => r.AffectedSeconds - r.FuseSeconds),
        };
        return (failed, null);
    }

    /// <summary>One round: a full <c>fuse test</c> of the tree as it stands, then a rerun of the failures it recorded.</summary>
    private static async Task<FailedRound> FailedRoundAsync(EvalRepo repo, int round)
    {
        await Task.Delay(500);
        var full = await repo.FuseTimedAsync("test");
        var rerun = await repo.FuseTimedAsync("test", "--failed");
        var affected = TestPlanCounts.SelectedTests(full.Output);
        var failedBefore = TestPlanCounts.RunCounts(full.Output).Failed;
        var rerunCounts = TestPlanCounts.RunCounts(rerun.Output);
        int? rerunTests = rerun.ExitCode == 2 ? null
            : Reran().Match(rerun.Output) is { Success: true } reran ? int.Parse(reran.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : PreviousFailures().Match(rerun.Output) is { Success: true } previous ? int.Parse(previous.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : rerunCounts.Failed + rerunCounts.Passed;
        var avoided = affected is not null && rerunTests is not null ? affected - rerunTests : null;
        Console.WriteLine($"[selection] failed round {round}: {failedBefore} failed before, reran {rerunTests?.ToString() ?? "?"} of {affected?.ToString() ?? "?"} affected, avoided {avoided?.ToString() ?? "?"}");
        return new FailedRound(round, failedBefore, affected, rerunTests, avoided, full.Milliseconds / 1000, rerun.Milliseconds / 1000, false);
    }

    /// <summary>Replaces <paramref name="oldText"/> once; anything but exactly one occurrence is a scenario bug, not a measurement.</summary>
    private static string ReplaceOnce(string text, string oldText, string newText, string what)
    {
        var count = 0;
        for (var i = text.IndexOf(oldText, StringComparison.Ordinal); i >= 0; i = text.IndexOf(oldText, i + oldText.Length, StringComparison.Ordinal))
            count++;
        if (count != 1)
            throw new InvalidOperationException($"the failed-rerun scenario needs one {what}; found {count}");
        return text.Replace(oldText, newText, StringComparison.Ordinal);
    }

    /// <summary>Names of the failing tests in <c>fuse test</c> output: each <c>FAILED name</c> line and each name listed under <c>also failed</c>.</summary>
    private static List<string> FailingNames(string output)
    {
        var names = new List<string>();
        var inList = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("FAILED ", StringComparison.Ordinal))
            {
                names.Add(line["FAILED ".Length..].Trim());
                inList = false;
            }
            else if (line.StartsWith("also failed", StringComparison.Ordinal))
                inList = true;
            else if (inList && line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("  ... and ", StringComparison.Ordinal))
                names.Add(line.Trim());
            else if (inList)
                inList = false;
        }

        return names.Distinct().ToList();
    }

    [GeneratedRegex(@"fuse: (\d+) failed, (\d+) passed")]
    private static partial Regex Counts();

    [GeneratedRegex(@"reran (\d+) test\(s\) that failed last time", RegexOptions.CultureInvariant)]
    private static partial Regex Reran();

    [GeneratedRegex(@"ran (\d+) previous failure", RegexOptions.CultureInvariant)]
    private static partial Regex PreviousFailures();
}
