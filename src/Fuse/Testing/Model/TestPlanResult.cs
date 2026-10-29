namespace Fuse.Testing.Model;

/// <summary>
///     Which tests to run and how. The client runs them, so a long test run never holds the engine's request lock.
/// </summary>
/// <param name="Runs">One entry per test run; empty when no test is affected.</param>
/// <param name="SelectedTests">Test methods selected, counted from the test sources without discovery.</param>
/// <param name="TotalTests">Test methods in the repository, counted the same way.</param>
/// <param name="Summary">One sentence stating what was selected and why. The client prints it in its last line.</param>
internal sealed record TestPlanResult(IReadOnlyList<PlannedRun> Runs, int SelectedTests, int TotalTests, string Summary)
{
    /// <summary>How many selected tests need a container runtime that is not running, so they run in no run above.</summary>
    public int ContainerGatedTests { get; init; }

    /// <summary>The projects those tests are in, in ordinal order.</summary>
    public string[] ContainerGatedProjects { get; init; } = [];
}
