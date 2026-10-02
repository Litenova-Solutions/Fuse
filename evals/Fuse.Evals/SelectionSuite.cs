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
    public static async Task<object> RunAsync(EvalRepo repo, TruthFile truth)
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
            var watch = Stopwatch.StartNew();
            var fuse = await repo.FuseAsync("test");
            var fuseSeconds = watch.Elapsed.TotalSeconds;
            await repo.ResetAsync();

            // The full run writes one TRX file per test project and target framework, as HEAD's did; fewer means a project
            // wrote no results, and its failures are missing from the truth side.
            if (item.ResultFiles < head.ResultFiles)
                withoutResults.Add($"case {planned.Index}: {item.ResultFiles} of {head.ResultFiles} result files");
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
            cases.Add(new SelectionCase(cases.Count, edit with { NewText = null }, truthFailing, fuseFailing, fuseFailed, fusePassed, lastLine, missed, fuseSeconds, item.Seconds, verdict));
            Console.WriteLine($"[selection] {cases.Count}/{truth.Plan.Count} {verdict,-10} truth-failing={truthFailing.Count} fuse ran={fuseFailed + fusePassed} fuse={fuseSeconds:0.0}s dotnet={item.Seconds:0.0}s  {edit.Kind} {edit.Path}");
        }

        await repo.ResetAsync();
        var clean = await repo.IsCleanAsync();
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
}
