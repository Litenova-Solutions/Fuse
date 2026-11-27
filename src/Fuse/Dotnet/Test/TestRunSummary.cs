namespace Fuse.Dotnet.Test;

/// <summary>Counts, failures, and per-test records from one or more VSTest TRX files.</summary>
internal sealed record TestRunSummary(int Passed, int Failed, int Skipped, IReadOnlyList<TestFailure> Failures)
{
    public static TestRunSummary Empty { get; } = new(0, 0, 0, []);

    /// <summary>One record per test the run reported, for the last-run record; empty when the run named no tests.</summary>
    public IReadOnlyList<TestRecord> Records { get; init; } = [];

    public int Total => Passed + Failed + Skipped;

    public TestRunSummary Add(TestRunSummary other) =>
        new(Passed + other.Passed, Failed + other.Failed, Skipped + other.Skipped, [.. Failures, .. other.Failures])
        {
            Records = [.. Records, .. other.Records],
        };
}
