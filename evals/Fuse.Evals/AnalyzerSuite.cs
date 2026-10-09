using System.Diagnostics;

namespace Fuse.Evals;

/// <summary>
///     What a repository whose analyzer reads the whole project costs Fuse, on the generated <see cref="AnalyzerFixture"/>:
///     whether the engine finds the analyzer compilation-wide, how long the post-edit hook, a check of one file and a check
///     of twenty files take, the engine's peak and idle memory, and that the analyzer's error is still reported by
///     <c>fuse check</c> while the post-edit hook leaves the analyzer out and names it. It runs no tests and needs no truth.
/// </summary>
internal static class AnalyzerSuite
{
    private const int Edits = 5;
    private const int ManyFiles = 20;

    public static async Task<object> RunAsync(EvalRepo repo)
    {
        if (!await repo.IsCleanAsync())
            throw new InvalidOperationException($"{repo.Root} has uncommitted changes; the suite needs a clean tree");
        var build = await repo.BuildAsync();
        if (build.ExitCode != 0)
            throw new InvalidOperationException($"the analyzer fixture does not build: {string.Join("; ", build.Errors.Take(3))}");
        string Full(int i) => Path.Combine(repo.Root, AnalyzerFixture.PartPath(i));
        async Task Edit(int i, int value) => await File.WriteAllTextAsync(Full(i), AnalyzerFixture.Part(i, value));

        await repo.KillEngineAsync();
        await Edit(0, 1);
        var cold = await repo.FuseTimedAsync("check", Full(0));
        Console.WriteLine($"[analyzers] cold check of one file: {cold.Milliseconds:0} ms");

        var hook = new List<double>();
        for (var i = 0; i < Edits; i++)
        {
            await Edit(0, 10 + i);
            hook.Add((await HookAsync(repo, Full(0))).Ms);
        }

        var one = new List<double>();
        for (var i = 0; i < Edits; i++)
        {
            await Edit(0, 20 + i);
            one.Add((await repo.FuseTimedAsync("check", Full(0))).Milliseconds);
        }

        var many = new List<double>();
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < ManyFiles; i++)
                await Edit(i, 30 + round);
            many.Add((await repo.FuseTimedAsync(["check", .. Enumerable.Range(0, ManyFiles).Select(Full)])).Milliseconds);
        }

        // A class the analyzer forbids, with a compiler error in it so the post-edit hook has something to report: the check
        // reports both errors, and the hook reports the compiler error and names the analyzer it left out.
        await File.WriteAllTextAsync(Full(1), AnalyzerFixture.Part(1) + "\npublic sealed class ForbiddenWidget\n{\n    public int Size() => fuseEvalMissingName;\n}\n");
        var hookAnswer = (await HookAsync(repo, Full(1))).Output;
        var checkAnswer = (await repo.FuseTimedAsync("check", Full(1))).Output;
        var reportedByCheck = checkAnswer.Contains(AnalyzerFixture.RuleId, StringComparison.Ordinal) && checkAnswer.Contains("CS0103", StringComparison.Ordinal);
        var leftOutByHook = hookAnswer.Contains("CS0103", StringComparison.Ordinal)
                            && !hookAnswer.Contains(AnalyzerFixture.RuleId, StringComparison.Ordinal)
                            && hookAnswer.Contains(AnalyzerFixture.AnalyzerName, StringComparison.Ordinal);

        var engines = await repo.EngineProcessesAsync();
        var peak = engines.Count > 0 ? engines.Max(e => e.PeakWorkingSet) / (1024.0 * 1024.0) : 0;
        // An engine returns the memory its requests freed once idle for 30 s, and looks every 15 s, so 50 s covers both.
        await Task.Delay(TimeSpan.FromSeconds(50));
        var idleEngines = await repo.EngineProcessesAsync();
        var idle = idleEngines.Count > 0 ? idleEngines.Max(e => e.WorkingSet) / (1024.0 * 1024.0) : 0;
        var plan = repo.EngineLogLines().LastOrDefault(l => l.StartsWith("analyzers of App:", StringComparison.Ordinal));
        await repo.ResetAsync();

        var summary = new
        {
            suite = "analyzers",
            repo = repo.Name,
            commit = await repo.HeadAsync(),
            fuseBuild = await repo.VersionAsync(),
            files = AnalyzerFixture.Files,
            compilationWide = plan?.Contains(AnalyzerFixture.AnalyzerName, StringComparison.Ordinal) ?? false,
            planLine = plan,
            coldCheckMs = cold.Milliseconds,
            postEditHook = LatencyStats.Of(hook),
            checkOneFile = LatencyStats.Of(one),
            checkManyFiles = LatencyStats.Of(many),
            manyFiles = ManyFiles,
            reportedByCheck,
            leftOutByHook,
            enginePeakWorkingSetMb = Math.Round(peak, 1),
            engineIdleWorkingSetMb = Math.Round(idle, 1),
            treeCleanAfter = await repo.IsCleanAsync(),
        };
        Console.WriteLine($"[analyzers] compilation-wide {summary.compilationWide}; post-edit hook P50 {summary.postEditHook.P50:0} ms; check of one file P50 {summary.checkOneFile.P50:0} ms; of {ManyFiles} files P50 {summary.checkManyFiles.P50:0} ms; reported by check {reportedByCheck}, left out by the hook {leftOutByHook}; engine {peak:0} MB at peak, {idle:0} MB once idle");
        return summary;
    }

    /// <summary>Runs the Claude Code post-edit hook for <paramref name="file"/>.</summary>
    private static async Task<(string Output, double Ms)> HookAsync(EvalRepo repo, string file)
    {
        var payload = new Dictionary<string, object>
        {
            ["hook_event_name"] = "PostToolUse",
            ["tool_name"] = "Edit",
            ["cwd"] = repo.Root,
            ["tool_input"] = new Dictionary<string, string> { ["file_path"] = file },
        };
        var watch = Stopwatch.StartNew();
        var result = await repo.FuseHookAsync("claude", "post-edit", System.Text.Json.JsonSerializer.Serialize(payload));
        return (result.Output, watch.Elapsed.TotalMilliseconds);
    }
}
