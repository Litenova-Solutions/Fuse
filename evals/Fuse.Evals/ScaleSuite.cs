namespace Fuse.Evals;

/// <summary>
///     What a check of a large tree costs, on the generated <see cref="ScaleFixture"/>: one cold <c>fuse check</c> of
///     every change, its wall time, the files it checked, and the engine's peak and idle memory. The fixture is a linear
///     scale-down of the observed case (about 15,000 files with about 3,700 changed), so the full-scale number is an
///     extrapolation, quoted with the factor, never as measured. Check-only: no truth and no test runs.
/// </summary>
internal static class ScaleSuite
{
    /// <summary>How many files the observed large tree holds, which the scale factor extrapolates to.</summary>
    private const int ObservedFiles = 15000;

    public static async Task<object> RunAsync(EvalRepo repo, SolutionInfo solution, int projects, int files, int changed)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the suite needs a clean tree");
        var build = await repo.BuildAsync();
        if (build.ExitCode != 0)
            throw new InvalidOperationException($"the scale fixture does not build: {string.Join("; ", build.Errors.Take(3))}");

        var edits = ScaleFixture.ChangedFiles(projects, files, changed);
        foreach (var (project, file) in edits)
            await File.WriteAllTextAsync(Path.Combine(repo.Root, ScaleFixture.PartPath(project, file)), ScaleFixture.Part(project, file, 1));
        await Task.Delay(500);

        await repo.KillEngineAsync();
        var logStart = repo.EngineLogLines().Count;
        var check = await repo.FuseTimedAsync("check");
        var counts = CheckCounts.Parse(check.Output, repo.EngineLogLines().Skip(logStart).ToList(), phases: null);
        var engines = await repo.EngineProcessesAsync();
        var peak = engines.Count > 0 ? engines.Max(e => e.PeakWorkingSet) / (1024.0 * 1024.0) : 0;
        // An engine returns the memory its requests freed once idle for 30 s, and looks every 15 s, so 50 s covers both.
        await Task.Delay(TimeSpan.FromSeconds(50));
        var idleEngines = await repo.EngineProcessesAsync();
        var idle = idleEngines.Count > 0 ? idleEngines.Max(e => e.WorkingSet) / (1024.0 * 1024.0) : 0;

        var filesTotal = SourcesUnder(repo.Root).Count;
        var filesChanged = edits.Count;
        // Every changed file's owner loads to bind it; a body edit reaches no dependent, so none of those loads.
        var projectsLoaded = edits.Select(e => solution.ProjectFileOf(Path.Combine(repo.Root, ScaleFixture.PartPath(e.Project, e.File)))).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count();
        // An engine the run killed is gone afterwards; nothing else stops it. A check that never started its engine is
        // unanswered, not out of memory.
        var verdict = check.ExitCode is 0 or 1 ? "answered"
            : check.Output.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ? "out-of-memory"
            : check.ExitCode == 2 && idleEngines.Count == 0 ? "out-of-memory"
            : "unanswered";
        await repo.ResetAsync();
        var summary = new
        {
            suite = "scale",
            repo = repo.Name,
            commit = await repo.HeadAsync(),
            fuseBuild = await repo.VersionAsync(),
            filesTotal,
            filesChanged,
            projectsTotal = solution.Projects.Count,
            projectsLoaded,
            checkMs = check.Milliseconds,
            filesChecked = counts.FilesChecked,
            wholeProjectsBound = counts.WholeProjectsBound,
            enginePeakWorkingSetMb = Math.Round(peak, 1),
            engineIdleWorkingSetMb = Math.Round(idle, 1),
            scaleFactor = Math.Round((double)ObservedFiles / filesTotal, 2),
            checkMsPer1kChanged = filesChanged == 0 ? 0 : Math.Round(check.Milliseconds / (filesChanged / 1000.0), 1),
            verdict,
            treeCleanAfter = await repo.IsCleanAsync(),
        };
        Console.WriteLine($"[scale] {filesTotal} files, {filesChanged} changed: check {check.Milliseconds / 1000:0.0} s, engine peak {peak:0} MB, {counts.FilesChecked?.ToString() ?? "?"} file(s) checked");
        return summary;
    }

    /// <summary>Every C# file under <paramref name="root"/>, excluding build output.</summary>
    private static List<string> SourcesUnder(string root) =>
        [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))];
}
