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
        // the shadow folder, and the runs below write build output, so all of it has to be one window in this repository.
        using var buildLock = BuildLock.AcquireForClient(root);
        return await RunLockedAsync(root, workingDirectory, arguments, all, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OperationResult> RunLockedAsync(RepoRoot root, string workingDirectory, IReadOnlyList<string> arguments, bool all, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        if (arguments.Count > 0)
        {
            var (outcome, buildFailure) = await RunDotnetTestAsync(root, workingDirectory, [.. arguments], cancellationToken).ConfigureAwait(false);
            return Render(outcome, buildFailure, root, "ran the tests your dotnet test arguments name", Seconds(started));
        }

        EngineRequest request = all ? new EngineRequest.PlanAllTests() : new EngineRequest.PlanAffectedTests();
        var response = await EngineClient.SendAsync(root, request, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        if (response is not EngineResponse.PlanAnswered answered)
            return new OperationResult(Outcome.Unanswered, $"fuse: {(response as EngineResponse.Unanswered)?.Message ?? "the engine gave no answer"}");
        var plan = answered.Plan;
        if (plan.Runs.Length == 0)
            return new OperationResult(Outcome.Clean, $"fuse: {plan.Summary}");

        // Shadow runs touch no build output, so they run in parallel. MSBuild runs share obj and bin folders across
        // projects, so they run one after another.
        var groups = plan.Runs.GroupBy(r => r.Project, StringComparer.OrdinalIgnoreCase).ToList();
        var shadowGroups = groups.Where(g => g.All(r => r.Mode is TestRunMode.Shadow && !r.UsesTestingPlatform)).ToList();
        var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<string, TestOutcome>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            shadowGroups,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (group, ct) =>
            {
                // One process per target framework, in parallel. Every run in a shadow group is a shadow run.
                var results = await Task.WhenAll(group.Select(run => RunDotnetTestAsync(
                    root, root.Path, [((TestRunMode.Shadow)run.Mode).Assembly, .. (run.Filter is null ? Array.Empty<string>() : ["--filter", run.Filter])], ct, assembly: true))).ConfigureAwait(false);
                // A shadow that could not run (a host or adapter problem) sends the whole project through MSBuild below.
                if (results.Any(r => r.Outcome is null))
                    return;
                outcomes[group.Key] = results.Aggregate(TestOutcome.Empty, (sum, r) => sum.Add(r.Outcome!));
            }).ConfigureAwait(false);

        var aggregate = TestOutcome.Empty;
        OperationResult? failure = null;
        foreach (var group in groups)
        {
            if (outcomes.TryGetValue(group.Key, out var shadowOutcome))
            {
                aggregate = aggregate.Add(shadowOutcome);
                continue;
            }

            var run = group.First();
            var (outcome, buildFailure) = run.UsesTestingPlatform
                ? await RunDotnetTestAsync(root, root.Path, ["--project", run.Project, "--no-restore"], cancellationToken, testingPlatform: true).ConfigureAwait(false)
                : await RunDotnetTestAsync(root, root.Path, [run.Project, "--no-restore", .. (run.Filter is null ? Array.Empty<string>() : ["--filter", run.Filter])], cancellationToken).ConfigureAwait(false);
            if (outcome is null)
            {
                failure ??= Render(null, buildFailure, root, plan.Summary, Seconds(started));
                continue;
            }

            aggregate = aggregate.Add(outcome);
        }

        if (failure is not null && aggregate.Total == 0)
            return failure;
        var shadowRuns = outcomes.Count;
        var mode = shadowRuns == groups.Count ? "without MSBuild" : shadowRuns > 0 ? $"without MSBuild for {shadowRuns} of {groups.Count} project(s)" : "built with MSBuild";
        return Render(aggregate, null, root, $"{plan.Summary}; {mode}", Seconds(started));
    }

    private static double Seconds(long started) => (Environment.TickCount64 - started) / 1000.0;

    /// <summary>Runs <c>dotnet test</c> and reads its TRX results.</summary>
    /// <param name="assembly">True when <paramref name="arguments"/> name a test assembly: <c>dotnet test</c> then hands them to VSTest, which rejects MSBuild switches.</param>
    private static async Task<(TestOutcome? Outcome, ProcessResult? BuildFailure)> RunDotnetTestAsync(
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
            var outcome = TrxReader.ReadDirectory(results, root.Path);
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

    private static OperationResult Render(TestOutcome? outcome, ProcessResult? buildFailure, RepoRoot root, string summary, double seconds)
    {
        if (outcome is null)
        {
            if (buildFailure is not null)
            {
                var build = BuildOperation.Render(buildFailure, root.Path, seconds, "test build");
                return build with { Outcome = buildFailure.ExitCode == 0 ? Outcome.Clean : Outcome.ProblemsFound };
            }

            // Microsoft.Testing.Platform run that passed: its console output is the only report.
            return new OperationResult(Outcome.Clean, $"fuse: tests passed in {seconds:0.0} s; {summary}");
        }

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
