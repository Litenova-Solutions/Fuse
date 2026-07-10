namespace Fuse.Workspace;

/// <summary>
///     The result of a covering-test run (T1): the per-test verdicts, whether the run timed out, and whether the
///     covering set was empty (so the caller reports "nothing to run" rather than an unfiltered whole-suite run).
/// </summary>
/// <param name="Verdicts">The per-test verdicts parsed from the run.</param>
/// <param name="TimedOut">Whether the run exceeded its timeout and was killed.</param>
/// <param name="RanNothing">Whether the covering set was empty, so no test command was issued.</param>
/// <param name="Diagnostics">A short diagnostic line when the runner itself failed (nonzero exit, no results).</param>
public sealed record TestRunResult(
    IReadOnlyList<TestVerdict> Verdicts, bool TimedOut, bool RanNothing, string? Diagnostics);

/// <summary>
///     Runs the covering subset of tests at build grade (T1's pre-agreed default and floor): <c>dotnet test</c>
///     scoped to the covering set with <c>--filter</c>, under a hard timeout, with verdicts read from the emitted
///     TRX. This is framework-agnostic (the target's own test framework and adapter run the tests, via the real
///     build), so it works on any repo; the emit-and-micro-host fast path is the optimization layered on top.
/// </summary>
/// <remarks>
///     An empty covering set never runs: an empty <c>--filter</c> would run the whole suite, so the runner reports
///     <see cref="TestRunResult.RanNothing" /> instead. This path runs the real build (MSBuild), so it is the
///     build-grade rung; the caller stamps the grade.
/// </remarks>
public static class BuildGradeTestRunner
{
    /// <summary>
    ///     Runs the covering tests for a target under a timeout and returns the per-test verdicts.
    /// </summary>
    /// <param name="target">The solution or project to test.</param>
    /// <param name="filterExpression">
    ///     The runner filter selecting the covering subset (built by <see cref="TestFilterBuilder" />). An empty
    ///     string is treated as "run nothing", never an unfiltered whole-suite run.
    /// </param>
    /// <param name="resultsDirectory">A scratch directory the TRX log is written to (created if absent).</param>
    /// <param name="timeout">The hard timeout for the run.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>The run result: verdicts, timeout and ran-nothing flags, and a diagnostic when the runner failed.</returns>
    public static async Task<TestRunResult> RunAsync(
        string target,
        string filterExpression,
        string resultsDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(filterExpression))
            return new TestRunResult([], TimedOut: false, RanNothing: true, Diagnostics: null);

        var filter = filterExpression;
        Directory.CreateDirectory(resultsDirectory);
        const string trxName = "fuse-covering.trx";

        var result = await TimedProcess.RunAsync(
            "dotnet",
            [
                "test", target,
                "--filter", filter,
                "--logger", "trx;LogFileName=" + trxName,
                "--results-directory", resultsDirectory,
                "--nologo",
            ],
            workingDirectory: null,
            environment: null,
            timeout,
            cancellationToken);

        if (result.TimedOut)
            return new TestRunResult([], TimedOut: true, RanNothing: false, Diagnostics: null);

        var trxPath = Path.Combine(resultsDirectory, trxName);
        if (!File.Exists(trxPath))
        {
            // No TRX means the run did not produce results (a build failure, or no matching test). Report the exit
            // as a diagnostic rather than a silent green.
            return new TestRunResult([], TimedOut: false, RanNothing: false,
                Diagnostics: $"no test results produced (dotnet test exit {result.ExitCode?.ToString() ?? "unknown"}).");
        }

        var verdicts = TrxResultParser.Parse(await File.ReadAllTextAsync(trxPath, cancellationToken));
        return new TestRunResult(verdicts, TimedOut: false, RanNothing: false, Diagnostics: null);
    }
}
