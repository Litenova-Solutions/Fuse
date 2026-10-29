using System.Diagnostics;
using Fuse.Dotnet;

namespace Fuse.Evals;

/// <summary>
///     Fills a <see cref="TruthFile"/>: draws the plan when it has none, and runs the real <c>dotnet build</c> or
///     <c>dotnet test</c> for HEAD and for the cases it is asked for that have no answer yet. Fuse plays no part here.
/// </summary>
internal static class TruthRunner
{
    /// <summary>
    ///     Draws the plan if needed, then answers HEAD when <paramref name="head"/> is true and every case in
    ///     <paramref name="cases"/> (every case when null) that has no answer.
    /// </summary>
    /// <param name="origin">Where this machine is, as each answer records it.</param>
    public static async Task FillAsync(TruthFile truth, EvalRepo repo, SolutionInfo solution, bool head, Func<int, bool>? cases, string origin)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the truth side needs a clean tree");

        if (truth.Plan is null)
        {
            Console.WriteLine($"[truth] drawing the {truth.Suite} plan for {repo.Name}...");
            if (truth.Suite == "selection")
                (truth.Plan, truth.Skipped) = await CasePlanner.SelectionAsync(repo, solution, truth.Count, truth.Seed);
            else
                truth.Plan = CasePlanner.Correctness(repo, solution, truth.Count, truth.Seed);
        }

        if (head && truth.Head is null)
        {
            truth.Head = truth.Suite == "selection" ? await SelectionHeadAsync(repo, solution, origin) : await BuildAsync(repo, origin);
            Console.WriteLine($"[truth] HEAD: {truth.Head.Names.Count} name(s), {truth.Head.Total} test(s), {truth.Head.Seconds:0.0} s");
        }

        foreach (var planned in truth.Plan)
        {
            if (truth.Cases.ContainsKey(planned.Index) || cases?.Invoke(planned.Index) == false)
                continue;
            await CasePlanner.ApplyAsync(repo, planned);
            var item = truth.Suite == "selection" ? await SolutionTestAsync(repo, origin) : await BuildAsync(repo, origin);
            await repo.ResetAsync();
            truth.Cases[planned.Index] = item;
            Console.WriteLine($"[truth] case {planned.Index}: {item.Names.Count} name(s), {item.Seconds:0.0} s{(item.BuildFailed ? ", build failed" : "")}  {string.Join(" + ", planned.Edits.Select(e => $"{e.Kind} {e.Path}"))}");
        }

