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

        var total = TestOutcome.Empty;
        OperationResult? failure = null;
        var fast = 0;
        foreach (var run in plan.Runs)
        {
            var (outcome, buildFailure, raw) = await RunPlannedAsync(root, run, cancellationToken).ConfigureAwait(false);
            if (run.ShadowAssembly is not null && outcome is not null)
                fast++;
            if (outcome is null)
            {
                failure ??= Render(null, buildFailure, raw, root, plan.Scope, Seconds(started));
                continue;
            }

            total = total.Add(outcome);
        }

        if (failure is not null && total.Total == 0)
            return failure;
        var mode = fast == plan.Runs.Length ? "fast path" : fast > 0 ? "partly fast path" : "built with MSBuild";
        return Render(total, null, null, root, $"{plan.Scope}; {mode}", Seconds(started));
    }

    private static double Seconds(long started) => (Environment.TickCount64 - started) / 1000.0;

    private static async Task<(TestOutcome? Outcome, ProcessResult? BuildFailure, string? Raw)> RunPlannedAsync(RepoRoot root, TestRun run, CancellationToken cancellationToken)
    {
        if (run.TestingPlatform)
            return await RunDotnetTestAsync(root, root.Path, ["--project", run.Project, "--no-restore"], cancellationToken, testingPlatform: true).ConfigureAwait(false);

        var filter = run.Filter is null ? [] : new[] { "--filter", run.Filter };
        if (run.ShadowAssembly is not null)
        {
            var fast = await RunDotnetTestAsync(root, root.Path, [run.ShadowAssembly, .. filter], cancellationToken).ConfigureAwait(false);
            if (fast.Outcome is not null)
                return fast;
            // The shadow could not run (a host or adapter problem); a normal build and run is always correct.
        }

        return await RunDotnetTestAsync(root, root.Path, [run.Project, "--no-restore", .. filter], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(TestOutcome? Outcome, ProcessResult? BuildFailure, string? Raw)> RunDotnetTestAsync(
        RepoRoot root, string workingDirectory, string[] arguments, CancellationToken cancellationToken, bool testingPlatform = false)
    {
        var results = Path.Combine(root.StateDirectory, "results", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string[] reporting = testingPlatform
                ? []
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
            text.Append($"... {outcome.Failures.Count - MaxFailuresShown} more failure(s)\n");
        var skipped = outcome.Skipped > 0 ? $", {outcome.Skipped} skipped" : "";
        text.Append($"fuse: {outcome.Failed} failed, {outcome.Passed} passed{skipped} in {seconds:0.0} s; {scope}");
        return new OperationResult(outcome.Failed > 0 ? 1 : 0, text.ToString());
    }
}
