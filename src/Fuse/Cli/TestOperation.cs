using System.Text;
using Fuse.Dotnet;
using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Repo;

namespace Fuse.Cli;

/// <summary>
///     Runs tests and prints only failures. With no arguments it runs the tests affected by the working-tree changes
///     (planned by the engine); with arguments it runs exactly what <c>dotnet test</c> would.
/// </summary>
internal static class TestOperation
{
    private const int MaxFailuresShown = 10;
    private const int MaxNamesShown = 100;

    public static async Task<OperationResult> RunAsync(RepoRoot root, string workingDirectory, IReadOnlyList<string> arguments, bool all, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        if (arguments.Count > 0)
        {
            var (outcome, buildFailure, raw) = await RunDotnetTestAsync(root, workingDirectory, [.. arguments], cancellationToken).ConfigureAwait(false);
            return Render(outcome, buildFailure, raw, root, "ran the tests you selected", Seconds(started));
        }

        var response = await EngineClient.SendAsync(root, new EngineRequest("", RequestKind.TestPlan, AllTests: all), TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        if (response.Status != ResponseStatus.Ok || response.Tests is null)
            return new OperationResult(2, $"fuse: {response.Message ?? "the engine gave no answer"}");
        var plan = response.Tests;
        if (plan.Runs.Length == 0)
            return new OperationResult(0, $"fuse: {plan.Scope}");

        // Shadow runs touch no build output, so they run in parallel. MSBuild runs share obj and bin folders across
        // projects, so they run one after another.
        var groups = plan.Runs.GroupBy(r => r.Project, StringComparer.OrdinalIgnoreCase).ToList();
        var fastGroups = groups.Where(g => g.All(r => r.ShadowAssembly is not null && !r.TestingPlatform)).ToList();
        var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<string, TestOutcome>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            fastGroups,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (group, ct) =>
            {
                // One process per target framework, in parallel.
                var results = await Task.WhenAll(group.Select(run => RunDotnetTestAsync(
                    root, root.Path, [run.ShadowAssembly!, .. (run.Filter is null ? Array.Empty<string>() : ["--filter", run.Filter])], ct, assembly: true))).ConfigureAwait(false);
                // A shadow that could not run (a host or adapter problem) sends the whole project through MSBuild below.
                if (results.Any(r => r.Outcome is null))
                    return;
                outcomes[group.Key] = results.Aggregate(TestOutcome.Empty, (sum, r) => sum.Add(r.Outcome!));
            }).ConfigureAwait(false);

        var aggregate = TestOutcome.Empty;
        OperationResult? failure = null;
        foreach (var group in groups)
        {
            if (outcomes.TryGetValue(group.Key, out var fastOutcome))
            {
                aggregate = aggregate.Add(fastOutcome);
                continue;
            }

            var run = group.First();
            var (outcome, buildFailure, raw) = run.TestingPlatform
                ? await RunDotnetTestAsync(root, root.Path, ["--project", run.Project, "--no-restore"], cancellationToken, testingPlatform: true).ConfigureAwait(false)
                : await RunDotnetTestAsync(root, root.Path, [run.Project, "--no-restore", .. (run.Filter is null ? Array.Empty<string>() : ["--filter", run.Filter])], cancellationToken).ConfigureAwait(false);
            if (outcome is null)
            {
                failure ??= Render(null, buildFailure, raw, root, plan.Scope, Seconds(started));
                continue;
            }

            aggregate = aggregate.Add(outcome);
        }

        if (failure is not null && aggregate.Total == 0)
            return failure;
        var fast = outcomes.Count;
        var mode = fast == groups.Count ? "fast path" : fast > 0 ? $"fast path for {fast} of {groups.Count} project(s)" : "built with MSBuild";
        return Render(aggregate, null, null, root, $"{plan.Scope}; {mode}", Seconds(started));
    }

    private static double Seconds(long started) => (Environment.TickCount64 - started) / 1000.0;

    /// <summary>Runs <c>dotnet test</c> and reads its TRX results.</summary>
    /// <param name="assembly">True when <paramref name="arguments"/> name a test assembly: <c>dotnet test</c> then hands them to VSTest, which rejects MSBuild switches.</param>
    private static async Task<(TestOutcome? Outcome, ProcessResult? BuildFailure, string? Raw)> RunDotnetTestAsync(
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
                return (null, result.ExitCode == 0 ? null : result, result.Output);
            if (outcome is null)
                return (null, result, result.Output);
            return (outcome, null, null);
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

    private static OperationResult Render(TestOutcome? outcome, ProcessResult? buildFailure, string? raw, RepoRoot root, string scope, double seconds)
    {
        if (outcome is null)
        {
            if (buildFailure is not null)
            {
                var build = BuildOperation.Render(buildFailure, root.Path, seconds, "test build");
                return build with { ExitCode = buildFailure.ExitCode == 0 ? 0 : 1 };
            }

            // Microsoft.Testing.Platform run that passed: its console output is the only report.
            return new OperationResult(0, $"fuse: tests passed in {seconds:0.0} s; {scope}");
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
            // Names only past the first few, so the agent still sees every failing test.
            var rest = outcome.Failures.Skip(MaxFailuresShown).Select(f => f.Name).Distinct().ToList();
            text.Append($"also failed ({outcome.Failures.Count - MaxFailuresShown}):\n");
            foreach (var name in rest.Take(MaxNamesShown))
                text.Append("  ").Append(name).Append('\n');
            if (rest.Count > MaxNamesShown)
                text.Append($"  ... and {rest.Count - MaxNamesShown} more\n");
        }
        var skipped = outcome.Skipped > 0 ? $", {outcome.Skipped} skipped" : "";
        text.Append($"fuse: {outcome.Failed} failed, {outcome.Passed} passed{skipped} in {seconds:0.0} s; {scope}");
        return new OperationResult(outcome.Failed > 0 ? 1 : 0, text.ToString());
    }
}
