namespace Fuse.Protocol;

/// <summary>Which tests to run and how. The client executes it, so a long test run never blocks the engine.</summary>
/// <param name="Runs">One entry per test run (one per target framework for a shadow run, one per project otherwise); empty when no test is affected.</param>
/// <param name="SelectedTests">Test methods selected, counted statically.</param>
/// <param name="TotalTests">Test methods in the repository, counted statically.</param>
/// <param name="Summary">One sentence stating the selection and its reason.</param>
internal sealed record TestPlan(TestRun[] Runs, int SelectedTests, int TotalTests, string Summary);
