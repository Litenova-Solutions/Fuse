namespace Fuse.Protocol;

/// <summary>Which tests to run and how. The client executes it, so a long test run never blocks the engine.</summary>
/// <param name="Runs">One entry per test run (one per target framework for a shadow run, one per project otherwise); empty when no test is affected.</param>
/// <param name="SelectedTests">Test methods selected, counted statically.</param>
/// <param name="TotalTests">Test methods in the repository, counted statically.</param>
/// <param name="Summary">One sentence stating the selection and its reason.</param>
/// <param name="PlanId">Names the content the plan selected from, which a report of its runs names back; empty when the runs are not reported. A line without it reads as empty.</param>
/// <param name="CachedTests">How many selected tests the plan answered from the cache without running them. A line without it reads as 0.</param>
internal sealed record TestPlan(TestRun[] Runs, int SelectedTests, int TotalTests, string Summary, string PlanId = "", int CachedTests = 0);
