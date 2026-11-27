using System.Text;
using Fuse.Dotnet.Build;
using Fuse.Dotnet.Processes;
using Fuse.Dotnet.Test;
using Fuse.Operations.Build;
using Fuse.Paths;
using Fuse.Protocol.Testing;
namespace Fuse.Operations.Test.Rendering;

/// <summary>
///     Renders test answers: the failures grouped by their shared first line, the counts and summary line,
///     and the answers for runs that ended without results or need a container runtime.
/// </summary>
internal static class TestResultRenderer
{
    private const int MaxFailuresShown = 10;

    private const int MaxNamesShown = 100;

    /// <summary>How <see cref="WithoutResults"/> names a run of the user's own <c>dotnet test</c> arguments.</summary>
    internal const string ArgumentsRun = "dotnet test";

    /// <summary>Microsoft.Testing.Platform's exit code for a run in which no test ran, such as one whose filter matched none.</summary>
    internal const int NoTestRanExitCode = 8;

    /// <summary>How a timeout reads in an answer: minutes, or seconds when it is under a minute.</summary>
    internal static string TimeoutText(TimeSpan timeout) =>
        timeout.TotalMinutes >= 1 ? $"{timeout.TotalMinutes:0} minutes" : $"{timeout.TotalSeconds:0} seconds";

    /// <summary>One failure as the answer prints it: its name, its message, and its frames.</summary>
    internal static void AppendFailure(StringBuilder text, TestFailure failure)
    {
        text.Append("FAILED ").Append(failure.Name).Append('\n');
        foreach (var line in failure.Message.Split('\n'))
            text.Append("  ").Append(line).Append('\n');
        foreach (var frame in failure.Frames)
            text.Append("  ").Append(frame).Append('\n');
    }

    /// <summary>The answer for runs with results: the failures grouped by their shared first line, then the counts and <paramref name="summary"/>.</summary>
    internal static OperationResult Render(TestRunSummary outcome, string summary, double seconds, string? resultsPath = null)
    {
        var text = new StringBuilder();
        text.Append(RenderFailures(outcome));
        text.Append(RenderSummary(outcome, summary, seconds, resultsPath));
        return new OperationResult(outcome.Failed > 0 ? Outcome.ProblemsFound : Outcome.Clean, text.ToString());
    }

    /// <summary>The failures of <paramref name="outcome"/> as the answer prints them: each group under its shared line, then the names past the detail budget.</summary>
    internal static string RenderFailures(TestRunSummary outcome)
    {
        var text = new StringBuilder();
        var (detailed, restNames, restCount) = FailureGroups.Split(outcome.Failures, MaxFailuresShown);
        foreach (var (header, failure) in detailed)
        {
            if (header is not null)
                text.Append(header).Append('\n');
            AppendFailure(text, failure);
        }

        if (restCount > 0)
        {
            // Names only past the detail budget, each on its own FAILED line, so a script matching FAILED finds every failure.
            text.Append($"also failed ({restCount}):\n");
            foreach (var name in restNames.Take(MaxNamesShown))
                text.Append("  FAILED ").Append(name).Append('\n');
            if (restNames.Count > MaxNamesShown)
                text.Append($"  ... and {restNames.Count - MaxNamesShown} more\n");
        }

        return text.ToString();
    }

    /// <summary>The last line for runs with results: the counts, <paramref name="summary"/>, and the results file when the answer left failures or names out.</summary>
    internal static string RenderSummary(TestRunSummary outcome, string summary, double seconds, string? resultsPath = null)
    {
        var skipped = outcome.Skipped > 0 ? $", {outcome.Skipped} skipped" : "";
        // The file holds every failure; the answer names it only when it left failures or names out.
        var (_, restNames, _) = FailureGroups.Split(outcome.Failures, MaxFailuresShown);
        var namesCapped = restNames.Count > MaxNamesShown;
        var file = resultsPath is not null && (outcome.Failed > Math.Min(MaxFailuresShown, outcome.Failures.Count) || namesCapped)
            ? $"; all {outcome.Failed} in {resultsPath}"
            : "";
        return $"fuse: {outcome.Failed} failed, {outcome.Passed} passed{skipped}{Duration.In(seconds)}; {summary}{file}";
    }

