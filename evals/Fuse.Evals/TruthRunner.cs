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
            var run = await ProcessRunner.RunAsync("dotnet", ["test", project, "--no-restore", "--logger", "trx;LogFilePrefix=truth", "--results-directory", results, "-nologo", "-tl:off"], repo.Root, CancellationToken.None);
            var outcome = TrxReader.ReadDirectory(results, repo.Root);
            if (outcome is null)
            {
                EvalRepo.ThrowIfLocked(BuildOutputParser.Errors(run.Output, repo.Root), $"dotnet test {Path.GetFileName(project)}");
                var name = Path.GetRelativePath(repo.Root, project).Replace('\\', '/');
                withoutResults.Add(SolutionInfo.IsMicrosoftTestingPlatform(project) ? $"{name} (Microsoft.Testing.Platform runner, no VSTest trx)" : name);
            }
            else
            {
                total = total.Add(outcome);
                files += CountResultFiles(results);
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
        var run = await ProcessRunner.RunAsync("dotnet", ["test", repo.BuildTarget, "--no-restore", "--logger", "trx;LogFilePrefix=truth", "--results-directory", results, "-nologo", "-tl:off"], repo.Root, CancellationToken.None);
        var seconds = watch.Elapsed.TotalSeconds;
        var errors = BuildOutputParser.Errors(run.Output, repo.Root);
        EvalRepo.ThrowIfLocked(errors, $"dotnet test {repo.BuildTarget}");
        var outcome = TrxReader.ReadDirectory(results, repo.Root) ?? TrxResults.Empty;
        var files = CountResultFiles(results);
        Delete(results);
        return new TruthItem([.. outcome.Failures.Select(f => f.Name).Distinct()], outcome.Total, seconds, run.ExitCode != 0 && errors.Count > 0, files, origin);
    }

    private static string ResultsDirectory(EvalRepo repo) => Path.Combine(repo.Root, "obj", "fuse-evals", Guid.NewGuid().ToString("N")[..8]);

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
