using System.Diagnostics;
using System.Text.RegularExpressions;
using Fuse.Dotnet;

namespace Fuse.Evals;

/// <summary>One behavior mutation: which tests really fail, and which ones fuse test ran and reported.</summary>
internal sealed record SelectionCase(
    int Index,
    FileEdit Edit,
    List<string> TruthFailing,
    List<string> FuseFailing,
    int FuseFailed,
    int FusePassed,
    string FuseScope,
    List<string> Missed,
    double FuseSeconds,
    double DotnetSeconds,
    string Verdict);

/// <summary>
///     Suite 7.2: applies behavior mutations to non-test code and checks that every test the full suite reports as
///     newly failing is also run and reported by <c>fuse test</c>.
/// </summary>
internal static partial class SelectionSuite
{
    public static async Task<object> RunAsync(EvalRepo repo, SolutionInfo solution, int count, int seed)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the suite needs a clean tree");
        Console.WriteLine($"[selection] {repo.Name}: HEAD build and full test run...");
        var headBuild = await repo.BuildAsync();
        if (headBuild.ExitCode != 0)
            throw new InvalidOperationException($"HEAD does not build: {string.Join("; ", headBuild.Errors.Take(3))}");
        var head = await FullTestAsync(repo, solution);
        Console.WriteLine($"[selection] HEAD: {head.Outcome.Total} tests, {head.Outcome.Failed} failing, {head.Seconds:0.0} s");
        var baselineFailing = head.Outcome.Failures.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        await repo.FuseAsync("check");

        var sources = solution.CodeSources();
        var random = new Random(seed);
        var cases = new List<SelectionCase>();
        var skipped = new List<string>();
        for (var attempt = 0; cases.Count < count && attempt < count * 8; attempt++)
        {
            var file = sources[random.Next(sources.Count)];
            var kind = BehaviorMutator.Kinds[random.Next(BehaviorMutator.Kinds.Length)];
            var edit = BehaviorMutator.Mutate(file, Path.GetRelativePath(repo.Root, file).Replace('\\', '/'), kind, random);
            if (edit is null)
                continue;
            await File.WriteAllTextAsync(file, edit.NewText);

            // An agent runs tests some time after its edit; give the file watcher a moment, as that gap would.
            await Task.Delay(500);
            var watch = Stopwatch.StartNew();
            var fuse = await repo.FuseAsync("test");
            var fuseSeconds = watch.Elapsed.TotalSeconds;
            if (fuse.Result.Output.Contains("test build failed", StringComparison.Ordinal))
            {
                skipped.Add($"{edit.Kind} {edit.Path}: {edit.Description} (does not compile)");
                await repo.ResetAsync();
                continue;
            }

            var truth = await FullTestAsync(repo, solution);
            if (truth.BuildFailed)
            {
                skipped.Add($"{edit.Kind} {edit.Path}: {edit.Description} (does not compile)");
                await repo.ResetAsync();
                continue;
            }

            var truthFailing = truth.Outcome.Failures.Select(f => f.Name).Where(n => !baselineFailing.Contains(n)).Distinct().ToList();
            var fuseFailing = FailedLine().Matches(fuse.Result.Output).Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();
            var counts = Counts().Match(fuse.Result.Output);
            var fuseFailed = counts.Success ? int.Parse(counts.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var fusePassed = counts.Success ? int.Parse(counts.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var scope = fuse.Result.Output.Trim().Split('\n').Last().Trim();
            // fuse prints at most 10 failures; names beyond that are only missing when its failed count is lower.
            var missed = truthFailing.Where(t => !fuseFailing.Contains(t)).ToList();
            if (fuseFailing.Count >= 10 && fuseFailed >= truthFailing.Count)
                missed.Clear();
            var verdict = missed.Count > 0 ? "missed" : truthFailing.Count == 0 ? "no-failure" : "caught";
            cases.Add(new SelectionCase(cases.Count, edit with { NewText = null }, truthFailing, fuseFailing, fuseFailed, fusePassed, scope, missed, fuseSeconds, truth.Seconds, verdict));
            Console.WriteLine($"[selection] {cases.Count}/{count} {verdict,-10} truth-failing={truthFailing.Count} fuse ran={fuseFailed + fusePassed} fuse={fuseSeconds:0.0}s dotnet={truth.Seconds:0.0}s  {edit.Kind} {edit.Path}");
            await repo.ResetAsync();
        }

        await repo.ResetAsync();
        var clean = await repo.IsCleanAsync();
        var summary = new
        {
            suite = "selection",
            repo = repo.Name,
            seed,
            requested = count,
            cases = cases.Count,
            withFailures = cases.Count(c => c.TruthFailing.Count > 0),
            missedCases = cases.Count(c => c.Missed.Count > 0),
            missedTests = cases.Sum(c => c.Missed.Count),
            totalTests = head.Outcome.Total,
            meanSelectedFraction = cases.Count == 0 || head.Outcome.Total == 0 ? 0 : cases.Average(c => (double)(c.FuseFailed + c.FusePassed) / head.Outcome.Total),
            fuseMedianSeconds = CorrectnessSuite.Median(cases.Select(c => c.FuseSeconds)),
            dotnetMedianSeconds = CorrectnessSuite.Median(cases.Select(c => c.DotnetSeconds)),
            skippedNonCompiling = skipped,
            treeCleanAfter = clean,
            details = cases,
        };
        Console.WriteLine($"[selection] {repo.Name}: {cases.Count} cases, {summary.withFailures} with failures, missed cases {summary.missedCases}, selected fraction {summary.meanSelectedFraction:P1}, fuse median {summary.fuseMedianSeconds:0.0} s vs dotnet test {summary.dotnetMedianSeconds:0.0} s, tree clean {clean}");
        return summary;
    }

    /// <summary>Runs every test project in the solution with dotnet test and reads the TRX results.</summary>
    private static async Task<(TestOutcome Outcome, double Seconds, bool BuildFailed)> FullTestAsync(EvalRepo repo, SolutionInfo solution)
    {
        var watch = Stopwatch.StartNew();
        var total = TestOutcome.Empty;
        var buildFailed = false;
        foreach (var project in solution.TestProjects)
        {
            var results = Path.Combine(repo.Root, "obj", "fuse-evals", Guid.NewGuid().ToString("N")[..8]);
            var run = await ProcessRunner.RunAsync("dotnet", ["test", project, "--no-restore", "--logger", "trx;LogFilePrefix=truth", "--results-directory", results, "-nologo", "-tl:off"], repo.Root, CancellationToken.None);
            var outcome = TrxReader.ReadDirectory(results, repo.Root);
            if (outcome is null)
            {
                if (run.ExitCode != 0 && BuildOutputParser.Errors(run.Output, repo.Root).Count > 0)
                    buildFailed = true;
            }
            else
            {
                total = total.Add(outcome);
            }

            try
            {
                Directory.Delete(results, recursive: true);
            }
            catch (IOException)
            {
                // DirectoryNotFoundException included: no results were written.
            }
        }

        return (total, watch.Elapsed.TotalSeconds, buildFailed);
    }

    [GeneratedRegex(@"^FAILED (.+)$", RegexOptions.Multiline)]
    private static partial Regex FailedLine();

    [GeneratedRegex(@"fuse: (\d+) failed, (\d+) passed")]
    private static partial Regex Counts();
}