    /// <summary>Several answers as one, in order; it found problems when any of them did.</summary>
    internal static OperationResult Join(List<OperationResult> answers) =>
        answers.Count == 1
            ? answers[0]
            : new OperationResult(
                answers.Any(a => a.Outcome == Outcome.ProblemsFound) ? Outcome.ProblemsFound : Outcome.Clean,
                string.Join('\n', answers.Select(a => a.Text)));

    /// <summary>
    ///     The answer when every selected test needs a container runtime that is not running: no process starts, which
    ///     reads as blocked rather than clean, and the answer names the fix.
    /// </summary>
    internal static OperationResult WithoutRuntime(TestPlan plan) =>
        new(Outcome.ProblemsFound, $"fuse: no test ran: {plan.ContainerGatedTests} test(s) selected in {string.Join(", ", plan.ContainerGatedProjects)} need a container runtime, and none is running; start Docker or Podman and run fuse test again");

    /// <summary>
    ///     The answer for a run without results. A run whose process was killed after writing no output for the test
    ///     inactivity timeout is reported as timed out, never as passed. Error lines in its output mean the project
    ///     did not build, and print as a failed test build. A Microsoft.Testing.Platform run that exited with
    ///     <see cref="NoTestRanExitCode"/> ran no test, which the answer says. Any other run that exited with a code
    ///     other than 0 and printed no error line ended without results Fuse can read, which the answer says after the
    ///     end of its output; a Microsoft.Testing.Platform run that crashed before its run summary ends this way.
    /// </summary>
    /// <param name="result">The <c>dotnet test</c> process, or null for a Microsoft.Testing.Platform run that exited with 0.</param>
    /// <param name="root">The repository, which error paths are printed relative to.</param>
    /// <param name="run">What ran, as the answer names it: "the test run of" a project, or <see cref="ArgumentsRun"/> for the user's arguments.</param>
    /// <param name="summary">What ran and why, for a run that passed.</param>
    /// <param name="seconds">How long the operation has taken.</param>
    /// <param name="log">The build's file log, or empty, so a failed test build shows what a failed command printed.</param>
    /// <param name="timedOutFramework">The target framework a timed-out run hung on, or null when the run covers more than one.</param>
    /// <param name="inactivityTimeout">The timeout the run armed, so the answer names it, or null for the timeout every run uses.</param>
    internal static OperationResult WithoutResults(ProcessResult? result, RepoRoot root, string run, string summary, double seconds, string log = "", string? resultsDirectory = null, string? timedOutFramework = null, TimeSpan? inactivityTimeout = null)
    {
        // A Microsoft.Testing.Platform run that passed without printing a run summary, so there are no counts to show.
        if (result is null)
            return new OperationResult(Outcome.Clean, $"fuse: tests passed{Duration.In(seconds)}; {summary}");
        if (result.TimedOut)
        {
            var tail = BuildOperation.Tail(result.Output);
            var framework = timedOutFramework is null ? "" : $" ({timedOutFramework})";
            var line = $"fuse: {run}{framework} timed out after {TimeoutText(TestOperation.EffectiveTimeout(inactivityTimeout))} without output{Duration.In(seconds)}; the test process was killed";
            return new OperationResult(Outcome.ProblemsFound, tail.Length == 0 ? line : $"{tail}\n{line}");
        }

        if (result.ExitCode == NoTestRanExitCode && BuildOutputParser.Errors(result.Output, root.Path).Count == 0)
        {
            var what = run == ArgumentsRun ? "no test matched the dotnet test arguments" : $"{run} ran no test";
            return new OperationResult(Outcome.ProblemsFound, $"fuse: {what} (exit code {NoTestRanExitCode})");
        }

        if (result.ExitCode != 0 && BuildOutputParser.Errors(result.Output, root.Path).Count == 0)
        {
            var tail = BuildOperation.Tail(result.Output);
            var where = resultsDirectory is null ? "" : $" (no result file in {resultsDirectory})";
            var line = $"fuse: {run} exited with code {result.ExitCode} and produced no results{Duration.In(seconds)}{where}";
            return new OperationResult(Outcome.ProblemsFound, tail.Length == 0 ? line : $"{tail}\n{line}");
        }

        var build = BuildOperation.Render(result, root.Path, seconds, "test build", log);
        return build with { Outcome = result.ExitCode == 0 ? Outcome.Clean : Outcome.ProblemsFound };
    }
}