        await repo.ResetAsync();
    }

    private static async Task<TruthItem> BuildAsync(EvalRepo repo, string origin)
    {
        var build = await repo.BuildAsync();
        return new TruthItem(build.Errors, 0, build.Seconds, build.ExitCode != 0, 0, origin);
    }

    /// <summary>
    ///     HEAD's full test run, one test project at a time, so a project that writes no TRX results stops the suite by name:
    ///     a silent gap in the truth side would flatter every selection number.
    /// </summary>
    private static async Task<TruthItem> SelectionHeadAsync(EvalRepo repo, SolutionInfo solution, string origin)
    {
        var build = await repo.BuildAsync();
        if (build.ExitCode != 0)
            throw new InvalidOperationException($"HEAD does not build: {string.Join("; ", build.Errors.Take(3))}");

        var watch = Stopwatch.StartNew();
        var total = TrxResults.Empty;
        var files = 0;
        var withoutResults = new List<string>();
        foreach (var project in solution.TestProjects)
        {
            var results = ResultsDirectory(repo);
            var run = await ProcessRunner.RunAsync("dotnet", TestArguments(repo, project, results), repo.Root, CancellationToken.None);
            var outcome = Outcome(repo, run.Output, results);
            if (outcome is null)
            {
                EvalRepo.ThrowIfLocked(BuildOutputParser.Errors(run.Output, repo.Root), $"dotnet test {Path.GetFileName(project)}");
                var name = Path.GetRelativePath(repo.Root, project).Replace('\\', '/');
                withoutResults.Add(SolutionInfo.IsMicrosoftTestingPlatform(project, repo.Root) ? $"{name} (Microsoft.Testing.Platform runner, no run summary)" : name);
            }
            else
            {
                total = total.Add(outcome);
                files += CountResults(repo, project, run.Output, results);
            }

            Delete(results);
        }

        if (withoutResults.Count > 0)
            throw new InvalidOperationException($"{withoutResults.Count} test project(s) produced no results, so the truth side would be missing them: {string.Join(", ", withoutResults)}");
        return new TruthItem([.. total.Failures.Select(f => f.Name).Distinct()], total.Total, watch.Elapsed.TotalSeconds, false, files, origin);
    }

    /// <summary>
    ///     One <c>dotnet test</c> of the whole solution, as a user runs it after an edit: it builds what changed and runs
    ///     the test projects in parallel. Its time is the <c>dotnet test</c> time the benchmarks quote.
    /// </summary>
    private static async Task<TruthItem> SolutionTestAsync(EvalRepo repo, string origin)
    {
        var results = ResultsDirectory(repo);
        var watch = Stopwatch.StartNew();
        var run = await ProcessRunner.RunAsync("dotnet", TestArguments(repo, repo.BuildTarget, results), repo.Root, CancellationToken.None);
        var seconds = watch.Elapsed.TotalSeconds;
        var errors = BuildOutputParser.Errors(run.Output, repo.Root);
        EvalRepo.ThrowIfLocked(errors, $"dotnet test {repo.BuildTarget}");
        var buildFailed = run.ExitCode != 0 && errors.Count > 0;
        // A run that built but left no results would read as no failures, so it stops the suite instead.
        var outcome = Outcome(repo, run.Output, results)
            ?? (buildFailed ? TrxResults.Empty : throw new InvalidOperationException($"dotnet test {repo.BuildTarget} produced no results, so the truth side would be missing them"));
        var files = CountResults(repo, repo.BuildTarget, run.Output, results);
        Delete(results);
        return new TruthItem([.. outcome.Failures.Select(f => f.Name).Distinct()], outcome.Total, seconds, buildFailed, files, origin);
    }

    /// <summary>
    ///     The <c>dotnet test</c> arguments for <paramref name="target"/>. With a <c>global.json</c> that sets the
    ///     Microsoft.Testing.Platform runner, <c>dotnet test</c> takes the target by option and has no TRX logger, so the
    ///     results come from the run's output instead.
    /// </summary>
    private static string[] TestArguments(EvalRepo repo, string target, string results) =>
        IsPlatformTarget(repo, target)
            ? ["test", target.EndsWith("proj", StringComparison.OrdinalIgnoreCase) ? "--project" : "--solution", target, "--no-restore", "--results-directory", results]
            : ["test", target, "--no-restore", "--logger", "trx;LogFilePrefix=truth", "--results-directory", results, "-nologo", "-tl:off"];

    /// <summary>
    ///     True when <c>dotnet test</c> runs <paramref name="target"/> on Microsoft.Testing.Platform: the runner the
    ///     <c>global.json</c> nearest to the target's own directory names, so a nested layout decides from its own file.
    ///     <paramref name="target"/> is an absolute project path for per-project runs and the relative solution otherwise.
    /// </summary>
    private static bool IsPlatformTarget(EvalRepo repo, string target) =>
        GlobalJson.UsesTestingPlatform(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(repo.Root, target)))!, repo.Root);

    /// <summary>The results a run left as TRX files, or else the ones a Microsoft.Testing.Platform run printed; null when neither.</summary>
    private static TrxResults? Outcome(EvalRepo repo, string output, string results)
    {
        if (TrxReader.ReadDirectory(results, repo.Root) is { } trx)
            return trx;
        ThrowIfSplitSummary(output);
        return TestingPlatformOutput.Read(output, repo.Root);
    }

    /// <summary>
    ///     Stops the suite when a Microsoft.Testing.Platform run printed more than one run summary. The reader takes its
    ///     counts from the last summary but its failure names from the whole output, so a split run would understate the
    ///     total while naming every failure. That shape fails rather than sums because where each section's failures
    ///     print is unconfirmed, and a wrong sum would quietly inflate the selection fractions and the chart's test count.
    /// </summary>
    private static void ThrowIfSplitSummary(string output)
    {
        var summaries = AnsiSequences.Strip(output).Replace("\r", "").Split('\n').Count(l => l.StartsWith("Test run summary:", StringComparison.Ordinal));
        if (summaries > 1)
            throw new InvalidOperationException($"dotnet test printed {summaries} run summaries, so the truth side cannot tell how many tests ran: encode the shape before measuring");
    }

    private static string ResultsDirectory(EvalRepo repo) => Path.Combine(repo.Root, "obj", "fuse-evals", Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    ///     How many result units a run produced: the TRX files it wrote, or, on Microsoft.Testing.Platform, the test
    ///     assemblies it executed, counted from the output that names each.
    /// </summary>
    private static int CountResults(EvalRepo repo, string target, string output, string results) =>
        IsPlatformTarget(repo, target) ? CountExecutions(output) : CountResultFiles(results);

    /// <summary>
    ///     How many test assemblies a Microsoft.Testing.Platform run executed, from its own banner lines. This assumes the
    ///     runner prints one <c>Running tests from</c> line per executed assembly per target framework, passing or failing,
    ///     in per-project and orchestrated runs alike; the product tests show the line for one assembly only, so the first
    ///     Polly truth run should confirm the shape.
    /// </summary>
    private static int CountExecutions(string output) =>
        AnsiSequences.Strip(output).Replace("\r", "").Split('\n').Count(l => l.StartsWith("Running tests from ", StringComparison.Ordinal));

    private static int CountResultFiles(string results) =>
        Directory.Exists(results) ? Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories).Length : 0;

    private static void Delete(string results)
    {
        try
        {
            Directory.Delete(results, recursive: true);
        }
        catch (IOException)
        {
            // DirectoryNotFoundException included: a run that writes no results leaves no directory.
        }
    }
}
