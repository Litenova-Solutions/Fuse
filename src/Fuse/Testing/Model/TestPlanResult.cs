namespace Fuse.Testing.Model;

/// <summary>
///     Which tests to run and how. The client runs them, so a long test run never holds the engine's request lock.
/// </summary>
/// <param name="Runs">One entry per <c>dotnet test</c> process; empty when no test is affected.</param>
/// <param name="SelectedTests">Test methods selected, counted from the test sources without discovery.</param>
/// <param name="TotalTests">Test methods in the repository, counted the same way.</param>
/// <param name="Summary">One sentence stating what was selected and why. The client prints it at the end of its last line.</param>
internal sealed record TestPlanResult(IReadOnlyList<PlannedRun> Runs, int SelectedTests, int TotalTests, string Summary);
