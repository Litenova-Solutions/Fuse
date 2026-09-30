using System.Text;
using Fuse.Dotnet;
using Fuse.Engine.Client;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>
///     Runs tests and prints failures. With no arguments it runs the tests affected by the working-tree changes
///     (planned by the engine); with arguments it passes them to <c>dotnet test</c> and adds result reporting.
/// </summary>
internal static class TestOperation
{
    private const int MaxFailuresShown = 10;
    private const int MaxNamesShown = 100;

    public static async Task<OperationResult> RunAsync(RepoRoot root, string workingDirectory, IReadOnlyList<string> arguments, bool all, CancellationToken cancellationToken)
    {
        // Held from before the plan request until the last child has exited: the plan makes the engine mirror and emit into
        // the shadow folder, and the runs below write build output, so no other build or test in this repository may run in between.
        using var buildLock = BuildLock.AcquireForClient(root);
        return await RunLockedAsync(root, workingDirectory, arguments, all, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OperationResult> RunLockedAsync(RepoRoot root, string workingDirectory, IReadOnlyList<string> arguments, bool all, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        if (arguments.Count > 0)
        {
            // On Microsoft.Testing.Platform the arguments go to the test application, which rejects VSTest's logger options.
            var testingPlatform = GlobalJson.UsesTestingPlatform(workingDirectory, root.Path);
            var (outcome, process) = await RunDotnetTestAsync(root, workingDirectory, [.. arguments], cancellationToken, testingPlatform).ConfigureAwait(false);
            const string summary = "ran the tests your dotnet test arguments name";
            return outcome is null
                ? WithoutResults(process, root, "dotnet test", summary, Seconds(started))
                : Render(outcome, summary, Seconds(started));
        }

        EngineRequest request = all ? new EngineRequest.PlanAllTests() : new EngineRequest.PlanAffectedTests();
        var response = await EngineClient.SendAsync(root, request, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        if (response is not EngineResponse.PlanAnswered answered)
            return OperationResult.Unanswered(response, "test plan");
        var plan = answered.Plan;
        if (plan.Runs.Length == 0)
            return new OperationResult(Outcome.Clean, $"fuse: {plan.Summary}");
        return await RunPlanAsync(root, plan, started, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs every run of <paramref name="plan"/> and answers for all of them together.</summary>
    /// <param name="root">The repository the plan is for.</param>
    /// <param name="plan">The engine's plan, with at least one run.</param>
    /// <param name="started">When the operation started, in <see cref="Environment.TickCount64"/> milliseconds, for the times the answer prints.</param>
    /// <param name="cancellationToken">Kills the running <c>dotnet test</c> processes.</param>
    internal static async Task<OperationResult> RunPlanAsync(RepoRoot root, TestPlan plan, long started, CancellationToken cancellationToken)
    {
        // Shadow runs touch no build output, so they run in parallel. MSBuild runs share obj and bin folders across
        // projects, so they run one after another.
        var groups = plan.Runs.GroupBy(r => root.PathOf(r.Project)).ToList();
        var shadowGroups = groups.Where(g => g.All(r => r.Mode is TestRunMode.Shadow && !r.UsesTestingPlatform)).ToList();
        var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<RepoPath, TrxResults>();
        await Parallel.ForEachAsync(
            shadowGroups,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (group, ct) =>
            {
                // One process per target framework, in parallel. Every run in a shadow group is a shadow run.
                var results = await Task.WhenAll(group.Select(run => RunDotnetTestAsync(
                    root, root.Path, [((TestRunMode.Shadow)run.Mode).Assembly, .. (run.Filter is null ? Array.Empty<string>() : ["--filter", run.Filter])], ct, assembly: true))).ConfigureAwait(false);
                // A shadow run that could not start (a test host or adapter problem) sends the whole project through MSBuild below.
                if (results.Any(r => r.Outcome is null))
                    return;
                outcomes[group.Key] = results.Aggregate(TrxResults.Empty, (sum, r) => sum.Add(r.Outcome!));
            }).ConfigureAwait(false);

        var aggregate = TrxResults.Empty;
        // A project that did not build, or whose run ended without results, is part of the answer whatever the other
        // projects did. A Microsoft.Testing.Platform run that passed without printing a run summary is kept apart: it has
        // no counts to add.
        var problems = new List<OperationResult>();
        OperationResult? passedWithoutResults = null;
        foreach (var group in groups)
        {
            if (outcomes.TryGetValue(group.Key, out var shadowOutcome))
            {
                aggregate = aggregate.Add(shadowOutcome);
                continue;
            }

            var run = group.First();
            var (outcome, process) = run.UsesTestingPlatform
                ? await RunDotnetTestAsync(root, root.Path, ["--project", run.Project, "--no-restore"], cancellationToken, testingPlatform: true).ConfigureAwait(false)
                : await RunDotnetTestAsync(root, root.Path, [run.Project, "--no-restore", .. (run.Filter is null ? Array.Empty<string>() : ["--filter", run.Filter])], cancellationToken).ConfigureAwait(false);
            if (outcome is not null)
            {
                aggregate = aggregate.Add(outcome);
                continue;
            }

            var answer = WithoutResults(process, root, $"the test run of {run.Name}", plan.Summary, Seconds(started));
            if (answer.Outcome == Outcome.Clean)
                passedWithoutResults ??= answer;
            else
                problems.Add(answer);
        }

        if (aggregate.Total == 0 && problems.Count == 0 && passedWithoutResults is not null)
            return passedWithoutResults;
        if (aggregate.Total == 0 && problems.Count > 0)
            return Join(problems);

        var shadowRuns = outcomes.Count;
        var mode = shadowRuns == groups.Count ? "without MSBuild" : shadowRuns > 0 ? $"without MSBuild for {shadowRuns} of {groups.Count} project(s)" : "built with MSBuild";
        var tests = Render(aggregate, $"{plan.Summary}; {mode}", Seconds(started));
        // The tests' answer comes first, so the last line is a failed build or run whenever there is one.
        return problems.Count == 0 ? tests : Join([tests, .. problems]);
    }

    /// <summary>Several answers as one, in order; it found problems when any of them did.</summary>
    private static OperationResult Join(List<OperationResult> answers) =>
        answers.Count == 1
            ? answers[0]
            : new OperationResult(
                answers.Any(a => a.Outcome == Outcome.ProblemsFound) ? Outcome.ProblemsFound : Outcome.Clean,
                string.Join('\n', answers.Select(a => a.Text)));

    private static double Seconds(long started) => (Environment.TickCount64 - started) / 1000.0;

    /// <summary>
    ///     Runs <c>dotnet test</c> and reads its results: from its TRX files, or for a Microsoft.Testing.Platform run from
    ///     the summary and failures it prints. Without results it returns the process, which is null for a
    ///     Microsoft.Testing.Platform run that exited with 0.
    /// </summary>
    /// <param name="assembly">True when <paramref name="arguments"/> name a test assembly: <c>dotnet test</c> then hands them to VSTest, which rejects MSBuild switches.</param>
    private static async Task<(TrxResults? Outcome, ProcessResult? Process)> RunDotnetTestAsync(
        RepoRoot root, string workingDirectory, string[] arguments, CancellationToken cancellationToken, bool testingPlatform = false, bool assembly = false)
    {
        var results = Path.Combine(root.StateDirectory, "results", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string[] reporting = testingPlatform
                ? []
                : assembly
                    ? ["--logger", "trx;LogFilePrefix=fuse", "--results-directory", results]
                    : ["--logger", "trx;LogFilePrefix=fuse", "--results-directory", results, "-nologo", "-tl:off"];
            var result = await ProcessRunner.RunAsync("dotnet", ["test", .. arguments, .. reporting], workingDirectory, cancellationToken).ConfigureAwait(false);
            var outcome = TrxReader.ReadDirectory(results, root.Path) ?? (testingPlatform ? TestingPlatformOutput.Read(result.Output, root.Path) : null);
            // Counts that say nothing failed from a run that exited with a code other than 0 do not explain its exit code.
            if (testingPlatform && outcome is { Failed: 0 } && result.ExitCode != 0)
                outcome = null;
            if (outcome is null && testingPlatform)
                return (null, result.ExitCode == 0 ? null : result);
            if (outcome is null)
                return (null, result);
            return (outcome, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(results))
                    Directory.Delete(results, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    ///     The answer for a run without results. Error lines in its output mean the project did not build, and print as a
    ///     failed test build. A run that exited with a code other than 0 and printed no error line ended without results
    ///     Fuse can read, which the answer says after the end of its output; a Microsoft.Testing.Platform run that crashed
    ///     before its run summary ends this way.
    /// </summary>
    /// <param name="result">The <c>dotnet test</c> process, or null for a Microsoft.Testing.Platform run that exited with 0.</param>
    /// <param name="root">The repository, which error paths are printed relative to.</param>
    /// <param name="run">What ran, as the answer names it: "the test run of" a project, or "dotnet test" for the user's arguments.</param>
    /// <param name="summary">What ran and why, for a run that passed.</param>
    /// <param name="seconds">How long the operation has taken.</param>
    internal static OperationResult WithoutResults(ProcessResult? result, RepoRoot root, string run, string summary, double seconds)
    {
        // A Microsoft.Testing.Platform run that passed without printing a run summary, so there are no counts to show.
        if (result is null)
            return new OperationResult(Outcome.Clean, $"fuse: tests passed in {seconds:0.0} s; {summary}");
        if (result.ExitCode != 0 && BuildOutputParser.Errors(result.Output, root.Path).Count == 0)
        {
            var tail = BuildOperation.Tail(result.Output);
            var line = $"fuse: {run} exited with code {result.ExitCode} and produced no results in {seconds:0.0} s";
            return new OperationResult(Outcome.ProblemsFound, tail.Length == 0 ? line : $"{tail}\n{line}");
        }

        var build = BuildOperation.Render(result, root.Path, seconds, "test build");
        return build with { Outcome = result.ExitCode == 0 ? Outcome.Clean : Outcome.ProblemsFound };
    }

    /// <summary>The answer for runs that wrote TRX results: the failures, then the counts and <paramref name="summary"/>.</summary>
    private static OperationResult Render(TrxResults outcome, string summary, double seconds)
    {
        var text = new StringBuilder();
        foreach (var failure in outcome.Failures.Take(MaxFailuresShown))
        {
            text.Append("FAILED ").Append(failure.Name).Append('\n');
            foreach (var line in failure.Message.Split('\n'))
                text.Append("  ").Append(line).Append('\n');
            foreach (var frame in failure.Frames)
                text.Append("  ").Append(frame).Append('\n');
        }

        if (outcome.Failures.Count > MaxFailuresShown)
        {
            // Names only past the first ten, so the agent sees up to 110 failing tests by name.
            var rest = outcome.Failures.Skip(MaxFailuresShown).Select(f => f.Name).Distinct().ToList();
            text.Append($"also failed ({outcome.Failures.Count - MaxFailuresShown}):\n");
            foreach (var name in rest.Take(MaxNamesShown))
                text.Append("  ").Append(name).Append('\n');
            if (rest.Count > MaxNamesShown)
                text.Append($"  ... and {rest.Count - MaxNamesShown} more\n");
        }
        var skipped = outcome.Skipped > 0 ? $", {outcome.Skipped} skipped" : "";
        text.Append($"fuse: {outcome.Failed} failed, {outcome.Passed} passed{skipped} in {seconds:0.0} s; {summary}");
        return new OperationResult(outcome.Failed > 0 ? Outcome.ProblemsFound : Outcome.Clean, text.ToString());
    }
}
